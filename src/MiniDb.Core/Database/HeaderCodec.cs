using System.Buffers.Binary;
using System.Text;
using MiniDb.Storage;

namespace MiniDb.Database;

/// <summary>Converts the header to and from the bytes of page 0.</summary>
public static class HeaderCodec
{
    public const int CurrentFormatVersion = 1;

    private const int VersionOffset = 8;
    private const int PageSizeOffset = 12;
    private const int PageCountOffset = 16;
    private const int CatalogRootOffset = 24;
    /// <summary>Offset of the first free page id. <see cref="Storage.FreePages"/> reads and writes it in place.</summary>
    public const int FreeListOffset = 32;
    private const int ChecksumOffset = PageConstants.PageSize - sizeof(uint);

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MINIDB01");

    /// <summary>True if the first bytes of a file carry the MiniDb magic.</summary>
    public static bool HasMagic(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= Magic.Length && bytes[..Magic.Length].SequenceEqual(Magic);

    public static void Write(DatabaseHeader header, Span<byte> page)
    {
        page.Clear();
        Magic.CopyTo(page);
        BinaryPrimitives.WriteInt32LittleEndian(page[VersionOffset..], header.FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(page[PageSizeOffset..], PageConstants.PageSize);
        BinaryPrimitives.WriteInt64LittleEndian(page[PageCountOffset..], header.PageCount);
        BinaryPrimitives.WriteInt64LittleEndian(page[CatalogRootOffset..], header.CatalogRootPage);
        BinaryPrimitives.WriteInt64LittleEndian(page[FreeListOffset..], header.FreeListHead);
        BinaryPrimitives.WriteUInt32LittleEndian(page[ChecksumOffset..], Checksum.Compute(page[..ChecksumOffset]));
    }

    public static DatabaseHeader Read(ReadOnlySpan<byte> page)
    {
        if (!page[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDatabaseException("magic mismatch, the file is not a MiniDb database");
        }

        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(page[ChecksumOffset..]);
        if (Checksum.Compute(page[..ChecksumOffset]) != stored)
        {
            throw new InvalidDatabaseException("header checksum mismatch");
        }

        if (BinaryPrimitives.ReadInt32LittleEndian(page[PageSizeOffset..]) != PageConstants.PageSize)
        {
            throw new InvalidDatabaseException("unexpected page size");
        }

        return new DatabaseHeader(
            BinaryPrimitives.ReadInt32LittleEndian(page[VersionOffset..]),
            BinaryPrimitives.ReadInt64LittleEndian(page[PageCountOffset..]),
            BinaryPrimitives.ReadInt64LittleEndian(page[CatalogRootOffset..]),
            BinaryPrimitives.ReadInt64LittleEndian(page[FreeListOffset..]));
    }
}
