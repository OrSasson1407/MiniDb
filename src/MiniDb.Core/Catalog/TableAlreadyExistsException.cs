namespace MiniDb.Catalog;

/// <summary>Raised when a table with the same name is already in the catalog.</summary>
public sealed class TableAlreadyExistsException : MiniDbException
{
    public TableAlreadyExistsException(string name)
        : base($"Table '{name}' already exists.")
    {
    }
}
