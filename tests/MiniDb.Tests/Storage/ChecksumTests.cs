using System.Text;
using MiniDb.Storage;

namespace MiniDb.Tests.Storage;

public class ChecksumTests
{
    [Fact]
    public void Crc32_MatchesStandardCheckValue()
    {
        byte[] input = Encoding.ASCII.GetBytes("123456789");

        Assert.Equal(0xCBF43926u, Checksum.Compute(input));
    }
}
