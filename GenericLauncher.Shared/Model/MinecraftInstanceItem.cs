using GenericLauncher.Database;
using GenericLauncher.Minecraft;

namespace GenericLauncher.Model;

public record MinecraftInstanceItem(
    MinecraftInstance Instance,
    ThreadSafeInstallProgressReporter.InstallProgress? Progress
)
{
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
};
