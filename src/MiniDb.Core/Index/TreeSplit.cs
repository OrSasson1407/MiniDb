namespace MiniDb.Index;

/// <summary>What a child hands to its parent after splitting: the separator key and the new right-hand page.</summary>
internal readonly record struct TreeSplit(TreeKey Key, long PageId);
