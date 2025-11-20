using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace GenericLauncher.Minecraft;

/// <summary>
/// Downloading a Minecraft versions consists of multiple steps and reporting meaningful progress
/// is hard. All the downloads, mentioned below, are downloaded in parallel, with limited number of
/// download threads. So each step can report progress from different, and also multiple different,
/// threads. Here we combine all of them, in a thread-safe way, and report good progress updates on
/// a single thread.
///
/// There are separate progress updates for the Minecraft's jar file, the Java installation, assets
/// downloads and libraries downloads, which produces hundreds of their progress updates. Thus, to
/// prevent UI stutter on low-end computers caused by GC, we are using struct-based messages that
/// are stack-allocated.
///
/// Minecraft needs:
///   * downloading a JSON file describing all the files needed
///   * downloading the client.jar, that is the Minecraft game
///   * downloading all the assets, like images and sounds
///   * downloading 3rd party libraries' jar files
///   * downloading a specific Java version
/// </summary>
public sealed class ThreadSafeInstallProgressReporter : IAsyncDisposable
{
    public readonly record struct InstallProgress(
        string InstanceId,
        bool IsValidMinecraftVersion,
        uint MinecraftDownloadProgress = 0,
        uint AssetsDownloadProgress = 0,
        uint LibrariesDownloadProgress = 0,
        uint JavaDownloadProgress = 0
    );

    private enum UpdateTarget : byte
    {
        Valid,
        Minecraft,
        Assets,
        Libraries,
        Java,
    }

    // Struct-based message passing for zero-allocation and not stressing GC.
    private readonly struct ProgressUpdate(UpdateTarget target, uint value = 0)
    {
        public readonly UpdateTarget Target = target;
        public readonly uint Value = value;
    }

    private readonly IProgress<InstallProgress> _progress;
    private readonly Channel<ProgressUpdate> _channel;
    private readonly Task _processingTask;
    private readonly CancellationTokenSource _cancellationTokenSource;

    // Access from a single consumer only, inside the _processingTask
    private InstallProgress _currentProgress;

    public ThreadSafeInstallProgressReporter(string instanceId, IProgress<InstallProgress> progress)
    {
        _progress = progress;
        _currentProgress = new InstallProgress(instanceId, true);
        _cancellationTokenSource = new CancellationTokenSource();

        // Create an unbounded channel for progress updates with one writer, so multiple threads
        // reporting are blocked until current write is finished.
        _channel = Channel.CreateUnbounded<ProgressUpdate>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        // Start the single-threaded background processing task. It will both process data and
        // report the progress on the same/one thread.
        _processingTask = Task.Run(ProcessProgressUpdates, _cancellationTokenSource.Token)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    // TODO: log and handle the problem
                }
            });
    }

    public void ReportStart()
    {
        EnqueueUpdate(new ProgressUpdate(UpdateTarget.Valid));
    }

    public void ReportMinecraftDownloadProgress(uint progress)
    {
        EnqueueUpdate(new ProgressUpdate(UpdateTarget.Minecraft, progress));
    }

    public void ReportAssetsDownloadProgress(uint progress)
    {
        EnqueueUpdate(new ProgressUpdate(UpdateTarget.Assets, progress));
    }

    public void ReportLibrariesDownloadProgress(uint progress)
    {
        EnqueueUpdate(new ProgressUpdate(UpdateTarget.Libraries, progress));
    }

    public void ReportJavaDownloadProgress(uint progress)
    {
        EnqueueUpdate(new ProgressUpdate(UpdateTarget.Java, progress));
    }

    private void EnqueueUpdate(ProgressUpdate update)
    {
        try
        {
            // Atomic/thread-safe write into a channel with single configured writer
            _channel.Writer.TryWrite(update);
        }
        catch (ChannelClosedException)
        {
            // Expected when canceled
        }
    }

    private async Task ProcessProgressUpdates()
    {
        try
        {
            await foreach (var msg in _channel.Reader.ReadAllAsync(_cancellationTokenSource.Token))
            {
                // Always do a monotonic update with Math.Max() to ensure progress never decreases
                // even if download threads race.

                switch (msg.Target)
                {
                    case UpdateTarget.Valid:
                        _currentProgress = _currentProgress with
                        {
                            IsValidMinecraftVersion = true,
                            MinecraftDownloadProgress = 0,
                            AssetsDownloadProgress = 0,
                            LibrariesDownloadProgress = 0,
                            JavaDownloadProgress = 0,
                        };
                        break;

                    case UpdateTarget.Minecraft:
                        var newMc = Math.Max(_currentProgress.MinecraftDownloadProgress, msg.Value);
                        _currentProgress = _currentProgress with { MinecraftDownloadProgress = newMc };
                        break;

                    case UpdateTarget.Assets:
                        var newAssets = Math.Max(_currentProgress.AssetsDownloadProgress, msg.Value);
                        _currentProgress = _currentProgress with { AssetsDownloadProgress = newAssets };
                        break;

                    case UpdateTarget.Libraries:
                        var newLibs = Math.Max(_currentProgress.LibrariesDownloadProgress, msg.Value);
                        _currentProgress = _currentProgress with { LibrariesDownloadProgress = newLibs };
                        break;

                    case UpdateTarget.Java:
                        var newJava = Math.Max(_currentProgress.JavaDownloadProgress, msg.Value);
                        _currentProgress = _currentProgress with { JavaDownloadProgress = newJava };
                        break;

                    default:
                        return;
                }

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
            _channel.Writer.TryComplete();

            // Wait for the processing task to complete -- TODO: with timeout?
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
