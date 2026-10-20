namespace MiniDb.Query;

/// <summary>Raised when SQL text cannot be tokenized or parsed. <see cref="Position"/> is the character offset of the problem.</summary>
public sealed class SqlSyntaxException : MiniDbException
{
    public SqlSyntaxException(string message, int position)
        : base($"{message} at position {position}.")
    {
        Position = position;
    }

    public int Position { get; }
}
