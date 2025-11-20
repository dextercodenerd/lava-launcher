using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GenericLauncher.Http;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Java;

public sealed class JavaVersionManager : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly FileDownloader _downloader;
    private readonly string _javaInstallationsDirectory;
    private readonly ILogger? _logger;

    public JavaVersionManager(
        string javaInstallationsDirectory,
        HttpClient httpClient,
        FileDownloader fileDownloader,
        ILogger? logger = null)
    {
        _javaInstallationsDirectory = javaInstallationsDirectory;
        _downloader = fileDownloader;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task InstallJavaAsync(int javaVersion,
        IProgress<double>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        const int minJavaVersion = 8;
        if (javaVersion < minJavaVersion)
        {
            throw new ArgumentException(
                $"only Java {minJavaVersion} and above are supported, wanted {javaVersion}");
        }

        _logger?.LogInformation("Installing Java {Version} (Eclipse Temurin)", javaVersion);

        // Check if already installed
        var installationPath = GetJavaInstallationPath(javaVersion);
        if (Directory.Exists(installationPath) && IsJavaInstallationValid(installationPath))
        {
            _logger?.LogInformation("Java {Version} already installed at {Path}", javaVersion, installationPath);
            progressCallback?.Report(1.0);
            return;
        }

        // Get platform-specific download URL
        var (downloadUrl, expectedHash) = await GetTemurinDownloadInfoAsync(javaVersion, cancellationToken);

        // Download JDK
        var tempDir = Path.Combine(Path.GetTempPath(), $"java-{javaVersion}-temurin-{Guid.NewGuid()}");
        var downloadPath = await _downloader.DownloadFileToFolderAsync(
            downloadUrl,
            tempDir,
            expectedHash,
            new Progress<double>(p =>
            {
                // Report just 95% here and report 100% after extracting it
                progressCallback?.Report(p * 0.95);
            }),
            cancellationToken);

        // extract and install
        await ExtractAndInstallJavaAsync(downloadPath, installationPath, cancellationToken);
        progressCallback?.Report(0.98);

        // cleanup
        try
        {
            Directory.Delete(tempDir, true);
        }
        catch (Exception ex)
        {
            // TODO: In the future, when we will check integrity of everything on app start, delete
            //  also the leftover temp folder of Java installation.
            _logger?.LogWarning(ex, "Problem deleting temporary Java folder '{Folder}'", tempDir);
        }

        _logger?.LogInformation("Successfully installed Java {Version} to {Path}", javaVersion, installationPath);
        progressCallback?.Report(1.0);
    }

    private async Task<(string downloadUrl, string expectedHash)> GetTemurinDownloadInfoAsync(int javaVersion,
        CancellationToken cancellationToken)
    {
        var (os, arch, extension) = GetPlatformDetails();

        var apiUrl = $"https://api.adoptium.net/v3/assets/latest/{javaVersion}/hotspot?" +
                     $"architecture={arch}&image_type=jdk&os={os}&vendor=eclipse";

        _logger?.LogDebug("Fetching Temurin download info from: {Url}", apiUrl);

        try
        {
            var response = await _httpClient.GetStringAsync(apiUrl, cancellationToken);

            using var doc = JsonDocument.Parse(response);
            var release = doc.RootElement.EnumerateArray().First();
            var binary = release.GetProperty("binary");

            if (binary.GetProperty("image_type").GetString() != "jdk")
            {
                throw new InvalidOperationException(
                    $"Expected JDK binary but found different image type: {binary.GetProperty("image_type").GetString()}");
            }

            var package = binary.GetProperty("package");
            var downloadUrl = package.GetProperty("link").GetString()
                              ?? throw new InvalidOperationException("No download link found for Eclipse Temurin");

            // Get the SHA256 hash from the package info
            var expectedHash = package.GetProperty("checksum").GetString()
                               ?? throw new InvalidOperationException("No checksum found for Eclipse Temurin package");

            _logger?.LogDebug("Resolved download URL: {Url}", downloadUrl);
            _logger?.LogDebug("Expected SHA256: {Hash}", expectedHash);

            return (downloadUrl, expectedHash);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404"))
        {
            throw new PlatformNotSupportedException(
                $"Java {javaVersion} for {os}-{arch} is not available in Eclipse Temurin. " +
                "This platform/architecture combination may not be supported.");
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidOperationException("Failed to parse Temurin API response", ex);
        }
    }

    private static (string os, string arch, string extension) GetPlatformDetails()
    {
        if (OperatingSystem.IsWindows())
        {
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "aarch64",
                Architecture.Arm => "arm",
                _ => "x64",
            };
            return ("windows", arch, "zip");
        }
        else if (OperatingSystem.IsLinux())
        {
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "aarch64",
                Architecture.Arm => "arm",
                _ => "x64",
            };
            return ("linux", arch, "tar.gz");
        }
        else if (OperatingSystem.IsMacOS())
        {
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "aarch64",
                _ => "x64",
            };
            return ("mac", arch, "tar.gz");
        }
        else
        {
            throw new PlatformNotSupportedException($"Unsupported platform: {RuntimeInformation.OSDescription}");
        }
    }

    private static async Task ExtractAndInstallJavaAsync(string archivePath,
        string installationPath,
        CancellationToken cancellationToken)
    {
        // Clean existing installation
        if (Directory.Exists(installationPath))
        {
            Directory.Delete(installationPath, true);
        }

        Directory.CreateDirectory(installationPath);

        // Extract based on file extension
        var extension = Path.GetExtension(archivePath).ToLowerInvariant();

        if (extension == ".zip")
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, installationPath, true);
        }
        else if (extension == ".gz" || extension == ".tgz")
        {
            await ExtractTarGzAsync(archivePath, installationPath, cancellationToken);
        }
        else
        {
            throw new NotSupportedException($"Unsupported archive format: {extension}");
        }

        // For Temurin, the JDK is typically in a subdirectory
        var subDirs = Directory.GetDirectories(installationPath);
        if (subDirs.Length == 1)
        {
            var subDir = subDirs[0];
            var tempMoveDir = installationPath + "_move";

            // Move the entire subdirectory contents to root
            Directory.Move(subDir, tempMoveDir);

            // Move everything back to installation path
            foreach (var entry in Directory.GetFileSystemEntries(tempMoveDir))
            {
                var dest = Path.Combine(installationPath, Path.GetFileName(entry));
                if (File.Exists(entry))
                {
                    File.Move(entry, dest, true);
                }
                else if (Directory.Exists(entry))
                {
                    if (Directory.Exists(dest))
                    {
                        Directory.Delete(dest, true);
                    }

                    Directory.Move(entry, dest);
                }
            }

            Directory.Delete(tempMoveDir);
        }
    }

    private static async Task ExtractTarGzAsync(
        string archivePath,
        string extractionPath,
        CancellationToken cancellationToken)
    {
        // For .NET 9, we can use the new TarFile class
        if (OperatingSystem.IsWindows())
        {
            // On Windows, we might need to handle .tar.gz differently
            // For now, use Process to call tar if available, or throw
            throw new NotSupportedException(
                "tar.gz extraction requires external tools on Windows. Use .NET 7+ or install tar.");
        }

        // On Linux/macOS, use Process to call tar
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-xzf \"{archivePath}\" -C \"{extractionPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException(
                $"tar extraction failed with exit code {process.ExitCode}: {error}");
        }
    }

    private string GetJavaInstallationPath(int javaVersion)
    {
        var (os, arch, _) = GetPlatformDetails();
        return Path.Combine(_javaInstallationsDirectory, $"{javaVersion}-{os}-{arch}");
    }

    private static bool IsJavaInstallationValid(string installationPath)
    {
        var javaExe = GetJavaExecutablePath(installationPath);
        return File.Exists(javaExe);
    }

    private static string GetJavaExecutablePath(string installationPath)
    {
        var executableName = OperatingSystem.IsWindows() ? "java.exe" : "java";
        return Path.Combine(installationPath, "bin", executableName);
    }

    public string? GetJavaExecutablePath(int javaVersion)
    {
        var installationPath = GetJavaInstallationPath(javaVersion);
        var javaExe = GetJavaExecutablePath(installationPath);
        return File.Exists(javaExe) ? javaExe : null;
    }

    public void Dispose()
    {
        _downloader.Dispose();
        _httpClient.Dispose();
    }
}
