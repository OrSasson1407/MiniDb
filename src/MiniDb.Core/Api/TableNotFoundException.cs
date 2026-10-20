namespace MiniDb.Api;

/// <summary>Raised when a table name is not in the catalog.</summary>
public sealed class TableNotFoundException : MiniDbException
{
    public TableNotFoundException(string name)
        : base($"Table '{name}' does not exist.")
    {
    }
}
