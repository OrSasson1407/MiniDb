namespace MiniDb.Index;

/// <summary>
/// Key of a B+ tree entry: a major value and a minor value that breaks ties between equal majors.
/// A plain primary key uses Minor = 0. A secondary index uses the indexed value as Major and the row's
/// primary key as Minor, so every entry is unique even when many rows share a value.
/// </summary>
public readonly record struct TreeKey(long Major, long Minor = 0) : IComparable<TreeKey>
{
    public int CompareTo(TreeKey other)
    {
        int byMajor = Major.CompareTo(other.Major);
        return byMajor != 0 ? byMajor : Minor.CompareTo(other.Minor);
    }

    public static implicit operator TreeKey(long major) => new(major);

    public static bool operator <(TreeKey left, TreeKey right) => left.CompareTo(right) < 0;

    public static bool operator <=(TreeKey left, TreeKey right) => left.CompareTo(right) <= 0;

    public static bool operator >(TreeKey left, TreeKey right) => left.CompareTo(right) > 0;

    public static bool operator >=(TreeKey left, TreeKey right) => left.CompareTo(right) >= 0;
}
