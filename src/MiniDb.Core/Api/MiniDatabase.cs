using MiniDb.Catalog;
using MiniDb.Database;
using MiniDb.Index;
using MiniDb.Query;
using MiniDb.Records;
using MiniDb.Storage;
using MiniDb.Transactions;

namespace MiniDb.Api;

/// <summary>
/// An open database file (<c>path</c>) with its write-ahead log (<c>path.wal</c>). Many threads may read at the same
/// time; one writer at a time holds the database exclusively, so readers wait for a writer to commit. An explicit
/// transaction holds the write lock until it is committed or rolled back. Dispose it to checkpoint and close the files.
/// </summary>
public sealed class MiniDatabase : IDisposable
{
    private readonly IPageStore _store;
    private readonly WriteAheadLog _wal;
    private readonly BufferPool _pool;
    private readonly TransactionManager _manager;
    private readonly TableCatalog _catalog;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
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
        _manager.RolledBack += _catalog.Invalidate;
    }

    /// <summary>Creates the file if it does not exist, otherwise recovers it from the WAL and opens it.</summary>
    public static MiniDatabase Open(string path, DatabaseOptions? options = null)
    {
        options ??= new DatabaseOptions();
        bool exists = File.Exists(path);
        bool isNew = !exists || new FileInfo(path).Length == 0;
        if (exists && !isNew)
        {
            ValidateMagic(path);
        }

        IPageStore store = new FilePageStore(path, create: !exists);
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
        InWriteTransaction(() =>
        {
            if (_catalog.Find(schema.Name) is not null)
            {
                throw new TableAlreadyExistsException(schema.Name);
            }

            long primaryRoot = ClusteredTree.Create(_pool).RootPage;
            _catalog.Create(new TableInfo(schema, primaryRoot, new Dictionary<string, long>()));
            return true;
        });
    }

    /// <summary>Runs one SQL statement. SELECT returns rows; INSERT and DELETE report the rows they changed.</summary>
    public QueryResult Execute(string sql)
    {
        EnsureOpen();
        try
        {
            return SqlEngine.Execute(this, sql);
        }
        catch (ArgumentException ex)
        {
            throw new MiniDbException(ex.Message);
        }
    }

    public Table GetTable(string name)
    {
        EnsureOpen();
        return ReadLocked(() =>
        {
            if (_catalog.Find(name) is null)
            {
                throw new TableNotFoundException(name);
            }

            return new Table(this, name);
        });
    }

    /// <summary>
    /// Starts an explicit transaction. Table writes until it is committed or rolled back belong to it, and no other
    /// thread can write or read until then. Dispose it to roll back if it is still open.
    /// </summary>
    public Transaction BeginTransaction()
    {
        EnsureOpen();
        _lock.EnterWriteLock();
        try
        {
            Transaction transaction = _manager.Begin();
            transaction.Finished = _lock.ExitWriteLock;
            _current = transaction;
            return transaction;
        }
        catch
        {
            _lock.ExitWriteLock();
            throw;
        }
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

        _lock.EnterWriteLock();
        try
        {
            _manager.Checkpoint();
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        _wal.Dispose();
        _store.Dispose();
    }

    internal BufferPool Pool => _pool;

    internal TableCatalog Catalog => _catalog;

    /// <summary>Runs <paramref name="read"/> under the shared lock: other readers may run alongside it.</summary>
    internal T ReadLocked<T>(Func<T> read)
    {
        // A thread that already holds the write lock (inside a transaction) reads what it has written.
        if (_lock.IsWriteLockHeld)
        {
            return read();
        }

        _lock.EnterReadLock();
        try
        {
            return read();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Enumerates rows under the shared lock, which is held until the enumeration ends.</summary>
    internal IEnumerable<T> ReadLockedSequence<T>(Func<IEnumerable<T>> rows)
    {
        bool taken = !_lock.IsWriteLockHeld;
        if (taken)
        {
            _lock.EnterReadLock();
        }

        try
        {
            foreach (T row in rows())
            {
                yield return row;
            }
        }
        finally
        {
            if (taken)
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> in the open transaction, or in a new one that commits when it returns.
    /// Called while this thread already holds the write lock (inside a transaction), it joins that transaction.
    /// </summary>
    internal T InWriteTransaction<T>(Func<T> action) => InWriteTransaction(action, static run => run());

    /// <summary>Same as the overload without state; a static action and a state value avoid allocating a closure per call.</summary>
    internal T InWriteTransaction<TState, T>(TState state, Func<TState, T> action)
    {
        EnsureOpen();
        if (_lock.IsWriteLockHeld && _current is { IsFinished: false } open)
        {
            try
            {
                return action(state);
            }
            catch
            {
                // A statement that fails halfway cannot be committed; abort the whole transaction.
                open.Rollback();
                throw;
            }
        }

        _lock.EnterWriteLock();
        try
        {
            using Transaction transaction = _manager.Begin();
            _current = transaction;
            T result = action(state);
            transaction.Commit();
            return result;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private static void ValidateMagic(string path)
    {
        var start = new byte[8];
        using FileStream stream = File.OpenRead(path);
        int read = stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
        if (read < start.Length || !HeaderCodec.HasMagic(start))
        {
            throw new InvalidDatabaseException("magic mismatch, the file is not a MiniDb database");
        }
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
