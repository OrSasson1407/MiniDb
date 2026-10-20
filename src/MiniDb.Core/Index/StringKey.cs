namespace MiniDb.Index;

/// <summary>
/// The B+ tree key of a string: its first four UTF-16 code units, packed into 64 bits. The packing keeps
/// <see cref="string.CompareOrdinal(string, string)"/> order, so a range of keys is a superset of the strings in a
/// range of values. Strings that share the prefix land on the same key, so a lookup always re-checks the full value.
/// </summary>
public static class StringKey
{
    public static long Prefix(string text)
    {
        ulong packed = 0;
        for (int i = 0; i < 4; i++)
        {
            packed = (packed << 16) | (i < text.Length ? (ushort)text[i] : (ushort)0);
        }

        // Flip the sign bit so that the unsigned order of the packed value is the signed order of the key.
        return (long)(packed ^ 0x8000_0000_0000_0000UL);
    }
}
