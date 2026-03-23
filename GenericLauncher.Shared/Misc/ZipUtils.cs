using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GenericLauncher.Misc;

public readonly record struct ZipExtractionRequest(string EntryName, string DestinationPath);

public static class ZipUtils
{
    public static async Task ExtractEntriesAsync(
        ZipArchive archive,
        IReadOnlyList<ZipExtractionRequest> requests,
        CancellationToken cancellationToken = default)
    {
        foreach (var request in requests)
        {
            var entry = archive.GetEntry(request.EntryName)
                        ?? throw new InvalidOperationException($"Zip entry '{request.EntryName}' is missing");
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)
                                      ?? throw new InvalidOperationException("Missing destination folder"));
            await using var entryStream = await entry.OpenAsync(cancellationToken);
            await using var destinationStream = File.Create(request.DestinationPath);
            await entryStream.CopyToAsync(destinationStream, cancellationToken);
        }
    }

    public static void ExtractEntries(
        ZipArchive archive,
        IReadOnlyList<ZipExtractionRequest> requests)
    {
        foreach (var request in requests)
        {
            var entry = archive.GetEntry(request.EntryName)
                        ?? throw new InvalidOperationException($"Zip entry '{request.EntryName}' is missing");
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)
                                      ?? throw new InvalidOperationException("Missing destination folder"));
            entry.ExtractToFile(request.DestinationPath, true);
        }
    }

    public static string? ReadJarMainClass(string jarPath)
    {
        using var archive = ZipFile.OpenRead(jarPath);
        var manifestEntry = archive.GetEntry("META-INF/MANIFEST.MF");
        if (manifestEntry is null)
        {
            return null;
        }

        using var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8, false, leaveOpen: false);
        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (line?.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase) == true)
            {
                return line["Main-Class:".Length..].Trim();
            }
        }

        return null;
    }
}
