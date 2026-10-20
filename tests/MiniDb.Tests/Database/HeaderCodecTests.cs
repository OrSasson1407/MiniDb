using MiniDb.Database;
using MiniDb.Storage;

namespace MiniDb.Tests.Database;

public class HeaderCodecTests
{
    [Fact]
    public void HeaderRoundTrips()
    {
        var header = new DatabaseHeader(FormatVersion: 1, PageCount: 3, CatalogRootPage: 1, FreeListHead: 0);
        var page = new byte[PageConstants.PageSize];

        HeaderCodec.Write(header, page);
        var read = HeaderCodec.Read(page);

        Assert.Equal(header, read);
    }

    [Fact]
    public void Read_RejectsPageWithWrongMagic()
    {
        var page = new byte[PageConstants.PageSize];

        var ex = Assert.Throws<InvalidDatabaseException>(() => HeaderCodec.Read(page));

        Assert.Contains("magic", ex.Message);
    }

    [Fact]
    public void Read_RejectsPageWhoseChecksumDoesNotMatch()
    {
        var page = new byte[PageConstants.PageSize];
        HeaderCodec.Write(new DatabaseHeader(1, 1, 0, 0), page);
        page[20] ^= 0xFF;

        var ex = Assert.Throws<InvalidDatabaseException>(() => HeaderCodec.Read(page));

        Assert.Contains("checksum", ex.Message);
    }
}
