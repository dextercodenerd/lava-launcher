using CommunityToolkit.Mvvm.ComponentModel;
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

    [ObservableProperty] private ThreadSafeInstallProgressReporter.InstallProgress? _progress;

    public string ProgressMessage
    {
        get
        {
            if (!Progress.HasValue)
            {
                return "100";
            }

            var average = (long)Progress.Value.MinecraftDownloadProgress * Progress.Value.AssetsDownloadProgress *
                Progress.Value.LibrariesDownloadProgress * Progress.Value.JavaDownloadProgress / 1000000;

            return average.ToString();
        }
    }

    public MinecraftInstanceItem(MinecraftInstance instance,
        ThreadSafeInstallProgressReporter.InstallProgress? progress)
    {
        Instance = instance;
        Progress = progress;
    }

    partial void OnProgressChanged(ThreadSafeInstallProgressReporter.InstallProgress? value)
    {
        // Computed properties, like our `ProgressMessage`, cannot be annotated as
        // [ObservableProperty], so we notify this change manually and templated controls pick-up
        // the change, and update their UI.
        OnPropertyChanged(nameof(ProgressMessage));
    }
}