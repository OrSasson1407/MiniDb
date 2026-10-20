namespace MiniDb.Query;

/// <summary>Outcome of <see cref="Api.MiniDatabase.Execute"/>. SELECT fills Rows; INSERT and DELETE fill RowsAffected.</summary>
/// <param name="Columns">Names of the returned columns, in order.</param>
/// <param name="Rows">Returned rows, each with one value per column.</param>
/// <param name="RowsAffected">Rows inserted or deleted.</param>
public sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, int RowsAffected)
{
    public static QueryResult Affected(int count) => new(Array.Empty<string>(), Array.Empty<object?[]>(), count);
}
