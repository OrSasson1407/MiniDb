using System.Buffers.Binary;
using MiniDb.Records;

namespace MiniDb.Tests.Records;

public class RecordLayoutTests
{
    private static TableSchema MixedSchema() => new(
        "t",
        new[]
        {
            new ColumnDefinition("id", ColumnType.Int64),
            new ColumnDefinition("name", ColumnType.String, maxLength: 20),
            new ColumnDefinition("score", ColumnType.Int32),
            new ColumnDefinition("ok", ColumnType.Bool),
        },
        primaryKeyIndex: 0);

    [Fact]
    public void FixedWidthColumns_HaveKnownOffsets_AndStringsFollowThem()
    {
        TableSchema schema = MixedSchema();

        Assert.Equal(new[] { 0, -1, 8, 12 }, schema.FixedOffsets);
        Assert.Equal(13, schema.FixedSize);
    }

    [Fact]
    public void Int32Column_IsStoredAtItsFixedOffset_WhateverTheStringsAre()
    {
        TableSchema schema = MixedSchema();
        var buffer = new byte[256];
        RecordCodec.Encode(schema, new object?[] { 1L, "longer name", 7, true }, buffer);

        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(8)));
    }
}
