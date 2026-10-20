namespace MiniDb.Records;

/// <summary>Name, columns and primary key of a table. The primary key must be an Int64 column.</summary>
public sealed class TableSchema
{
    public TableSchema(string name, IReadOnlyList<ColumnDefinition> columns, int primaryKeyIndex)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Table name cannot be empty.", nameof(name));
        }

        if (columns.Count == 0)
        {
            throw new ArgumentException("A table needs at least one column.", nameof(columns));
        }

        if (primaryKeyIndex < 0 || primaryKeyIndex >= columns.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(primaryKeyIndex));
        }

        if (columns[primaryKeyIndex].Type != ColumnType.Int64)
        {
            throw new ArgumentException("The primary key must be an Int64 column.", nameof(primaryKeyIndex));
        }

        Name = name;
        Columns = columns;
        PrimaryKeyIndex = primaryKeyIndex;
        (FixedOffsets, FixedSize) = ComputeFixedLayout(columns);
    }

    public string Name { get; }

    public IReadOnlyList<ColumnDefinition> Columns { get; }

    public int PrimaryKeyIndex { get; }

    public ColumnDefinition PrimaryKey => Columns[PrimaryKeyIndex];

    /// <summary>Byte offset of each fixed-width column inside a record, or -1 for String columns.</summary>
    public int[] FixedOffsets { get; }

    /// <summary>Size of the fixed-width block at the start of a record. String columns follow it.</summary>
    public int FixedSize { get; }

    private static (int[] Offsets, int Size) ComputeFixedLayout(IReadOnlyList<ColumnDefinition> columns)
    {
        var offsets = new int[columns.Count];
        int size = 0;
        for (int i = 0; i < columns.Count; i++)
        {
            ColumnType type = columns[i].Type;
            offsets[i] = type == ColumnType.String ? -1 : size;
            size += RecordCodec.FixedWidthOf(type);
        }

        return (offsets, size);
    }
}
