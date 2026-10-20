using System.Text;
using MiniDb.Records;

namespace MiniDb.Catalog;

/// <summary>Serializes a <see cref="TableInfo"/> into one catalog record and back.</summary>
internal static class TableInfoCodec
{
    public static byte[] Encode(TableInfo table)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteSchema(writer, table.Schema);
            writer.Write(table.PrimaryKeyRoot);
            writer.Write(table.SecondaryIndexRoots.Count);
            foreach ((string column, long root) in table.SecondaryIndexRoots)
            {
                writer.Write(column);
                writer.Write(root);
            }

            writer.Write(table.HeapRoot);
            writer.Write(table.HeapTail);
        }

        return stream.ToArray();
    }

    public static TableInfo Decode(ReadOnlySpan<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray());
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        TableSchema schema = ReadSchema(reader);
        long primaryKeyRoot = reader.ReadInt64();
        int indexCount = reader.ReadInt32();
        var indexRoots = new Dictionary<string, long>(indexCount);
        for (int i = 0; i < indexCount; i++)
        {
            indexRoots.Add(reader.ReadString(), reader.ReadInt64());
        }

        long heapRoot = reader.ReadInt64();
        long heapTail = reader.ReadInt64();
        return new TableInfo(schema, primaryKeyRoot, indexRoots, heapRoot, heapTail);
    }

    private static void WriteSchema(BinaryWriter writer, TableSchema schema)
    {
        writer.Write(schema.Name);
        writer.Write(schema.Columns.Count);
        foreach (ColumnDefinition column in schema.Columns)
        {
            writer.Write(column.Name);
            writer.Write((byte)column.Type);
            writer.Write(column.MaxLength);
        }

        writer.Write(schema.PrimaryKeyIndex);
    }

    private static TableSchema ReadSchema(BinaryReader reader)
    {
        string name = reader.ReadString();
        int columnCount = reader.ReadInt32();
        var columns = new List<ColumnDefinition>(columnCount);
        for (int i = 0; i < columnCount; i++)
        {
            string columnName = reader.ReadString();
            var type = (ColumnType)reader.ReadByte();
            int maxLength = reader.ReadInt32();
            columns.Add(new ColumnDefinition(columnName, type, maxLength));
        }

        return new TableSchema(name, columns, reader.ReadInt32());
    }
}
