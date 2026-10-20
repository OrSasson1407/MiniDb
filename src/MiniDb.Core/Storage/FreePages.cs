using System.Buffers.Binary;
using MiniDb.Database;

namespace MiniDb.Storage;

/// <summary>
/// Pages that no longer hold data, chained into a free list. The header page keeps the first free page id, and each free
/// page keeps the id of the next one in its first eight bytes. Every change goes through the buffer pool, so it is logged
/// like any other write and undone by a rollback. A free page is zeroed when it is handed out.
/// </summary>
internal static class FreePages
{
    /// <summary>Returns a free page, or 0 when there is none. The page is zeroed and not pinned.</summary>
    public static long Take(IBufferPool pool)
    {
        using PinnedPage header = pool.Pin(DatabaseFile.HeaderPageId);
        long head = BinaryPrimitives.ReadInt64LittleEndian(header.Data[HeaderCodec.FreeListOffset..]);
        if (head == 0)
        {
            return 0;
        }

        using PinnedPage page = pool.Pin(head);
        long next = BinaryPrimitives.ReadInt64LittleEndian(page.Data);
        BinaryPrimitives.WriteInt64LittleEndian(header.Data[HeaderCodec.FreeListOffset..], next);
        header.MarkDirty();
        page.Data.Clear();
        page.MarkDirty();
        return head;
    }

    /// <summary>Adds a page to the front of the free list.</summary>
    public static void Push(IBufferPool pool, long pageId)
    {
        using PinnedPage header = pool.Pin(DatabaseFile.HeaderPageId);
        using PinnedPage page = pool.Pin(pageId);
        page.Data.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(page.Data, BinaryPrimitives.ReadInt64LittleEndian(header.Data[HeaderCodec.FreeListOffset..]));
        page.MarkDirty();
        BinaryPrimitives.WriteInt64LittleEndian(header.Data[HeaderCodec.FreeListOffset..], pageId);
        header.MarkDirty();
    }
}
