using MiniDb.Storage;
using MiniDb.Transactions;

namespace MiniDb.Tests.Transactions;

public class PageDeltaTests
{
    [Fact]
    public void Compute_ThenApply_ReproducesTheAfterImage()
    {
        var before = new byte[PageConstants.PageSize];
        var after = new byte[PageConstants.PageSize];
        after[100] = 1;
        after[101] = 2;
        after[3000] = 9;
        var delta = new byte[PageConstants.PageSize * 2];

        int length = PageDelta.TryCompute(before, after, delta);
        var target = (byte[])before.Clone();
        PageDelta.Apply(target, delta.AsSpan(0, length));

        Assert.True(length > 0 && length < PageConstants.PageSize);
        Assert.Equal(after[..PageConstants.UsableSize], target[..PageConstants.UsableSize]);
    }

    [Fact]
    public void Compute_ReturnsMinusOne_WhenEveryByteChanged()
    {
        var before = new byte[PageConstants.PageSize];
        var after = Enumerable.Repeat((byte)7, PageConstants.PageSize).ToArray();

        Assert.Equal(-1, PageDelta.TryCompute(before, after, new byte[PageConstants.PageSize * 2]));
    }
}
