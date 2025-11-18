using System;
using System.Threading;
using System.Threading.Tasks;

namespace GenericLauncher.Misc;

/// <summary>
/// An asynchronous reader-writer lock using two SemaphoreSlims.
///
/// This version uses an "Execute" pattern, removing the need for
/// helper IDisposable classes.
///
/// This lock is:
/// - Reader-Preferential (writers can be starved)
/// - NON-REENTRANT (a read lock CANNOT be upgraded to a write lock)
///
/// DO NOT DO THIS - IT WILL DEADLOCK
/// await myLock.ExecuteReadAsync(async () =>
/// {
///     Console.WriteLine("Read lock acquired. Trying to upgrade...");
///
///     // The read lock is *holding* _writerGate.
///     // This call will *wait for* _writerGate.
///     // This is a classic deadlock.
///     await myLock.ExecuteWriteAsync(async () =>
///     {
///         Console.WriteLine("Write lock acquired!"); // This line will never run
///     });
/// });
///
/// </summary>
public sealed class AsyncRwLock : IAsyncDisposable
{
    private readonly SemaphoreSlim _readerLock;
    private readonly SemaphoreSlim _writerLock = new(1, 1);

    // Prevents new readers when a writer is waiting
    private readonly SemaphoreSlim _blockReadersWhenPendingWriterLock = new(1, 1);

    private readonly int _maxReaders;
    private volatile bool _isDisposed = false;

    public AsyncRwLock(int maxReaders)
    {
        if (maxReaders <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxReaders),
                "Maximum reader count must be greater than 0.");
        }

        _maxReaders = maxReaders;
        _readerLock = new SemaphoreSlim(_maxReaders, _maxReaders);
    }

    public async Task<T> ExecuteReadAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        await _blockReadersWhenPendingWriterLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            // Wait for a slot in the reader pool
            await _readerLock.WaitAsync(cancellationToken);
        }
        finally
        {
            _blockReadersWhenPendingWriterLock.Release();
        }

        try
        {
            return await action();
        }
        finally
        {
            _readerLock.Release();
        }
    }

    public async Task<T> ExecuteWriteAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // Wait for our turn to be the __only__ writer attempting to drain the reader locks.
        await _writerLock.WaitAsync(cancellationToken);

        // Block new readers from entering
        await _blockReadersWhenPendingWriterLock.WaitAsync(cancellationToken);

        // We need to track the number of acquired locks, because if the task is cancelled we need
        // to release just that number of permits.
        var acquiredPermits = 0;

        try
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            // Acquire __all__ the reader locks. This waits for all active readers to finish and
            // blocks any new readers from starting.
            for (var i = 0; i < _maxReaders; i++)
            {
                await _readerLock.WaitAsync(cancellationToken);
                acquiredPermits++;
            }

            return await action();
        }
        finally
        {
            if (acquiredPermits > 0)
            {
                _readerLock.Release(acquiredPermits);
            }

            _blockReadersWhenPendingWriterLock.Release();
            _writerLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        try
        {
            // Before disposing, we have to wait for all activity to stop, both writer and readers
            await _writerLock.WaitAsync();
            await _blockReadersWhenPendingWriterLock.WaitAsync();

            for (var i = 0; i < _maxReaders; i++)
            {
                await _readerLock.WaitAsync();
            }
        }
        finally
        {
            _readerLock.Dispose();
            _writerLock.Dispose();
            _blockReadersWhenPendingWriterLock.Dispose();
        }
    }
}