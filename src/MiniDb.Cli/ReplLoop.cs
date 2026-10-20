using System.Globalization;
using MiniDb.Api;
using MiniDb.Query;

namespace MiniDb.Cli;

/// <summary>Reads SQL from the console, runs each statement, and prints the result as a plain table.</summary>
internal static class ReplLoop
{
    public static void Run(MiniDatabase db)
    {
        Console.WriteLine("MiniDb shell. Type a statement, or 'exit' to quit.");
        while (true)
        {
            Console.Write("minidb> ");
            string? line = Console.ReadLine();
            if (line is null || line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                Execute(db, line);
            }
        }
    }

    private static void Execute(MiniDatabase db, string sql)
    {
        try
        {
            QueryResult result = db.Execute(sql);
            if (result.Columns.Count > 0)
            {
                PrintTable(result);
            }
            else if (result.RowsAffected > 0)
            {
                Console.WriteLine($"{result.RowsAffected} row(s) affected.");
            }
            else
            {
                Console.WriteLine("OK.");
            }
        }
        catch (MiniDbException ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    private static void PrintTable(QueryResult result)
    {
        List<string[]> cells = result.Rows.Select(row => row.Select(Format).ToArray()).ToList();
        int[] widths = result.Columns
            .Select((name, i) => Math.Max(name.Length, cells.Select(row => row[i].Length).Append(0).Max()))
            .ToArray();

        WriteRow(result.Columns, widths);
        Console.WriteLine(string.Join("-+-", widths.Select(width => new string('-', width))));
        foreach (string[] row in cells)
        {
            WriteRow(row, widths);
        }

        Console.WriteLine($"({cells.Count} row(s))");
    }

    private static void WriteRow(IEnumerable<string> values, int[] widths) =>
        Console.WriteLine(string.Join(" | ", values.Select((value, i) => value.PadRight(widths[i]))));

    private static string Format(object? value) => value switch
    {
        null => "NULL",
        bool b => b ? "TRUE" : "FALSE",
        double d => d.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
