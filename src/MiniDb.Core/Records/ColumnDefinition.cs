namespace MiniDb.Records;

/// <summary>One column of a table.</summary>
public sealed class ColumnDefinition
{
    /// <param name="name">Column name, unique within its table.</param>
    /// <param name="type">Stored type of the column.</param>
    /// <param name="maxLength">Maximum length in characters for a String column; 0 for every other type.</param>
    public ColumnDefinition(string name, ColumnType type, int maxLength = 0)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Column name cannot be empty.", nameof(name));
        }

        if (type == ColumnType.String && maxLength <= 0)
        {
            throw new ArgumentException($"String column '{name}' needs a positive MaxLength.", nameof(maxLength));
        }

        Name = name;
        Type = type;
        MaxLength = maxLength;
    }

    public string Name { get; }

    public ColumnType Type { get; }

    public int MaxLength { get; }
}
