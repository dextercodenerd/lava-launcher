using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using GenericLauncher.Misc;
using Xunit;

namespace GenericLauncher.Tests.Misc;

public class ZipUtilsTest
{
    [Fact]
    public async Task ExtractEntriesAsync_ExtractsAllRequestedEntries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var destinationA = Path.Combine(root, "nested", "first.txt");
        var destinationB = Path.Combine(root, "other", "second.txt");
        await using var stream = new MemoryStream(CreateArchiveBytes(
            ("a/first.txt", "first-content"),
            ("b/second.txt", "second-content")));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false);

        await ZipUtils.ExtractEntriesAsync(
            archive,
            [
                new ZipExtractionRequest("a/first.txt", destinationA),
                new ZipExtractionRequest("b/second.txt", destinationB),
            ],
            cancellationToken);

        Assert.Equal("first-content", await File.ReadAllTextAsync(destinationA, cancellationToken));
        Assert.Equal("second-content", await File.ReadAllTextAsync(destinationB, cancellationToken));
    }

    [Fact]
    public void ExtractEntries_ExtractsRequestedEntry()
    {
        var root = CreateTempRoot();
        var destination = Path.Combine(root, "payload", "client.lzma");
        using var stream = new MemoryStream(CreateArchiveBytes(
            ("data/client.lzma", "patch-data")));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false);

        ZipUtils.ExtractEntries(
            archive,
            [
                new ZipExtractionRequest("data/client.lzma", destination),
            ]);

        Assert.Equal("patch-data", File.ReadAllText(destination));
    }

    [Fact]
    public async Task ExtractEntriesAsync_ThrowsWhenEntryIsMissing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var destination = Path.Combine(root, "missing", "file.txt");
        await using var stream = new MemoryStream(CreateArchiveBytes(
            ("present.txt", "content")));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ZipUtils.ExtractEntriesAsync(
            archive,
            [
                new ZipExtractionRequest("missing.txt", destination),
            ],
            cancellationToken));

        Assert.Contains("Zip entry 'missing.txt' is missing", ex.Message, StringComparison.Ordinal);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "lavalancher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static byte[] CreateArchiveBytes(params (string EntryName, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var entryData in entries)
            {
                var entry = archive.CreateEntry(entryData.EntryName);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(entryData.Content);
            }
        }

        return stream.ToArray();
    }
}
