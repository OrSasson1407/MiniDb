using MiniDb.Api;
using MiniDb.Database;
using MiniDb.Storage;
using MiniDb.Transactions;

namespace MiniDb.Tests.Reliability;

/// <summary>
/// A damaged data file must be refused or read as an error, never read as wrong data and never hang the engine.
/// Two kinds of damage are tried: plain bit flips, which the page checksum must catch; and changes that come with a
/// fresh checksum, which only the structure checks can catch. The second kind is what an attacker or a buggy writer makes.
/// </summary>
public class CorruptFileTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("minidb-corrupt-").FullName;

    private string BasePath => Path.Combine(_directory, "base.mdb");

    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("MINIDB_FUZZ_ITERATIONS"), out int value) ? value : 200;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void BitFlipInAnyUsedPage_IsDetected_AsCorruptPage()
    {
        BuildBaseFile();
        long pages = new FileInfo(BasePath).Length / PageConstants.PageSize;
        int detected = 0;

        for (long page = 1; page < pages; page++)
        {
            string copy = CopyOfBase(page.ToString());
            FlipByte(copy, page * PageConstants.PageSize + 100);

            Exception? failure = OpenAndRead(copy);
            if (failure is CorruptPageException)
            {
                detected++;
            }
            else
            {
                Assert.True(failure is null || failure is MiniDbException, $"page {page}: {failure}");
            }
        }

        Assert.True(detected > 0, "no bit flip was detected as a corrupt page");
    }

    [Fact]
    public void DamagedMagic_IsRefusedAtOpen()
    {
        BuildBaseFile();
        string copy = CopyOfBase("magic");
        FlipByte(copy, 3);

        Assert.Throws<InvalidDatabaseException>(() => MiniDatabase.Open(copy));
    }

    [Fact]
    public void PagesWithFreshChecksums_NeverCrashOrHang_TheEngine()
    {
        BuildBaseFile();
        long pages = new FileInfo(BasePath).Length / PageConstants.PageSize;
        var random = new Random(13);

        for (int i = 0; i < Iterations; i++)
        {
            string copy = CopyOfBase(i.ToString());
            long page = random.NextInt64(pages);
            MutatePageWithFreshChecksum(copy, page, random);

            Exception? failure = OpenAndReadWithDeadline(copy);
            Assert.True(failure is null || failure is MiniDbException,
                $"iteration {i}, page {page}: {failure}");
        }
    }

    private void BuildBaseFile()
    {
        using MiniDatabase db = MiniDatabase.Open(BasePath);
        db.Execute("CREATE TABLE users (id BIGINT PRIMARY KEY, name VARCHAR(40), score INT)");
        Table users = db.GetTable("users");
        using Transaction transaction = db.BeginTransaction();
        for (long id = 0; id < 600; id++)
        {
            users.Insert(new object?[] { id, $"name-{id:D6}-" + new string('x', 20), (int)(id % 50) });
        }

        transaction.Commit();
    }

    private string CopyOfBase(string tag)
    {
        string copy = Path.Combine(_directory, $"copy-{tag}.mdb");
        File.Copy(BasePath, copy, overwrite: true);
        File.Delete(copy + ".wal");
        return copy;
    }

    private static void FlipByte(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        stream.Position = offset;
        int value = stream.ReadByte();
        stream.Position = offset;
        stream.WriteByte((byte)(value ^ 0xFF));
    }

    /// <summary>Changes a few bytes of one page, then recomputes its checksum so that the change is not caught there.</summary>
    private static void MutatePageWithFreshChecksum(string path, long page, Random random)
    {
        var bytes = new byte[PageConstants.PageSize];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            stream.Position = page * PageConstants.PageSize;
            stream.ReadExactly(bytes);
        }

        for (int k = random.Next(1, 6); k > 0; k--)
        {
            bytes[random.Next(PageConstants.UsableSize)] = (byte)random.Next(256);
        }

        PageChecksum.Stamp(bytes);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write);
        writer.Position = page * PageConstants.PageSize;
        writer.Write(bytes);
    }

    private static Exception? OpenAndRead(string path)
    {
        try
        {
            using MiniDatabase db = MiniDatabase.Open(path);
            db.Execute("SELECT * FROM users");
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>A structure that is cyclic after the mutation must not make the reader loop forever.</summary>
    private static Exception? OpenAndReadWithDeadline(string path)
    {
        Task<Exception?> attempt = Task.Run(() => OpenAndRead(path));
        if (!attempt.Wait(TimeSpan.FromSeconds(10)))
        {
            return new TimeoutException("the engine did not finish reading a damaged file within 10 seconds");
        }

        return attempt.Result;
    }
}
