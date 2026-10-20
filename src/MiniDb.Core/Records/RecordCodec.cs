using System.Buffers.Binary;
using System.Text;

namespace MiniDb.Records;

/// <summary>
/// Converts a row to bytes and back. Fixed-width columns come first, in schema order, so each has a known offset;
/// String columns follow, each as a UInt16 length and UTF-8 bytes. Sizes: Int32 4, Int64 8, Double 8, Bool 1.
/// Null values are not supported in version 1.
/// </summary>
public static class RecordCodec
{
    private const int LengthPrefixSize = sizeof(ushort);

    /// <summary>Writes <paramref name="values"/> into <paramref name="dest"/> and returns the number of bytes written.</summary>
    public static int Encode(TableSchema schema, object?[] values, Span<byte> dest)
    {
        int size = EncodedSize(schema, values);
        if (size > SlottedPage.MaxRecordSize)
        {
            throw new RecordTooLargeException(size);
        }

        if (dest.Length < size)
        {
            throw new ArgumentException($"Destination holds {dest.Length} bytes, the record needs {size}.", nameof(dest));
        }

        int stringCursor = schema.FixedSize;
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            int fixedOffset = schema.FixedOffsets[i];
            if (fixedOffset >= 0)
            {
                WriteValue(values[i]!, dest[fixedOffset..]);
            }
            else
            {
                stringCursor += WriteValue(values[i]!, dest[stringCursor..]);
            }
        }

        return stringCursor;
    }

    public static object?[] Decode(TableSchema schema, ReadOnlySpan<byte> src)
    {
        var values = new object?[schema.Columns.Count];
        int stringCursor = schema.FixedSize;
        for (int i = 0; i < values.Length; i++)
        {
            ColumnDefinition column = schema.Columns[i];
            int fixedOffset = schema.FixedOffsets[i];
            if (fixedOffset >= 0)
            {
                values[i] = ReadValue(column.Type, src[fixedOffset..], out _);
            }
            else
            {
                values[i] = ReadValue(column.Type, src[stringCursor..], out int used);
                stringCursor += used;
            }
        }

        return values;
    }

    /// <summary>Byte width of a fixed-width column type, or 0 for String.</summary>
    internal static int FixedWidthOf(ColumnType type) => type switch
    {
        ColumnType.Int32 => sizeof(int),
        ColumnType.Int64 => sizeof(long),
        ColumnType.Double => sizeof(double),
        ColumnType.Bool => sizeof(byte),
        _ => 0,
    };

    /// <summary>Number of bytes <see cref="Encode"/> will write for these values. Also validates them.</summary>
    public static int EncodedSize(TableSchema schema, object?[] values)
    {
        if (values.Length != schema.Columns.Count)
        {
            throw new ArgumentException($"Table '{schema.Name}' has {schema.Columns.Count} columns, got {values.Length} values.", nameof(values));
        }

        int size = 0;
        for (int i = 0; i < values.Length; i++)
        {
            size += ValueSize(schema.Columns[i], values[i]);
        }

        return size;
    }

    private static int ValueSize(ColumnDefinition column, object? value)
    {
        Validate(column, value);
        return value switch
        {
            int => sizeof(int),
            long => sizeof(long),
            double => sizeof(double),
            bool => sizeof(byte),
            string s => LengthPrefixSize + Encoding.UTF8.GetByteCount(s),
            _ => throw new ArgumentException($"Unsupported value for column '{column.Name}'."),
        };
    }

    private static void Validate(ColumnDefinition column, object? value)
    {
        bool typeMatches = (column.Type, value) switch
        {
            (ColumnType.Int32, int) => true,
            (ColumnType.Int64, long) => true,
            (ColumnType.Double, double) => true,
            (ColumnType.Bool, bool) => true,
            (ColumnType.String, string) => true,
            _ => false,
        };

        if (!typeMatches)
        {
            throw new ArgumentException($"Column '{column.Name}' expects {column.Type}, got {(value is null ? "null" : value.GetType().Name)}.");
        }

        if (value is string text && text.Length > column.MaxLength)
        {
            throw new ArgumentException($"Column '{column.Name}' exceeds its maximum length of {column.MaxLength} characters.");
        }
    }

    private static int WriteValue(object value, Span<byte> dest)
    {
        switch (value)
        {
            case int i:
                BinaryPrimitives.WriteInt32LittleEndian(dest, i);
                return sizeof(int);
            case long l:
                BinaryPrimitives.WriteInt64LittleEndian(dest, l);
                return sizeof(long);
            case double d:
                BinaryPrimitives.WriteDoubleLittleEndian(dest, d);
                return sizeof(double);
            case bool b:
                dest[0] = (byte)(b ? 1 : 0);
                return sizeof(byte);
            case string s:
                int written = Encoding.UTF8.GetBytes(s, dest[LengthPrefixSize..]);
                BinaryPrimitives.WriteUInt16LittleEndian(dest, (ushort)written);
                return LengthPrefixSize + written;
            default:
                throw new ArgumentException("Unsupported value type.");
        }
    }

    private static object ReadValue(ColumnType type, ReadOnlySpan<byte> src, out int used)
    {
        switch (type)
        {
            case ColumnType.Int32:
                used = sizeof(int);
                return BinaryPrimitives.ReadInt32LittleEndian(src);
            case ColumnType.Int64:
                used = sizeof(long);
                return BinaryPrimitives.ReadInt64LittleEndian(src);
            case ColumnType.Double:
                used = sizeof(double);
                return BinaryPrimitives.ReadDoubleLittleEndian(src);
            case ColumnType.Bool:
                used = sizeof(byte);
                return src[0] != 0;
            case ColumnType.String:
                int length = BinaryPrimitives.ReadUInt16LittleEndian(src);
                used = LengthPrefixSize + length;
                return Encoding.UTF8.GetString(src.Slice(LengthPrefixSize, length));
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }
}
