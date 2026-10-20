using MiniDb.Records;

namespace MiniDb.Query;

/// <summary>
/// GROUP BY and aggregate functions, computed in one pass with a hash table keyed by the grouping values. Each group
/// keeps one small accumulator per aggregate, so memory grows with the number of groups, not rows.
/// Without GROUP BY the whole input is one group, which exists even when no row matches (COUNT gives 0).
/// </summary>
internal static class Aggregation
{
    /// <summary>Where each SELECT item's value comes from: a grouping column (by position) or an accumulator.</summary>
    private readonly record struct Slot(int KeyIndex, AggregateKind? Kind, ColumnRef? Column);

    private sealed class Group
    {
        public Group(object?[] key, Accumulator?[] accumulators)
        {
            Key = key;
            Accumulators = accumulators;
        }

        public object?[] Key { get; }

        public Accumulator?[] Accumulators { get; }
    }

    public static List<object?[]> Run(
        IEnumerable<RowView[]> rows,
        ColumnScope scope,
        IReadOnlyList<SelectItem> items,
        IReadOnlyList<ColumnRef> groupBy)
    {
        Slot[] slots = items.Select(item => Describe(item, scope, groupBy)).ToArray();
        var index = new Dictionary<object?[], Group>(KeyComparer.Instance);
        var groups = new List<Group>();
        foreach (RowView[] tuple in rows)
        {
            object?[] key = groupBy.Select(column => tuple[column.Table][column.Column]).ToArray();
            if (!index.TryGetValue(key, out Group? group))
            {
                group = NewGroup(key, slots);
                index.Add(key, group);
                groups.Add(group);
            }

            foreach (Accumulator? accumulator in group.Accumulators)
            {
                accumulator?.Add(tuple);
            }
        }

        if (groupBy.Count == 0 && groups.Count == 0)
        {
            groups.Add(NewGroup(Array.Empty<object?>(), slots));
        }

        return groups.Select(group => Output(group, slots)).ToList();
    }

    private static Group NewGroup(object?[] key, Slot[] slots) =>
        new(key, slots.Select(slot => slot.Kind is { } kind ? new Accumulator(kind, slot.Column) : null).ToArray());

    private static object?[] Output(Group group, Slot[] slots)
    {
        var row = new object?[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            row[i] = slots[i].Kind is null ? group.Key[slots[i].KeyIndex] : group.Accumulators[i]!.Result();
        }

        return row;
    }

    private static Slot Describe(SelectItem item, ColumnScope scope, IReadOnlyList<ColumnRef> groupBy)
    {
        switch (item)
        {
            case ColumnItem column:
            {
                ColumnRef resolved = scope.Resolve(column.Column);
                int position = groupBy.ToList().FindIndex(g => g.Table == resolved.Table && g.Column == resolved.Column);
                if (position < 0)
                {
                    throw new MiniDbException($"Column '{column.Column}' must appear in GROUP BY or inside an aggregate.");
                }

                return new Slot(position, null, null);
            }

            case AggregateItem aggregate:
            {
                ColumnRef? column = aggregate.Column is null ? null : scope.Resolve(aggregate.Column);
                if (aggregate.Kind is AggregateKind.Sum or AggregateKind.Avg && column is { Type: not (ColumnType.Int32 or ColumnType.Int64 or ColumnType.Double) })
                {
                    throw new MiniDbException($"{aggregate.Kind.ToString().ToUpperInvariant()} needs a numeric column, and '{aggregate.Column}' is not one.");
                }

                if (aggregate.Kind is not AggregateKind.Count && column is null)
                {
                    throw new MiniDbException($"{aggregate.Kind.ToString().ToUpperInvariant()} needs a column.");
                }

                return new Slot(-1, aggregate.Kind, column);
            }

            default:
                throw new InvalidOperationException("Unknown select item.");
        }
    }

    /// <summary>Keeps one aggregate's running state for one group.</summary>
    private sealed class Accumulator
    {
        private readonly AggregateKind _kind;
        private readonly ColumnRef? _column;
        private readonly bool _isReal;
        private long _count;
        private long _sumInteger;
        private double _sumReal;
        private object? _best;

        public Accumulator(AggregateKind kind, ColumnRef? column)
        {
            _kind = kind;
            _column = column;
            _isReal = column is { Type: ColumnType.Double };
        }

        public void Add(RowView[] tuple)
        {
            _count++;
            if (_kind == AggregateKind.Count)
            {
                return;
            }

            ColumnRef column = _column!.Value;
            RowView row = tuple[column.Table];
            switch (_kind)
            {
                case AggregateKind.Sum or AggregateKind.Avg when _isReal:
                    _sumReal += row.GetDouble(column.Column);
                    break;
                case AggregateKind.Sum or AggregateKind.Avg:
                    _sumInteger += column.Type == ColumnType.Int32 ? row.GetInt32(column.Column) : row.GetInt64(column.Column);
                    break;
                default:
                    object value = row[column.Column]!;
                    if (_best is null || (_kind == AggregateKind.Min ? Comparison.CompareValues(value, _best) < 0 : Comparison.CompareValues(value, _best) > 0))
                    {
                        _best = value;
                    }

                    break;
            }
        }

        public object? Result() => _kind switch
        {
            AggregateKind.Count => _count,
            AggregateKind.Sum when _count == 0 => null,
            // Both branches are boxed on their own: a plain conditional would promote the long to double.
            AggregateKind.Sum => _isReal ? (object)_sumReal : _sumInteger,
            AggregateKind.Avg when _count == 0 => null,
            AggregateKind.Avg => (_isReal ? _sumReal : _sumInteger) / (double)_count,
            _ => _best,
        };
    }

    /// <summary>Groups compare by value, so two rows with the same grouping values share one group.</summary>
    private sealed class KeyComparer : IEqualityComparer<object?[]>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals(object?[]? x, object?[]? y)
        {
            if (x is null || y is null || x.Length != y.Length)
            {
                return false;
            }

            for (int i = 0; i < x.Length; i++)
            {
                if (!Equals(x[i], y[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(object?[] key)
        {
            var hash = new HashCode();
            foreach (object? value in key)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}
