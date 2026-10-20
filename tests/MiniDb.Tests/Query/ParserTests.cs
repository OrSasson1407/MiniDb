using MiniDb.Records;
using MiniDb.Query;

namespace MiniDb.Tests.Query;

public class ParserTests
{
    [Fact]
    public void Select_WithWhereAndLimit_ParsesAllParts()
    {
        var statement = Assert.IsType<SelectStatement>(Parser.Parse("SELECT id, name FROM users WHERE id >= 10 LIMIT 5"));

        Assert.Equal("users", statement.Table);
        Assert.Equal(new[] { "id", "name" }, statement.Columns);
        Assert.Equal(new Condition("id", CompareOp.GreaterOrEqual, 10L), statement.Where);
        Assert.Equal(5, statement.Limit);
    }

    [Fact]
    public void CreateTable_ParsesTypesAndPrimaryKey()
    {
        var statement = Assert.IsType<CreateTableStatement>(
            Parser.Parse("CREATE TABLE users (id BIGINT PRIMARY KEY, name VARCHAR(50), price DOUBLE)"));

        Assert.Equal(3, statement.Columns.Count);
        Assert.Equal(ColumnType.Int64, statement.Columns[0].Type);
        Assert.True(statement.Columns[0].IsPrimaryKey);
        Assert.Equal(ColumnType.String, statement.Columns[1].Type);
        Assert.Equal(50, statement.Columns[1].MaxLength);
        Assert.Equal(ColumnType.Double, statement.Columns[2].Type);
    }

    [Fact]
    public void SelectWithoutColumns_Throws_WithSyntaxError()
    {
        Assert.Throws<SqlSyntaxException>(() => Parser.Parse("SELECT FROM users"));
    }

    [Fact]
    public void SelectWithoutTable_Throws_WithSyntaxError()
    {
        Assert.Throws<SqlSyntaxException>(() => Parser.Parse("SELECT * FROM"));
    }

    [Fact]
    public void Delete_WithoutWhere_IsRejected()
    {
        Assert.Throws<SqlSyntaxException>(() => Parser.Parse("DELETE FROM users"));
    }
}
