using MiniDb.Api;
using MiniDb.Transactions;

namespace MiniDb.Tests.Reliability;

/// <summary>
/// In-process crash simulation. A random workload of transactions is cut off at random points: between commits, with a
/// transaction still open, or inside a commit whose log records are cut short. After every crash the database is reopened
/// (which runs recovery) and must show: every acknowledged commit present, every transaction all or nothing, and nothing
/// from a transaction that never committed.
///
/// A crash that leaves the files as they are is the same as a killed process: written bytes stay, and the buffer pool is
/// lost. Losing unsynced writes (power loss) is tested separately, in PowerLossTests.
/// </summary>
public class CrashSimulationTests : IDisposable
{
    private const int DefaultIterations = 40;

    private readonly string _directory = Directory.CreateTempSubdirectory("minidb-crash-").FullName;

    private string DataPath => Path.Combine(_directory, "crash.mdb");

    private string WalPath => DataPath + ".wal";

    /// <summary>MINIDB_CRASH_ITERATIONS raises the number of crashes for a long run (nightly job).</summary>
    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("MINIDB_CRASH_ITERATIONS"), out int value) ? value : DefaultIterations;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void RandomCrashes_KeepEveryAcknowledgedCommit_AndNoPartialTransaction()
    {
        var random = new Random(20261020);
        var model = new TransactionModel();

        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            MiniDatabase db = Open();
            if (iteration == 0)
            {
                TransactionRows.CreateTable(db);
            }

            TransactionModelCheck.Verify(db, model);

            for (int i = random.Next(1, 6); i > 0; i--)
            {
                long txn = model.Start();
                int rows = random.Next(1, 400);
                TransactionRows.InsertAndCommit(db, txn, rows);
                model.Acknowledge(txn, rows);
            }

            switch (random.Next(3))
            {
                case 0:
                    CrashInsideCommit(db, model, random);
                    break;
                case 1:
                    CrashWithOpenTransaction(db, model, random);
                    break;
                default:
                    db.SimulateCrash();
                    break;
            }
        }

        MiniDatabase final = Open();
        TransactionModelCheck.Verify(final, model);
        final.Dispose();
    }

    /// <summary>
    /// The crash comes during a commit, before it is acknowledged. The log is cut at a random point between the bytes the
    /// commit added. Any cut point is valid: the transaction is then either fully present or fully absent.
    /// </summary>
    private void CrashInsideCommit(MiniDatabase db, TransactionModel model, Random random)
    {
        long before = WalLength();
        long txn = model.Start();
        int rows = random.Next(1, 400);
        TransactionRows.InsertAndCommit(db, txn, rows);
        model.Attempt(txn, rows);
        db.SimulateCrash();

        long after = WalLength();
        if (after < before)
        {
            return; // a checkpoint ran inside the commit and emptied the log; the data file holds the transaction
        }

        long cut = random.NextInt64(before, after + 1);
        using var log = new FileStream(WalPath, FileMode.Open, FileAccess.ReadWrite);
        log.SetLength(cut);
    }

    /// <summary>The crash comes while a transaction is open and has written rows. Those rows must not survive.</summary>
    private static void CrashWithOpenTransaction(MiniDatabase db, TransactionModel model, Random random)
    {
        long txn = model.Start();
        int rows = random.Next(1, 400);
        Transaction open = TransactionRows.InsertOpen(db, txn, rows);
        model.MustBeAbsentTxn(txn);
        db.SimulateCrash();
        GC.KeepAlive(open);
    }

    private MiniDatabase Open() => MiniDatabase.Open(DataPath, new DatabaseOptions
    {
        BufferPoolPages = 64,
        CheckpointThresholdBytes = 64 * 1024,
    });

    private long WalLength() => File.Exists(WalPath) ? new FileInfo(WalPath).Length : 0;
}
