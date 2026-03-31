using GenericLauncher.Database.Model;
using GenericLauncher.Screens.InstanceDetails;
using Xunit;

namespace GenericLauncher.Tests.Minecraft;

public sealed class InstanceDetailsDeleteBehaviorTest
{
    [Fact]
    public void ReadyStoppedInstance_ShowsNormalDeleteOnly()
    {
        var instance = CreateInstance(MinecraftInstanceState.Ready);
        var viewModel = new InstanceDetailsViewModel(instance, null, null, null, null, null);

        Assert.True(viewModel.CanDelete);
        Assert.False(viewModel.CanForceDelete);
        Assert.False(viewModel.IsDeleteFailed);
    }

    [Fact]
    public void InstallingStoppedInstance_ShowsFallbackForceDelete()
    {
        var instance = CreateInstance(MinecraftInstanceState.Installing);
        var viewModel = new InstanceDetailsViewModel(instance, null, null, null, null, null);

        Assert.False(viewModel.CanDelete);
        Assert.True(viewModel.CanForceDelete);
    }

    [Fact]
    public void DeleteFailedState_UsesDedicatedDeleteFailedPathInsteadOfFallbackForceDelete()
    {
        var instance = CreateInstance(MinecraftInstanceState.DeleteFailed);
        var viewModel = new InstanceDetailsViewModel(instance, null, null, null, null, null);

        Assert.True(viewModel.IsDeleteFailed);
        Assert.False(viewModel.CanForceDelete);
    }

    private static MinecraftInstance CreateInstance(MinecraftInstanceState state) =>
        new(
            "test-instance",
            "1.21.1",
            "1.21.1",
            MinecraftInstanceModLoader.Vanilla,
            null,
            state,
            "release",
            "folder",
            21,
            "",
            "",
            "",
            [],
            [],
            []);
}