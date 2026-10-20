using MiniDb.Records;

namespace MiniDb.Query;

/// <summary>
/// Turns a WHERE tree into a delegate once per statement, so each row costs only its comparisons. A single-table
/// filter tests one row; a join filter tests a tuple with one row per table in FROM order.
/// </summary>
internal static class Predicates
{
    /// <summary>A filter over the rows of a single-table statement.</summary>
    public static Func<RowView, bool> ForSingleTable(ColumnScope scope, Expr expr) => expr switch
    {
        ComparisonExpr comparison => SingleLeaf(scope, comparison.Condition),
        AndExpr and => Both(ForSingleTable(scope, and.Left), ForSingleTable(scope, and.Right)),
        OrExpr or => Either(ForSingleTable(scope, or.Left), ForSingleTable(scope, or.Right)),
        _ => throw new InvalidOperationException("Unknown expression."),
    };

    /// <summary>A filter over joined tuples. Each comparison reads its column from the tuple's table.</summary>
    public static Func<RowView[], bool> ForTuple(ColumnScope scope, Expr expr) => expr switch
    {
        ComparisonExpr comparison => TupleLeaf(scope, comparison.Condition),
        AndExpr and => Both(ForTuple(scope, and.Left), ForTuple(scope, and.Right)),
        OrExpr or => Either(ForTuple(scope, or.Left), ForTuple(scope, or.Right)),
        _ => throw new InvalidOperationException("Unknown expression."),
    };

    private static Func<RowView, bool> SingleLeaf(ColumnScope scope, Condition condition)
    {
        ColumnRef column = scope.Resolve(condition.Column);
        if (column.Table != 0)
        {
            throw new MiniDbException($"Column '{condition.Column}' is not in the table of this statement.");
        }

        return row => Comparison.Matches(row, column, condition);
    }

    private static Func<RowView[], bool> TupleLeaf(ColumnScope scope, Condition condition)
    {
        ColumnRef column = scope.Resolve(condition.Column);
        return tuple => Comparison.Matches(tuple[column.Table], column, condition);
    }

    private static Func<T, bool> Both<T>(Func<T, bool> left, Func<T, bool> right) => row => left(row) && right(row);

    private static Func<T, bool> Either<T>(Func<T, bool> left, Func<T, bool> right) => row => left(row) || right(row);
}
