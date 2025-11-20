using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace GenericLauncher.Minecraft;

public sealed class ThreadSafeInstallProgressReporter : IAsyncDisposable
{
    public readonly record struct InstallProgress(
        bool IsValidMinecraftVersion,
        uint MinecraftDownloadProgress = 0,
        uint AssetsDownloadProgress = 0,
        uint LibrariesDownloadProgress = 0,
        uint JavaDownloadProgress = 0
    );

    private record ProgressUpdate(Func<InstallProgress, InstallProgress> UpdateFunction);

    private readonly IProgress<InstallProgress> _progress;
    private readonly Channel<ProgressUpdate> _channel;
    private readonly Task _processingTask;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private InstallProgress _currentProgress;

    public ThreadSafeInstallProgressReporter(IProgress<InstallProgress> progress)
    {
        _progress = progress;
        _currentProgress = new InstallProgress(true);
        _cancellationTokenSource = new CancellationTokenSource();

        // Create an unbounded channel for progress updates
        _channel = Channel.CreateUnbounded<ProgressUpdate>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        // Start the background processing task
        _processingTask = Task.Run(ProcessProgressUpdates, _cancellationTokenSource.Token);
    }

    public void ReportStart()
    {
        TryWriteUpdate(_ => new InstallProgress(true));
    }

    public void ReportFinished()
    {
        TryWriteUpdate(_ => new InstallProgress(true, 100, 100, 100, 100));
    }

    public void ReportMinecraftDownloadProgress(uint progress)
    {
        TryWriteUpdate(p => p with { MinecraftDownloadProgress = progress });
    }

    public void ReportAssetsDownloadProgress(uint progress)
    {
        TryWriteUpdate(p => p with { AssetsDownloadProgress = progress });
    }

    public void ReportLibrariesDownloadProgress(uint progress)
    {
        TryWriteUpdate(p => p with { LibrariesDownloadProgress = progress });
    }

    public void ReportJavaDownloadProgress(uint progress)
    {
        TryWriteUpdate(p => p with { JavaDownloadProgress = progress });
    }

    private void TryWriteUpdate(Func<InstallProgress, InstallProgress> updateFunc)
    {
        try
        {
            _channel.Writer.TryWrite(new ProgressUpdate(updateFunc));
        }
        catch (ChannelClosedException)
        {
            // Ignore if the channel is closed
        }
    }

    private async Task ProcessProgressUpdates()
    {
        try
        {
            await foreach (var update in _channel.Reader.ReadAllAsync(_cancellationTokenSource.Token))
            {
                _currentProgress = update.UpdateFunction(_currentProgress);
                _progress.Report(_currentProgress);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when canceled
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _cancellationTokenSource.CancelAsync();
            _channel.Writer.Complete();

            // Wait for the processing task to complete -- TODO: with timeout
            await _processingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        finally
        {
            _cancellationTokenSource.Dispose();
        }
    }
}
