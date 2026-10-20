using MiniDb.Records;

namespace MiniDb.Tests.Records;

public class RecordCodecTests
{
    private static TableSchema UsersSchema(int nameMaxLength = 50) => new(
        "users",
        new[]
        {
            new ColumnDefinition("id", ColumnType.Int64),
            new ColumnDefinition("name", ColumnType.String, maxLength: nameMaxLength),
        },
        primaryKeyIndex: 0);

    [Fact]
    public void RowWithHebrewString_RoundTrips()
    {
        var schema = UsersSchema();
        var buffer = new byte[256];
        int len = RecordCodec.Encode(schema, new object?[] { 7L, "שלום" }, buffer);

        var values = RecordCodec.Decode(schema, buffer.AsSpan(0, len));

        Assert.Equal(7L, values[0]);
        Assert.Equal("שלום", values[1]);
    }

    [Fact]
    public void AllColumnTypes_RoundTrip()
    {
        var schema = new TableSchema(
            "mixed",
            new[]
            {
                new ColumnDefinition("id", ColumnType.Int64),
                new ColumnDefinition("count", ColumnType.Int32),
                new ColumnDefinition("price", ColumnType.Double),
                new ColumnDefinition("active", ColumnType.Bool),
                new ColumnDefinition("label", ColumnType.String, maxLength: 10),
            },
            primaryKeyIndex: 0);
        object?[] row = { 1L, 42, 3.5, true, "x" };
        var buffer = new byte[256];
        int len = RecordCodec.Encode(schema, row, buffer);

        var values = RecordCodec.Decode(schema, buffer.AsSpan(0, len));

        Assert.Equal(row, values);
    }

    [Fact]
    public void Encode_StringLongerThanMaxLength_ThrowsNamingTheColumn()
    {
        var schema = UsersSchema(nameMaxLength: 3);

        var ex = Assert.Throws<ArgumentException>(() =>
            RecordCodec.Encode(schema, new object?[] { 1L, "toolong" }, new byte[256]));

        Assert.Contains("name", ex.Message);
    }

    [Fact]
    public void Encode_NullValue_Throws()
    {
        var schema = UsersSchema();

        Assert.Throws<ArgumentException>(() =>
            RecordCodec.Encode(schema, new object?[] { null, "a" }, new byte[256]));
    }

    [Fact]
    public void Encode_RecordLargerThanPage_ThrowsRecordTooLarge()
    {
        var schema = UsersSchema(nameMaxLength: 10000);
        string huge = new('a', 5000);

        Assert.Throws<RecordTooLargeException>(() =>
            RecordCodec.Encode(schema, new object?[] { 1L, huge }, new byte[8192]));
    }

    [Fact]
    public void SchemaWithNonInt64PrimaryKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => new TableSchema(
            "bad",
            new[] { new ColumnDefinition("id", ColumnType.Int32) },
            primaryKeyIndex: 0));
    }
}
