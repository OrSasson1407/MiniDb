using MiniDb.Api;
using MiniDb.Storage;
using MiniDb.Transactions;

namespace MiniDb.Tests.Reliability;

/// <summary>
/// A power cut loses everything that was written but not synced. The test records each file's contents at every sync,
/// which is exactly what would survive, and at the cut it rolls each file back to its last synced state.
///
/// The rule under test: a commit that returned has been synced, so it must survive the cut. A commit that did not return
/// is not acknowledged, so it may be lost, but it must not be half-visible. A write that was not synced may be lost, and
/// the log must still let recovery rebuild the rest.
///
/// This checks the order of the engine's syncs: a commit that is acknowledged before its log is synced is caught here.
/// It does not model the disk reordering writes inside a single sync, which a test cannot reproduce.
/// </summary>
public class PowerLossTests : IDisposable
{
    private const int DefaultIterations = 30;

    private readonly string _directory = Directory.CreateTempSubdirectory("minidb-power-").FullName;

    private string DataPath => Path.Combine(_directory, "power.mdb");

    private string WalPath => DataPath + ".wal";

    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("MINIDB_CRASH_ITERATIONS"), out int value) ? value : DefaultIterations;

    public void Dispose()
    {
        FileObserver.Current = null;
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void PowerCut_KeepsEveryReturnedCommit_AndNothingHalfWritten()
    {
        var random = new Random(4242);
        var model = new TransactionModel();
        var synced = new Dictionary<string, byte[]>();

        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            FileObserver.Current = e =>
            {
                if (e.Kind == FileEventKind.Sync)
                {
                    synced[e.FilePath] = e.ReadContents();
                }
            };

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

            if (random.Next(2) == 0)
            {
                long txn = model.Start();
                Transaction open = TransactionRows.InsertOpen(db, txn, random.Next(1, 400));
                model.MustBeAbsentTxn(txn);
                GC.KeepAlive(open);
            }

            db.SimulateCrash();
            FileObserver.Current = null;
            RollBackToSynced(synced, DataPath);
            RollBackToSynced(synced, WalPath);
        }

        MiniDatabase final = Open();
        TransactionModelCheck.Verify(final, model);
        final.Dispose();
    }

    /// <summary>Restores a file to its last synced contents. A file that was never synced ends up empty.</summary>
    private static void RollBackToSynced(Dictionary<string, byte[]> synced, string path)
    {
        byte[] bytes = synced.GetValueOrDefault(path, Array.Empty<byte>());
        File.WriteAllBytes(path, bytes);
    }

    private MiniDatabase Open() => MiniDatabase.Open(DataPath, new DatabaseOptions
    {
        BufferPoolPages = 64,
        CheckpointThresholdBytes = 64 * 1024,
    });
}
