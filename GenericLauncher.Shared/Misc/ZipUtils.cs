using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
}
