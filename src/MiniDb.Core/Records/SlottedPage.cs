using System.Buffers.Binary;
using MiniDb.Storage;

namespace MiniDb.Records;

/// <summary>
/// View over one page laid out as a slotted page. The slot array grows from the start of the page,
/// record bytes grow from the end. Deleted records leave a hole that is not reclaimed (no compaction in v1).
/// </summary>
public readonly ref struct SlottedPage
{
    public const int HeaderSize = 25;
    public const int SlotSize = 4;
    public const int MaxRecordSize = PageConstants.UsableSize - HeaderSize - SlotSize;

    private const int TypeOffset = 0;
    private const int PageIdOffset = 1;
    private const int SlotCountOffset = 13;
    private const int FreeOffset = 15;
    private const int NextPageOffset = 17;

    private readonly Span<byte> _page;

    private SlottedPage(Span<byte> page)
    {
        _page = page;
    }

    /// <summary>Clears <paramref name="page"/> and writes an empty slotted-page header.</summary>
    public static SlottedPage Format(Span<byte> page, long pageId, PageType type)
    {
        page.Clear();
        page[TypeOffset] = (byte)type;
        BinaryPrimitives.WriteInt64LittleEndian(page[PageIdOffset..], pageId);
        BinaryPrimitives.WriteUInt16LittleEndian(page[FreeOffset..], PageConstants.UsableSize);
        return new SlottedPage(page);
    }

    public static SlottedPage Open(Span<byte> page) => new(page);

    public int SlotCount => BinaryPrimitives.ReadUInt16LittleEndian(_page[SlotCountOffset..]);

    public long NextPage
    {
        get => BinaryPrimitives.ReadInt64LittleEndian(_page[NextPageOffset..]);
        set => BinaryPrimitives.WriteInt64LittleEndian(_page[NextPageOffset..], value);
    }

    /// <summary>Bytes available for a new record, counting the slot entry it needs.</summary>
    public int FreeSpace => Math.Max(0, ReadFreeOffset() - (HeaderSize + SlotCount * SlotSize) - SlotSize);

    /// <summary>Appends a record and returns its slot index, or -1 if it does not fit.</summary>
    public int Insert(ReadOnlySpan<byte> record)
    {
        if (record.IsEmpty)
        {
            throw new ArgumentException("A record cannot be empty.", nameof(record));
        }

        if (record.Length > FreeSpace)
        {
            return -1;
        }

        int slot = SlotCount;
        int offset = ReadFreeOffset() - record.Length;
        record.CopyTo(_page[offset..]);
        WriteSlot(slot, offset, record.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(_page[FreeOffset..], (ushort)offset);
        BinaryPrimitives.WriteUInt16LittleEndian(_page[SlotCountOffset..], (ushort)(slot + 1));
        return slot;
    }

    /// <summary>Largest record this page could take if its holes were compacted first.</summary>
    public int ReclaimableSpace
    {
        get
        {
            int live = 0;
            for (int i = 0; i < SlotCount; i++)
            {
                (_, int length) = ReadSlot(i);
                live += length;
            }

            return PageConstants.UsableSize - HeaderSize - SlotCount * SlotSize - live - SlotSize;
        }
    }

    /// <summary>
    /// Inserts a record as slot <paramref name="index"/>, moving later slots up so the slot order is kept.
    /// Returns false when the record does not fit, even after compaction.
    /// </summary>
    public bool InsertAt(int index, ReadOnlySpan<byte> record)
    {
        if (record.IsEmpty)
        {
            throw new ArgumentException("A record cannot be empty.", nameof(record));
        }

        if (index < 0 || index > SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (record.Length > FreeSpace)
        {
            if (record.Length > ReclaimableSpace)
            {
                return false;
            }

            Compact();
        }

        int count = SlotCount;
        int shift = HeaderSize + index * SlotSize;
        _page.Slice(shift, (count - index) * SlotSize).CopyTo(_page[(shift + SlotSize)..]);
        int offset = ReadFreeOffset() - record.Length;
        record.CopyTo(_page[offset..]);
        WriteSlot(index, offset, record.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(_page[FreeOffset..], (ushort)offset);
        BinaryPrimitives.WriteUInt16LittleEndian(_page[SlotCountOffset..], (ushort)(count + 1));
        return true;
    }

    /// <summary>Removes slot <paramref name="index"/>; later slots move down. The record bytes stay until compaction.</summary>
    public void RemoveAt(int index)
    {
        CheckSlot(index);
        int count = SlotCount;
        int at = HeaderSize + index * SlotSize;
        _page.Slice(at + SlotSize, (count - index - 1) * SlotSize).CopyTo(_page[at..]);
        BinaryPrimitives.WriteUInt16LittleEndian(_page[SlotCountOffset..], (ushort)(count - 1));
    }

    /// <summary>Returns the record in <paramref name="slot"/>, or an empty span if it was deleted.</summary>
    public ReadOnlySpan<byte> Read(int slot)
    {
        CheckSlot(slot);
        (int offset, int length) = ReadSlot(slot);
        return length == 0 ? ReadOnlySpan<byte>.Empty : _page.Slice(offset, length);
    }

    /// <summary>Replaces the record in a live slot with one no longer than the current record.</summary>
    public void Overwrite(int slot, ReadOnlySpan<byte> record)
    {
        CheckSlot(slot);
        (int offset, int length) = ReadSlot(slot);
        if (length == 0 || record.Length > length)
        {
            throw new ArgumentException("The new record must fit in the slot's current length.", nameof(record));
        }

        record.CopyTo(_page[offset..]);
        WriteSlot(slot, offset, record.Length);
    }

    /// <summary>Marks the record in <paramref name="slot"/> as deleted. Returns false if it was already deleted.</summary>
    public bool Delete(int slot)
    {
        CheckSlot(slot);
        (int offset, int length) = ReadSlot(slot);
        if (length == 0)
        {
            return false;
        }

        WriteSlot(slot, offset, 0);
        return true;
    }

    /// <summary>Moves the live records back to the end of the page, closing the holes that removed records left.</summary>
    private void Compact()
    {
        int count = SlotCount;
        var live = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            live[i] = Read(i).ToArray();
        }

        int free = PageConstants.UsableSize;
        for (int i = 0; i < count; i++)
        {
            if (live[i].Length == 0)
            {
                WriteSlot(i, 0, 0);
                continue;
            }

            free -= live[i].Length;
            live[i].AsSpan().CopyTo(_page[free..]);
            WriteSlot(i, free, live[i].Length);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(_page[FreeOffset..], (ushort)free);
    }

    private int ReadFreeOffset() => BinaryPrimitives.ReadUInt16LittleEndian(_page[FreeOffset..]);

    private (int Offset, int Length) ReadSlot(int slot)
    {
        int at = HeaderSize + slot * SlotSize;
        return (BinaryPrimitives.ReadUInt16LittleEndian(_page[at..]),
                BinaryPrimitives.ReadUInt16LittleEndian(_page[(at + 2)..]));
    }

    private void WriteSlot(int slot, int offset, int length)
    {
        int at = HeaderSize + slot * SlotSize;
        BinaryPrimitives.WriteUInt16LittleEndian(_page[at..], (ushort)offset);
        BinaryPrimitives.WriteUInt16LittleEndian(_page[(at + 2)..], (ushort)length);
    }

    private void CheckSlot(int slot)
    {
        if (slot < 0 || slot >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), $"Slot {slot} does not exist.");
        }
    }
}
