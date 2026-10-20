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
    }

    public string Name { get; }

    public IReadOnlyList<ColumnDefinition> Columns { get; }

    public int PrimaryKeyIndex { get; }

    public ColumnDefinition PrimaryKey => Columns[PrimaryKeyIndex];
}
