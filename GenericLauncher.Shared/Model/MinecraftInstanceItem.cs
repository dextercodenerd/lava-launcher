using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GenericLauncher.Database.Model;
using GenericLauncher.Minecraft;

namespace GenericLauncher.Model;

/// <summary>
/// MinecraftInstanceItem is used for displaying the installed Minecraft instances on the home
/// screen.
///
/// It is a class that implements ObservableObject for performance reasons. This way, we can update
/// the progress in-place inside the collection that is bound to ItemsControl and the
/// TemplatedControl, that displays the item, will react to properties changes. This does not
/// trigger measuring, nor arranging/layout, thus prevents UI stuttering.
/// </summary>
public partial class MinecraftInstanceItem : ObservableObject
{
    public MinecraftInstance Instance { get; }

    [ObservableProperty] private MinecraftLauncher.RunningState _runningState = MinecraftLauncher.RunningState.Stopped;

    [ObservableProperty] private ThreadSafeInstallProgressReporter.InstallProgress? _progress;

    public IAsyncRelayCommand PlayCommand { get; }

    public string ProgressMessage => !Progress.HasValue
        ? "100"
        : Progress.Value.GetOverallProgressPercent().ToString();

    public MinecraftInstanceItem(MinecraftInstance instance,
        ThreadSafeInstallProgressReporter.InstallProgress? progress,
        Func<MinecraftInstanceItem, Task>? playAction = null)
    {
        Instance = instance;
        Progress = progress;
        PlayCommand = new AsyncRelayCommand(
            () => playAction?.Invoke(this) ?? Task.CompletedTask,
            CanPlay);
    }

    partial void OnProgressChanged(ThreadSafeInstallProgressReporter.InstallProgress? value)
    {
        // Computed properties, like our `ProgressMessage`, cannot be annotated as
        // [ObservableProperty], so we notify this change manually and templated controls pick-up
        // the change, and update their UI.
        OnPropertyChanged(nameof(ProgressMessage));
    }

    partial void OnRunningStateChanged(MinecraftLauncher.RunningState value)
    {
        PlayCommand.NotifyCanExecuteChanged();
    }

    private bool CanPlay() =>
        RunningState == MinecraftLauncher.RunningState.Stopped &&
        Instance.State == MinecraftInstanceState.Ready;
}
