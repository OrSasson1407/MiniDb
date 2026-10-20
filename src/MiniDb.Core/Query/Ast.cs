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

/// <summary>
/// A single comparison, such as <c>id &gt;= 10</c>. <see cref="Column"/> may be qualified as <c>table.column</c>.
/// <see cref="Value"/> is long, double, string or bool.
/// </summary>
public sealed record Condition(string Column, CompareOp Op, object Value);

/// <summary>A WHERE clause: comparisons joined with AND and OR, optionally grouped with parentheses.</summary>
public abstract record Expr;

public sealed record ComparisonExpr(Condition Condition) : Expr;

public sealed record AndExpr(Expr Left, Expr Right) : Expr;

public sealed record OrExpr(Expr Left, Expr Right) : Expr;

public sealed record ColumnSpec(string Name, ColumnType Type, int MaxLength, bool IsPrimaryKey);

public sealed record CreateTableStatement(string Table, IReadOnlyList<ColumnSpec> Columns) : Statement;

public sealed record InsertStatement(string Table, IReadOnlyList<object> Values) : Statement;

public enum AggregateKind
{
    Count,
    Sum,
    Min,
    Max,
    Avg,
}

/// <summary>One item of a SELECT list: a column or an aggregate.</summary>
public abstract record SelectItem;

/// <summary>A column, possibly qualified as <c>table.column</c>.</summary>
public sealed record ColumnItem(string Column) : SelectItem;

/// <summary>An aggregate over a column. <see cref="Column"/> is null for <c>COUNT(*)</c>.</summary>
public sealed record AggregateItem(AggregateKind Kind, string? Column) : SelectItem;

public sealed record OrderTerm(string Column, bool Descending);

/// <summary>An INNER JOIN of <see cref="Table"/> on an equality between two qualified columns.</summary>
public sealed record JoinClause(string Table, string LeftColumn, string RightColumn);

/// <param name="Items">Selected items, or null for <c>*</c>.</param>
/// <param name="Joins">Inner joins, in order. Empty for a single-table query.</param>
/// <param name="Where">Optional filter.</param>
/// <param name="GroupBy">Grouping columns. Empty when there is no GROUP BY.</param>
/// <param name="OrderBy">Sort keys in order. Empty when there is no ORDER BY.</param>
/// <param name="Limit">Maximum number of rows, or null for no limit.</param>
public sealed record SelectStatement(
    string Table,
    IReadOnlyList<SelectItem>? Items,
    IReadOnlyList<JoinClause> Joins,
    Expr? Where,
    IReadOnlyList<string> GroupBy,
    IReadOnlyList<OrderTerm> OrderBy,
    int? Limit) : Statement;

public sealed record Assignment(string Column, object Value);

public sealed record UpdateStatement(string Table, IReadOnlyList<Assignment> Assignments, Expr Where) : Statement;

public sealed record DeleteStatement(string Table, Expr Where) : Statement;

public sealed record CreateIndexStatement(string Table, string Column) : Statement;
