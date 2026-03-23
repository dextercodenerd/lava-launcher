using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using GenericLauncher.Misc;
using GenericLauncher.Minecraft.Json;
using Xunit;

namespace GenericLauncher.Tests.Misc;

public sealed class FileJsonExtensionsTest : IDisposable
{
    private readonly string _tempFolder = Path.Combine(
        Path.GetTempPath(),
        "lavalancher-tests",
        nameof(FileJsonExtensionsTest),
        Guid.NewGuid().ToString("N"));
    private static System.Threading.CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeserializeJsonAsync_ReturnsParsedModel()
    {
        var result = await File.DeserializeJsonAsync(
            Path.GetFullPath("../../../Data/client_1.21.10.json"),
            MinecraftJsonContext.Default.VersionDetails,
            CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("1.21.10", result.Id);
        Assert.NotNull(result.Arguments);
    }

    [Fact]
    public async Task DeserializeJsonAsync_ReturnsNullForJsonNull()
    {
        var path = CreateTempFile("null");

        var result = await File.DeserializeJsonAsync(path, MinecraftJsonContext.Default.VersionDetails, CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task DeserializeJsonAsync_BubblesJsonExceptions()
    {
        var path = CreateTempFile("{ definitely not json }");

        await Assert.ThrowsAsync<JsonException>(() =>
            File.DeserializeJsonAsync(path, MinecraftJsonContext.Default.VersionDetails, CancellationToken));
    }

    [Fact]
    public async Task DeserializeJsonAsync_BubblesMissingFileExceptions()
    {
        Directory.CreateDirectory(_tempFolder);
        var path = Path.Combine(_tempFolder, "missing.json");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            File.DeserializeJsonAsync(path, MinecraftJsonContext.Default.VersionDetails, CancellationToken));
    }

    private string CreateTempFile(string content)
    {
        Directory.CreateDirectory(_tempFolder);
        var path = Path.Combine(_tempFolder, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempFolder))
        {
            Directory.Delete(_tempFolder, true);
        }
    }
}
