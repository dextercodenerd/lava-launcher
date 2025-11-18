using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Http;

public class FileDownloader
{
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _concurrencySemaphore;
    private readonly ILogger? _logger;

    public FileDownloader(HttpClient httpClient,
        int maxConcurrentDownloads = 10,
        ILogger? logger = null)
    {
        _httpClient = httpClient;
        _concurrencySemaphore = new SemaphoreSlim(maxConcurrentDownloads, maxConcurrentDownloads);
        _logger = logger;
    }

    public async Task DownloadFileAsync(
        string url,
        string destinationPath,
        string? expectedHash = null,
        Action<double>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        await _concurrencySemaphore.WaitAsync(cancellationToken);
        try
        {
            await DownloadFileInternalAsync(url, destinationPath, expectedHash, progressCallback, cancellationToken);
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    public async Task<string> DownloadFileToFolderAsync(
        string url,
        string destinationDirectory,
        string? expectedHash = null,
        Action<double>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        var fileName = ExtractFileNameFromUrl(url);
        var destinationPath = Path.Combine(destinationDirectory, fileName);

        await DownloadFileAsync(url, destinationPath, expectedHash, progressCallback, cancellationToken);
        return destinationPath;
    }

    private async Task DownloadFileInternalAsync(
        string url,
        string destinationPath,
        string? expectedHash,
        Action<double>? progressCallback,
        CancellationToken cancellationToken)
    {
        _logger?.LogDebug("Downloading {Url} to {Destination}", url, destinationPath);

        if (File.Exists(destinationPath))
        {
            if (expectedHash is not null && await VerifyFileHashAsync(destinationPath, expectedHash))
            {
                _logger?.LogDebug("File already exists and is valid: {Destination}", destinationPath);
                progressCallback?.Invoke(1.0);
                return;
            }

            // Delete invalid/corupted files
            File.Delete(destinationPath);
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Download to a temporary file first
        var tempPath = destinationPath + ".tmp";
        try
        {
            using var response = await _httpClient.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            response.EnsureSuccessStatusCode();

            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalRead += bytesRead;

                if (totalBytes > 0)
                {
                    progressCallback?.Invoke((double)totalRead / totalBytes);
                }
            }

            if (totalBytes <= 0)
            {
                progressCallback?.Invoke(1.0);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to download {Url}", url);
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // ignored
            }

            throw;
        }

        // Verify integrity if hash is provided
        if (expectedHash is not null && !await VerifyFileHashAsync(tempPath, expectedHash))
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // ignored
            }

            throw new InvalidOperationException(
                $"Hash verification failed for {Path.GetFileName(destinationPath)}. Expected: {expectedHash}");
        }

        // Move the temporary file to the final location
        File.Move(tempPath, destinationPath, true);
        _logger?.LogInformation("Successfully downloaded {Destination}", destinationPath);
    }

    private static string ExtractFileNameFromUrl(string url)
    {
        // Handle URLs with query parameters and fragments
        var uri = new Uri(url);
        var fileName = Path.GetFileName(uri.LocalPath);

        if (string.IsNullOrEmpty(fileName))
        {
            // Fallback: use last segment of path or generate a name
            var segments = uri.Segments;
            fileName = segments.LastOrDefault(s => !string.IsNullOrEmpty(s.Trim('/'))) ?? "download";

            // Add extension based on content type or use generic name
            if (!Path.HasExtension(fileName))
            {
                fileName += ".bin";
            }
        }

        // Remove any invalid file name characters
        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var invalidChar in invalidChars)
        {
            fileName = fileName.Replace(invalidChar, '_');
        }

        return fileName;
    }

    private async Task<bool> VerifyFileHashAsync(string filePath, string expectedHash)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            await using var fileStream = File.OpenRead(filePath);

            byte[] hash;
            if (expectedHash.Length == 64) // SHA-256
            {
                hash = await SHA256.HashDataAsync(fileStream);
            }
            else if (expectedHash.Length == 40) // SHA-1
            {
                hash = await SHA1.HashDataAsync(fileStream);
            }
            else
            {
                throw new ArgumentException(
                    $"Unsupported hash length: {expectedHash.Length}. Expected 40 (SHA1) or 64 (SHA256) characters.");
            }

            var actualHash = Convert.ToHexString(hash).ToLowerInvariant();
            return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to verify hash for {FilePath}", filePath);
            return false;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _concurrencySemaphore.Dispose();
    }
}
