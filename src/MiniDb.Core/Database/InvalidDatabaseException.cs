namespace MiniDb.Database;

/// <summary>Raised when a file is not a MiniDb database or its header is damaged.</summary>
public sealed class InvalidDatabaseException : MiniDbException
{
    public InvalidDatabaseException(string reason)
        : base($"Invalid MiniDb database: {reason}.")
    {
    }
}
