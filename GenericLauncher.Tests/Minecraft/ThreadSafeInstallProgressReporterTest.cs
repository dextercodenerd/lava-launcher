using GenericLauncher.Minecraft;
using Xunit;

namespace GenericLauncher.Tests.Minecraft;

public class ThreadSafeInstallProgressReporterTest
{
    [Fact]
    public void GetOverallProgressPercent_DownloadPhaseAdvancesBeforeModLoaderStarts()
    {
        var progress = new ThreadSafeInstallProgressReporter.InstallProgress(
            "instance",
            true,
            MinecraftDownloadProgress: 80,
            AssetsDownloadProgress: 60,
            LibrariesDownloadProgress: 40,
            JavaDownloadProgress: 20,
            ModLoaderInstallProgress: 0);

        var overall = progress.GetOverallProgressPercent();

        Assert.Equal(45u, overall);
    }

    [Fact]
    public void GetOverallProgressPercent_ReachesHundredWhenAllStagesComplete()
    {
        var progress = new ThreadSafeInstallProgressReporter.InstallProgress(
            "instance",
            true,
            MinecraftDownloadProgress: 100,
            AssetsDownloadProgress: 100,
            LibrariesDownloadProgress: 100,
            JavaDownloadProgress: 100,
            ModLoaderInstallProgress: 100);

        Assert.Equal(100u, progress.GetOverallProgressPercent());
    }
}
