using System.Text;
using MiniDb;
using MiniDb.Api;
using MiniDb.Cli;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: MiniDb.Cli <database-file>");
    return 1;
}

try
{
    using MiniDatabase db = MiniDatabase.Open(args[0]);
    ReplLoop.Run(db);
    return 0;
}
catch (MiniDbException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
