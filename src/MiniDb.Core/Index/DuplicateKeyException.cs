namespace MiniDb.Index;

/// <summary>Raised when a key that is already in the tree is inserted again.</summary>
public sealed class DuplicateKeyException : MiniDbException
{
    public DuplicateKeyException(long key)
        : base($"Key {key} already exists.")
    {
    }
}
