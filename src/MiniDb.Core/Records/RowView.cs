using System.Buffers.Binary;
using System.Text;

namespace MiniDb.Records;

/// <summary>
/// One stored row, read in place. Typed getters decode only the column they are asked for: fixed-width columns are
/// read at their known offset, and a string is read from its position after the fixed block. A view owns a copy of
/// its record, so it stays valid after the page it came from is unpinned.
/// </summary>
public readonly struct RowView
{
    private readonly TableSchema _schema;
    private readonly byte[] _record;

    internal RowView(TableSchema schema, byte[] record)
    {
        _schema = schema;
        _record = record;
    }

    public int ColumnCount => _schema.Columns.Count;

    public long GetInt64(int column) => BinaryPrimitives.ReadInt64LittleEndian(FixedBytes(column, ColumnType.Int64));

    public int GetInt32(int column) => BinaryPrimitives.ReadInt32LittleEndian(FixedBytes(column, ColumnType.Int32));

    public double GetDouble(int column) => BinaryPrimitives.ReadDoubleLittleEndian(FixedBytes(column, ColumnType.Double));

    public bool GetBool(int column) => FixedBytes(column, ColumnType.Bool)[0] != 0;

    public string GetString(int column)
    {
        int start = StringStart(column);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(_record.AsSpan(start));
        return Encoding.UTF8.GetString(_record, start + sizeof(ushort), length);
    }

    /// <summary>The column as a boxed value, the same form <see cref="Api.Table.Get"/> returns.</summary>
    public object? this[int column] => _schema.Columns[column].Type switch
    {
        ColumnType.Int32 => GetInt32(column),
        ColumnType.Int64 => GetInt64(column),
        ColumnType.Double => GetDouble(column),
        ColumnType.Bool => GetBool(column),
        ColumnType.String => GetString(column),
        _ => throw new InvalidOperationException("Unknown column type."),
    };

    private ReadOnlySpan<byte> FixedBytes(int column, ColumnType expected)
    {
        RequireType(column, expected);
        return _record.AsSpan(_schema.FixedOffsets[column]);
    }

    /// <summary>Offset of a string column: after the fixed block and after every earlier string column.</summary>
    private int StringStart(int column)
    {
        RequireType(column, ColumnType.String);
        int cursor = _schema.FixedSize;
        for (int i = 0; i < column; i++)
        {
            if (_schema.Columns[i].Type == ColumnType.String)
            {
                cursor += sizeof(ushort) + BinaryPrimitives.ReadUInt16LittleEndian(_record.AsSpan(cursor));
            }
        }

        return cursor;
    }

    private void RequireType(int column, ColumnType expected)
    {
        ColumnType actual = _schema.Columns[column].Type;
        if (actual != expected)
        {
            throw new InvalidOperationException($"Column {column} is {actual}, not {expected}.");
        }
    }
}
