using System.Diagnostics;
using System.Text;
using MiniDb.Api;
using MiniDb.Transactions;

namespace MiniDb.CrashHarness;

/// <summary>
/// Real crash tests against a real process. Three modes:
///   kill &lt;cycles&gt; &lt;dir&gt;     a child process writes transactions; the parent kills it at a random moment
///                             (the operating system's kill, no cleanup), then reopens the database and verifies it.
///   soak &lt;minutes&gt; &lt;dir&gt;   a mixed workload (insert, update, delete, select) for a fixed time, with a check at the end.
///   child &lt;db&gt; &lt;ack&gt;      used by kill; writes forever until it is killed.
///
/// The child writes "I txn rows" before each commit and "A txn" after it returns, so the parent knows what was announced,
/// what was acknowledged, and what was in doubt at the moment of the kill.
/// </summary>
public static class Program
{
    /// <summary>Row key = transaction * KeySpace + row index. Wider than the largest transaction.</summary>
    private const int KeySpace = 512;

    private const int MaxRowsPerTransaction = 200;

    public static int Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["child", var db, var ack] => Child(db, ack),
                ["kill", var cycles, var dir] => Kill(int.Parse(cycles), dir),
                ["soak", var minutes, var dir] => Soak(double.Parse(minutes), dir),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("usage: kill <cycles> <dir>  |  soak <minutes> <dir>");
        return 2;
    }

    private static int Child(string dbPath, string ackPath)
    {
        // Never disposed: the parent kills this process, and the database must survive that.
        MiniDatabase db = Open(dbPath);
        EnsureUsers(db);
        long txn = LastTransaction(db) + 1;
        var random = new Random(Environment.ProcessId);
        using var ack = new FileStream(ackPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        while (true)
        {
            int rows = random.Next(1, MaxRowsPerTransaction + 1);
            Append(ack, $"I {txn} {rows}\n");
            InsertTransaction(db, txn, rows);
            Append(ack, $"A {txn}\n");
            txn++;
        }
    }

    private static int Kill(int cycles, string dir)
    {
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "kill.mdb");
        string ackPath = Path.Combine(dir, "kill.ack");
        var random = new Random(1);
        var timer = Stopwatch.StartNew();

        for (int cycle = 1; cycle <= cycles; cycle++)
        {
            using (Process child = StartChild(dbPath, ackPath))
            {
                Thread.Sleep(random.Next(150, 900));
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
            }

            Verify(dbPath, ackPath);
            if (cycle % 25 == 0)
            {
                Console.WriteLine($"cycle {cycle}/{cycles} ok ({timer.Elapsed:mm\\:ss})");
            }
        }

        Console.WriteLine($"{cycles} process kills, every check passed ({timer.Elapsed:mm\\:ss}).");
        return 0;
    }

    private static Process StartChild(string dbPath, string ackPath)
    {
        var info = new ProcessStartInfo("dotnet", new[] { typeof(Program).Assembly.Location, "child", dbPath, ackPath })
        {
            UseShellExecute = false,
        };
        return Process.Start(info) ?? throw new InvalidOperationException("the child process did not start");
    }

    /// <summary>Reads what the child announced and acknowledged, then checks the database against it.</summary>
    private static void Verify(string dbPath, string ackPath)
    {
        var announced = new Dictionary<long, int>();
        var acknowledged = new HashSet<long>();
        if (File.Exists(ackPath))
        {
            foreach (string line in File.ReadAllLines(ackPath))
            {
                string[] parts = line.Split(' ');
                if (parts.Length == 3 && parts[0] == "I" && long.TryParse(parts[1], out long intent) && int.TryParse(parts[2], out int rows))
                {
                    announced[intent] = rows;
                }
                else if (parts.Length == 2 && parts[0] == "A" && long.TryParse(parts[1], out long done))
                {
                    acknowledged.Add(done);
                }
            }
        }

        MiniDatabase db = Open(dbPath);
        try
        {
            var counts = new Dictionary<long, int>();
            if (TryGetUsers(db, out Table? users))
            {
                foreach (object?[] row in users!.Scan())
                {
                    long id = (long)row[0]!;
                    long txn = id / KeySpace;
                    if (!((string)row[1]!).StartsWith($"t{txn}:", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"row {id} carries another transaction's name");
                    }

                    counts[txn] = counts.GetValueOrDefault(txn) + 1;
                }
            }

            foreach (long txn in acknowledged)
            {
                if (!announced.TryGetValue(txn, out int rows) || counts.GetValueOrDefault(txn) != rows)
                {
                    throw new InvalidOperationException(
                        $"acknowledged transaction {txn} has {counts.GetValueOrDefault(txn)} rows, expected {rows}");
                }
            }

            foreach ((long txn, int present) in counts)
            {
                if (!announced.TryGetValue(txn, out int rows))
                {
                    throw new InvalidOperationException($"rows of transaction {txn} are visible, but it was never announced");
                }

                if (!acknowledged.Contains(txn) && present != 0 && present != rows)
                {
                    throw new InvalidOperationException($"in-doubt transaction {txn} is partly visible: {present} of {rows} rows");
                }
            }
        }
        finally
        {
            db.Dispose();
        }
    }

    /// <summary>
    /// Mixed SQL workload for a fixed time. The ids of live rows are kept in memory, so every operation can name a row that
    /// exists, and the count is checked both in the running process and after a clean reopen.
    /// </summary>
    private static int Soak(double minutes, string dir)
    {
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "soak.mdb");
        var random = new Random(2);
        var live = new List<long>();
        long nextId = 1;
        var timer = Stopwatch.StartNew();
        var nextReport = TimeSpan.FromSeconds(30);

        MiniDatabase db = Open(dbPath);
        if (!TryGetTable(db, "soak", out _))
        {
            db.Execute("CREATE TABLE soak (id BIGINT PRIMARY KEY, v INT, s VARCHAR(60))");
        }

        while (timer.Elapsed < TimeSpan.FromMinutes(minutes))
        {
            int op = random.Next(10);
            if (op < 5 || live.Count == 0)
            {
                db.Execute($"INSERT INTO soak VALUES ({nextId}, {random.Next(1000)}, 'row-{nextId}')");
                live.Add(nextId++);
            }
            else if (op < 7)
            {
                long id = live[random.Next(live.Count)];
                db.Execute($"UPDATE soak SET v = {random.Next(1000)} WHERE id = {id}");
            }
            else if (op < 8)
            {
                int at = random.Next(live.Count);
                db.Execute($"DELETE FROM soak WHERE id = {live[at]}");
                live[at] = live[^1];
                live.RemoveAt(live.Count - 1);
            }
            else
            {
                long id = live[random.Next(live.Count)];
                if (db.Execute($"SELECT id FROM soak WHERE id = {id}").Rows.Count != 1)
                {
                    throw new InvalidOperationException($"row {id} is missing");
                }
            }

            if (timer.Elapsed >= nextReport)
            {
                Report(dbPath, db, live.Count, timer.Elapsed);
                nextReport += TimeSpan.FromSeconds(30);
            }
        }

        int counted = db.Execute("SELECT id FROM soak").Rows.Count;
        if (counted != live.Count)
        {
            throw new InvalidOperationException($"the table has {counted} rows, expected {live.Count}");
        }

        db.Dispose();
        using (MiniDatabase reopened = Open(dbPath))
        {
            int afterReopen = reopened.Execute("SELECT id FROM soak").Rows.Count;
            if (afterReopen != live.Count)
            {
                throw new InvalidOperationException($"after reopening the table has {afterReopen} rows, expected {live.Count}");
            }
        }

        Console.WriteLine($"soak of {minutes} min: {live.Count} live rows, every check passed ({timer.Elapsed:hh\\:mm\\:ss}).");
        return 0;
    }

    private static void Report(string dbPath, MiniDatabase db, int liveRows, TimeSpan elapsed)
    {
        string wal = dbPath + ".wal";
        long walBytes = File.Exists(wal) ? new FileInfo(wal).Length : 0;
        Console.WriteLine(
            $"{elapsed:hh\\:mm\\:ss}  rows={liveRows}  data={new FileInfo(dbPath).Length / 1024} KB  " +
            $"wal={walBytes / 1024} KB  managed={GC.GetTotalMemory(forceFullCollection: false) / 1024} KB");
    }

    private static bool TryGetTable(MiniDatabase db, string name, out Table? table)
    {
        try
        {
            table = db.GetTable(name);
            return true;
        }
        catch (MiniDb.MiniDbException)
        {
            table = null;
            return false;
        }
    }

    private static MiniDatabase Open(string path) => MiniDatabase.Open(path);

    private static void EnsureUsers(MiniDatabase db)
    {
        if (!TryGetUsers(db, out _))
        {
            db.Execute("CREATE TABLE users (id BIGINT PRIMARY KEY, name VARCHAR(60))");
        }
    }

    private static bool TryGetUsers(MiniDatabase db, out Table? users)
    {
        try
        {
            users = db.GetTable("users");
            return true;
        }
        catch (MiniDb.MiniDbException)
        {
            users = null;
            return false;
        }
    }

    private static long LastTransaction(MiniDatabase db)
    {
        if (!TryGetUsers(db, out Table? users))
        {
            return 0;
        }

        long last = 0;
        foreach (object?[] row in users!.Scan())
        {
            last = Math.Max(last, (long)row[0]! / KeySpace);
        }

        return last;
    }

    private static void InsertTransaction(MiniDatabase db, long txn, int rows)
    {
        Table users = db.GetTable("users");
        using Transaction transaction = db.BeginTransaction();
        for (int j = 0; j < rows; j++)
        {
            users.Insert(new object?[] { txn * KeySpace + j, Name(txn) });
        }

        transaction.Commit();
    }

    private static string Name(long txn) => $"t{txn}:" + new string('x', 35);

    private static void Append(FileStream file, string line)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(line);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }
}
