using MiniDb.Records;

namespace MiniDb.Query;

/// <summary>How values compare: a column with a literal in a WHERE clause, two cells when sorting, two values in MIN and MAX.</summary>
internal static class Comparison
{
    /// <summary>True when the row's column satisfies the condition. Reads only that column.</summary>
    public static bool Matches(RowView row, ColumnRef column, Condition condition)
    {
        int order = CompareWithLiteral(row, column, condition.Value);
        return condition.Op switch
        {
            CompareOp.Equal => order == 0,
            CompareOp.Less => order < 0,
            CompareOp.LessOrEqual => order <= 0,
            CompareOp.Greater => order > 0,
            CompareOp.GreaterOrEqual => order >= 0,
            _ => false,
        };
    }

    /// <summary>Orders two cells of the same column without boxing them.</summary>
    public static int CompareCells(RowView left, RowView right, ColumnRef column) => column.Type switch
    {
        ColumnType.Int32 => left.GetInt32(column.Column).CompareTo(right.GetInt32(column.Column)),
        ColumnType.Int64 => left.GetInt64(column.Column).CompareTo(right.GetInt64(column.Column)),
        ColumnType.Double => left.GetDouble(column.Column).CompareTo(right.GetDouble(column.Column)),
        ColumnType.Bool => left.GetBool(column.Column).CompareTo(right.GetBool(column.Column)),
        ColumnType.String => string.CompareOrdinal(left.GetString(column.Column), right.GetString(column.Column)),
        _ => throw new InvalidOperationException("Unknown column type."),
    };

    /// <summary>Orders two boxed values of the same type, as read from one column.</summary>
    public static int CompareValues(object left, object right) => (left, right) switch
    {
        (int a, int b) => a.CompareTo(b),
        (long a, long b) => a.CompareTo(b),
        (double a, double b) => a.CompareTo(b),
        (bool a, bool b) => a.CompareTo(b),
        (string a, string b) => string.CompareOrdinal(a, b),
        _ => throw new MiniDbException("Values of different types cannot be ordered."),
    };

    /// <summary>Numbers compare across integer and decimal types; other combinations must match exactly.</summary>
    private static int CompareWithLiteral(RowView row, ColumnRef column, object literal) =>
        (column.Type, literal) switch
        {
            (ColumnType.Int32, long l) => ((long)row.GetInt32(column.Column)).CompareTo(l),
            (ColumnType.Int32, double d) => ((double)row.GetInt32(column.Column)).CompareTo(d),
            (ColumnType.Int64, long l) => row.GetInt64(column.Column).CompareTo(l),
            (ColumnType.Int64, double d) => ((double)row.GetInt64(column.Column)).CompareTo(d),
            (ColumnType.Double, long l) => row.GetDouble(column.Column).CompareTo((double)l),
            (ColumnType.Double, double d) => row.GetDouble(column.Column).CompareTo(d),
            (ColumnType.Bool, bool b) => row.GetBool(column.Column).CompareTo(b),
            (ColumnType.String, string s) => string.CompareOrdinal(row.GetString(column.Column), s),
            _ => throw new MiniDbException(
                $"Cannot compare column '{column.Name}' ({column.Type}) with {Describe(literal)}."),
        };

    private static string Describe(object value) => value switch
    {
        long or int => "an integer",
        double => "a decimal number",
        string => "text",
        bool => "a boolean",
        _ => value.GetType().Name,
    };
}
