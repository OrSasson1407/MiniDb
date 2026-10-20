using MiniDb.Catalog;
using MiniDb.Database;
using MiniDb.Index;
using MiniDb.Query;
using MiniDb.Records;
using MiniDb.Storage;
using MiniDb.Transactions;

namespace MiniDb.Api;

/// <summary>
/// An open database file (<c>path</c>) with its write-ahead log (<c>path.wal</c>).
/// Not thread-safe: use it from one thread at a time. Dispose it to checkpoint and close the files.
/// </summary>
public sealed class MiniDatabase : IDisposable
{
    private readonly IPageStore _store;
    private readonly WriteAheadLog _wal;
    private readonly BufferPool _pool;
    private readonly TransactionManager _manager;
    private readonly TableCatalog _catalog;
    private Transaction? _current;
    private bool _disposed;

    private MiniDatabase(
        IPageStore store,
        WriteAheadLog wal,
        BufferPool pool,
        TransactionManager manager,
        TableCatalog catalog)
    {
        _store = store;
        _wal = wal;
        _pool = pool;
        _manager = manager;
        _catalog = catalog;
    }

    /// <summary>Creates the file if it does not exist, otherwise recovers it from the WAL and opens it.</summary>
    public static MiniDatabase Open(string path, DatabaseOptions? options = null)
    {
        options ??= new DatabaseOptions();
        bool isNew = !File.Exists(path);
        IPageStore store = new FilePageStore(path, create: isNew);
        WriteAheadLog? wal = null;
        try
        {
            wal = new WriteAheadLog(path + ".wal");
            if (isNew)
            {
                DatabaseFile.Create(store);
                wal.Truncate();
            }
            else
            {
                Recovery.Run(store, wal);
            }

            DatabaseHeader header = DatabaseFile.Open(store);
            var pool = new BufferPool(store, options.BufferPoolPages);
            var manager = new TransactionManager(store, pool, wal, options.FsyncOnCommit, options.CheckpointThresholdBytes);
            long catalogRoot = header.CatalogRootPage;
            if (catalogRoot == 0)
            {
                catalogRoot = CreateCatalog(store, pool, manager, header);
            }

            return new MiniDatabase(store, wal, pool, manager, new TableCatalog(pool, catalogRoot));
        }
        catch
        {
            wal?.Dispose();
            store.Dispose();
            throw;
        }
    }

    public void CreateTable(TableSchema schema)
    {
        EnsureOpen();
        if (_catalog.Find(schema.Name) is not null)
        {
            throw new TableAlreadyExistsException(schema.Name);
        }

        InWriteTransaction(() =>
        {
            long heap = _pool.AllocatePage();
            using (PinnedPage page = _pool.Pin(heap))
            {
                SlottedPage.Format(page.Data, heap, PageType.Heap);
                page.MarkDirty();
            }

            long primaryRoot = BPlusTree.Create(_pool).RootPage;
            _catalog.Create(new TableInfo(schema, primaryRoot, new Dictionary<string, long>(), heap, heap));
            return true;
        });
    }

    /// <summary>Runs one SQL statement. SELECT returns rows; INSERT and DELETE report the rows they changed.</summary>
    public QueryResult Execute(string sql)
    {
        EnsureOpen();
        return SqlEngine.Run(this, sql);
    }

    public Table GetTable(string name)
    {
        EnsureOpen();
        if (_catalog.Find(name) is null)
        {
            throw new TableNotFoundException(name);
        }

        return new Table(this, name);
    }

    /// <summary>
    /// Starts an explicit transaction. Table writes until it is committed or rolled back belong to it.
    /// Dispose it to roll back if it is still open.
    /// </summary>
    public Transaction BeginTransaction()
    {
        EnsureOpen();
        Transaction transaction = _manager.Begin();
        _current = transaction;
        return transaction;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_current is { IsFinished: false } open)
        {
            open.Rollback();
        }

        _manager.Checkpoint();
        _wal.Dispose();
        _store.Dispose();
    }

    internal BufferPool Pool => _pool;

    internal TableCatalog Catalog => _catalog;

    /// <summary>Runs <paramref name="action"/> in the open transaction, or in a new one that commits when it returns.</summary>
    internal T InWriteTransaction<T>(Func<T> action)
    {
        EnsureOpen();
        if (_current is { IsFinished: false })
        {
            return action();
        }

        using Transaction transaction = _manager.Begin();
        _current = transaction;
        T result = action();
        transaction.Commit();
        return result;
    }

    private static long CreateCatalog(IPageStore store, BufferPool pool, TransactionManager manager, DatabaseHeader header)
    {
        using Transaction transaction = manager.Begin();
        long catalogRoot = TableCatalog.CreateRoot(pool);
        var page = new byte[PageConstants.PageSize];
        HeaderCodec.Write(new DatabaseHeader(header.FormatVersion, store.PageCount, catalogRoot, header.FreeListHead), page);
        transaction.WritePage(DatabaseFile.HeaderPageId, page);
        transaction.Commit();
        return catalogRoot;
    }

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
