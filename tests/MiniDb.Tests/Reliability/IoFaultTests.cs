using MiniDb.Api;
using MiniDb.Storage;
using MiniDb.Transactions;

namespace MiniDb.Tests.Reliability;

/// <summary>
/// A disk that fails. A write, an extension, a truncation or a sync throws an I/O error at a random point of a workload.
/// The engine must report the error (an <see cref="IOException"/> or a <see cref="MiniDbException"/>, never anything else),
/// and after the failure the database must still recover to a state that honours the same rules as a crash: every
/// acknowledged commit present, every transaction all or nothing.
///
/// A transaction whose failure came before its commit must be absent. One whose failure came during its commit may be
/// present or absent, because the commit record may already be durable.
/// </summary>
public class IoFaultTests : IDisposable
{
    private const int DefaultIterations = 40;

    private readonly string _directory = Directory.CreateTempSubdirectory("minidb-iofault-").FullName;

    private string DataPath => Path.Combine(_directory, "faults.mdb");

    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("MINIDB_CRASH_ITERATIONS"), out int value) ? value : DefaultIterations;

    public void Dispose()
    {
        FileObserver.Current = null;
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void FailedWriteOrSync_IsReported_AndAcknowledgedWorkSurvives()
    {
        var random = new Random(77);
        var model = new TransactionModel();

        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            MiniDatabase db = Open();
            if (iteration == 0)
            {
                TransactionRows.CreateTable(db);
            }

            TransactionModelCheck.Verify(db, model);
            RunUntilTheDiskFails(db, model, random);
            db.SimulateCrash();
        }

        MiniDatabase final = Open();
        TransactionModelCheck.Verify(final, model);
        final.Dispose();
    }

    /// <summary>Lets transactions run, and fails exactly one file operation after a random number of them.</summary>
    private static void RunUntilTheDiskFails(MiniDatabase db, TransactionModel model, Random random)
    {
        int countdown = random.Next(1, 300);
        bool failed = false;
        FileObserver.Current = e =>
        {
            if (!failed && --countdown == 0)
            {
                failed = true;
                throw new IOException($"injected I/O error on {e.Kind} of {Path.GetFileName(e.FilePath)}");
            }
        };

        try
        {
            for (int i = 0; i < 8 && !failed; i++)
            {
                long txn = model.Start();
                int rows = random.Next(1, 400);
                RunTransaction(db, model, txn, rows);
            }
        }
        catch (Exception ex) when (ex is IOException or MiniDbException)
        {
            // The injected failure surfaced as an engine error. Which transaction it belonged to is settled below.
        }
        finally
        {
            FileObserver.Current = null;
        }
    }

    /// <summary>
    /// Inserts and commits one transaction. A failure before the commit makes the transaction certainly absent. A failure
    /// during the commit leaves it in doubt.
    /// </summary>
    private static void RunTransaction(MiniDatabase db, TransactionModel model, long txn, int rows)
    {
        Transaction transaction = db.BeginTransaction();
        try
        {
            Table users = db.GetTable("users");
            for (int j = 0; j < rows; j++)
            {
                users.Insert(new object?[] { txn * TransactionRows.KeySpace + j, TransactionRows.Name(txn) });
            }
        }
        catch
        {
            model.MustBeAbsentTxn(txn);
            transaction.Dispose();
            throw;
        }

        try
        {
            transaction.Commit();
        }
        catch
        {
            model.Attempt(txn, rows);
            throw;
        }

        model.Acknowledge(txn, rows);
    }

    private MiniDatabase Open() => MiniDatabase.Open(DataPath, new DatabaseOptions
    {
        BufferPoolPages = 64,
        CheckpointThresholdBytes = 64 * 1024,
    });
}
