namespace MiniDb.Index;

/// <summary>Where a row lives: its heap page and slot. The B+ tree maps keys to these.</summary>
public readonly record struct RowLocation(long PageId, int Slot);
