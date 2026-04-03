using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GenericLauncher.Database.Model;
using GenericLauncher.Minecraft;
using GenericLauncher.Minecraft.ModLoaders;
using Xunit;

namespace GenericLauncher.Tests.Minecraft;

public sealed class MinecraftClassPathTest
{
    [Fact]
    public void TryGetLogicalLibraryIdentity_IgnoresVersionButKeepsClassifierAndExtension()
    {
        var baseIdentity = MinecraftClassPath.TryGetLogicalLibraryIdentity(
            "/tmp/mc/libraries/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar");
        var newerIdentity = MinecraftClassPath.TryGetLogicalLibraryIdentity(
            "/tmp/mc/libraries/org/lwjgl/lwjgl/3.4.0/lwjgl-3.4.0.jar");
        var nativesIdentity = MinecraftClassPath.TryGetLogicalLibraryIdentity(
            "/tmp/mc/libraries/org/lwjgl/lwjgl/3.4.0/lwjgl-3.4.0-natives-macos-arm64.jar");

        Assert.Equal("org.lwjgl:lwjgl@jar", baseIdentity);
        Assert.Equal(baseIdentity, newerIdentity);
        Assert.Equal("org.lwjgl:lwjgl:natives-macos-arm64@jar", nativesIdentity);
    }

    [Theory]
    [InlineData("/tmp/mc/modloaders/fabric/libraries/org/ow2/asm/asm/9.9/asm-9.9.jar")]
    [InlineData("/tmp/mc/modloaders/forge/libraries/org/ow2/asm/asm/9.9/asm-9.9.jar")]
    [InlineData("/tmp/mc/modloaders/neoforge/libraries/org/ow2/asm/asm/9.9/asm-9.9.jar")]
    public void MergeVanillaAndModLoaderLibraries_ModLoaderWinsLogicalCollision(string modLoaderAsmPath)
    {
        var merged = MinecraftClassPath.MergeVanillaAndModLoaderLibraries(
            ["/tmp/mc/libraries/org/ow2/asm/asm/9.6/asm-9.6.jar"],
            [new ResolvedModLoaderLibrary("org.ow2.asm:asm:9.9", null, modLoaderAsmPath, null)]);

        Assert.Equal([modLoaderAsmPath], merged);
    }

    [Fact]
    public void Normalize_PreservesClassifierSpecificArtifacts()
    {
        var baseJar = "/tmp/mc/libraries/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar";
        var nativesJar = "/tmp/mc/libraries/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-linux.jar";

        var normalized = MinecraftClassPath.Normalize([baseJar, nativesJar]);

        Assert.Equal([baseJar, nativesJar], normalized);
    }

    [Fact]
    public async Task NormalizePersistedClassPathAsync_PersistsOnlyWhenRepairIsNeeded()
    {
        var instance = CreateInstance(classPath:
        [
            "/tmp/mc/libraries/com/example/shared/1.0.0/shared-1.0.0.jar",
            "/tmp/mc/modloaders/fabric/libraries/com/example/shared/2.0.0/shared-2.0.0.jar",
        ]);
        string? persistedInstanceId = null;
        List<string>? persistedClassPath = null;

        var normalized = await MinecraftLauncher.NormalizePersistedClassPathAsync(instance, (instanceId, classPath) =>
        {
            persistedInstanceId = instanceId;
            persistedClassPath = classPath;
            return Task.CompletedTask;
        });

        Assert.Equal(instance.Id, persistedInstanceId);
        Assert.Equal(["/tmp/mc/modloaders/fabric/libraries/com/example/shared/2.0.0/shared-2.0.0.jar"],
            persistedClassPath);
        Assert.Equal(persistedClassPath, normalized.ClassPath);

        persistedInstanceId = null;
        persistedClassPath = null;

        var alreadyNormalized = await MinecraftLauncher.NormalizePersistedClassPathAsync(normalized,
            (_, _) =>
            {
                persistedInstanceId = "unexpected";
                return Task.CompletedTask;
            });

        Assert.Null(persistedInstanceId);
        Assert.True(alreadyNormalized.ClassPath.SequenceEqual(normalized.ClassPath));
    }

    private static MinecraftInstance CreateInstance(List<string> classPath) =>
        new(
            "instance-1",
            "1.21.1",
            "fabric-loader-0.18.5-1.21.1",
            MinecraftInstanceModLoader.Fabric,
            "0.18.5",
            MinecraftInstanceState.Ready,
            "release",
            "folder",
            21,
            "client.jar",
            "net.fabricmc.loader.impl.launch.knot.KnotClient",
            "asset-index",
            classPath,
            [],
            []);
}
