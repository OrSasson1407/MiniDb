using MiniDb.Records;

namespace MiniDb.Query;

/// <summary>A parsed SQL statement.</summary>
public abstract record Statement;

public enum CompareOp
{
    Equal,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

/// <summary>A single comparison, such as <c>id &gt;= 10</c>. <see cref="Value"/> is long, double, string or bool.</summary>
public sealed record Condition(string Column, CompareOp Op, object Value);

public sealed record ColumnSpec(string Name, ColumnType Type, int MaxLength, bool IsPrimaryKey);

public sealed record CreateTableStatement(string Table, IReadOnlyList<ColumnSpec> Columns) : Statement;

public sealed record InsertStatement(string Table, IReadOnlyList<object> Values) : Statement;

/// <param name="Columns">Selected columns, or null for <c>*</c>.</param>
/// <param name="Where">Optional filter.</param>
/// <param name="Limit">Maximum number of rows, or null for no limit.</param>
public sealed record SelectStatement(string Table, IReadOnlyList<string>? Columns, Condition? Where, int? Limit) : Statement;

public sealed record DeleteStatement(string Table, Condition Where) : Statement;
