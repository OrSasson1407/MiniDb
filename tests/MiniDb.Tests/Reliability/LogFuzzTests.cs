using MiniDb.Storage;
using MiniDb.Transactions;

namespace MiniDb.Tests.Reliability;

/// <summary>
/// The log and the page deltas are read from files that a crash or a bad disk may have damaged. Two properties must hold:
/// damaged input may be refused, but it must never make the reader crash; and it must never produce a record that was
/// not written. A reader that invents a record could replay a change that never committed.
/// </summary>
public class LogFuzzTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"minidb-logfuzz-{Guid.NewGuid():N}.wal");

    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("MINIDB_FUZZ_ITERATIONS"), out int value) ? value : 300;

    public void Dispose()
    {
        File.Delete(_path);
    }

    [Fact]
    public void RandomBytes_AreRefusedOrIgnored_AndNeverCrashTheReader()
    {
        var random = new Random(3);
        for (int i = 0; i < Iterations; i++)
        {
            var bytes = new byte[random.Next(0, 20_000)];
            random.NextBytes(bytes);
            File.WriteAllBytes(_path, bytes);

            using var wal = new WriteAheadLog(_path);
            try
            {
                List<WalFrame> frames = wal.ReadAll().ToList();
                Assert.Empty(frames);
            }
            catch (CorruptLogException)
            {
                // Refusing a damaged log is the correct answer.
            }
        }
    }

    [Fact]
    public void MutatedLog_YieldsOnlyARealPrefix_OrIsRefused()
    {
        List<WalFrame> original = WriteValidLog();
        byte[] valid = File.ReadAllBytes(_path);
        var random = new Random(5);

        for (int i = 0; i < Iterations; i++)
        {
            byte[] mutated = (byte[])valid.Clone();
            int flips = random.Next(1, 4);
            for (int f = 0; f < flips; f++)
            {
                mutated[random.Next(mutated.Length)] ^= (byte)random.Next(1, 256);
            }

            File.WriteAllBytes(_path, mutated);
            using var wal = new WriteAheadLog(_path);
            try
            {
                List<WalFrame> read = wal.ReadAll().ToList();
                Assert.True(read.Count <= original.Count, "the reader produced more records than were written");
                for (int k = 0; k < read.Count; k++)
                {
                    AssertSameRecord(original[k], read[k]);
                }
            }
            catch (CorruptLogException)
            {
                // The damage was detected in the middle of the log, and the reader refused the whole log.
            }
        }
    }

    [Fact]
    public void RandomPageDeltas_ApplyWithoutCrashing_AndRoundTripWhenValid()
    {
        var random = new Random(9);
        var before = new byte[PageConstants.PageSize];
        var after = new byte[PageConstants.PageSize];
        var scratch = new byte[PageConstants.PageSize * 2];

        for (int i = 0; i < Iterations; i++)
        {
            random.NextBytes(before);
            Array.Copy(before, after, before.Length);
            for (int c = random.Next(0, 300); c > 0; c--)
            {
                after[random.Next(PageConstants.UsableSize)] = (byte)random.Next(256);
            }

            int length = PageDelta.TryCompute(before, after, scratch);
            if (length < 0)
            {
                continue;
            }

            var page = (byte[])before.Clone();
            PageDelta.Apply(page, scratch.AsSpan(0, length));
            Assert.Equal(after.AsSpan(0, PageConstants.UsableSize).ToArray(), page.AsSpan(0, PageConstants.UsableSize).ToArray());
        }

        for (int i = 0; i < Iterations; i++)
        {
            var garbage = new byte[random.Next(0, 300)];
            random.NextBytes(garbage);
            var page = new byte[PageConstants.PageSize];
            try
            {
                PageDelta.Apply(page, garbage);
            }
            catch (InvalidDataException)
            {
                // A malformed delta is refused with the documented exception.
            }
        }
    }

    /// <summary>Writes a log of several transactions and returns the records in the order written.</summary>
    private List<WalFrame> WriteValidLog()
    {
        var written = new List<WalFrame>();
        using (var wal = new WriteAheadLog(_path))
        {
            for (long txn = 1; txn <= 6; txn++)
            {
                var page = new byte[PageConstants.PageSize];
                new Random((int)txn).NextBytes(page);
                written.Add(Log(wal, new WalFrame(txn, WalFrameType.PageImage, txn, page)));
                written.Add(Log(wal, new WalFrame(txn, WalFrameType.Commit, 0, Array.Empty<byte>())));
            }

            wal.Sync();
        }

        return written;
    }

    private static WalFrame Log(WriteAheadLog wal, WalFrame frame)
    {
        wal.Append(frame);
        return frame;
    }

    private static void AssertSameRecord(WalFrame expected, WalFrame actual)
    {
        Assert.Equal(expected.TransactionId, actual.TransactionId);
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.PageId, actual.PageId);
        Assert.Equal(expected.Payload, actual.Payload);
    }
}
