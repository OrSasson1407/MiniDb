namespace MiniDb;

/// <summary>Base class for every error the MiniDb engine raises on purpose.</summary>
public class MiniDbException : Exception
{
    public MiniDbException(string message) : base(message)
    {
    }
}
