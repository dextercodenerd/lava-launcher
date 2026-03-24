using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using GenericLauncher.Http;
using Xunit;

namespace GenericLauncher.Tests.Http;

public class FileDownloaderTest
{
    [Fact]
    public async Task VerifyFileHashAsync_SupportsSha512()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "lavalancher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var filePath = Path.Combine(root, "mod.jar");
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(filePath, bytes, cancellationToken);

        var sha512 = Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();

        var isValid = await FileDownloader.VerifyFileHashAsync(filePath, sha512, cancellationToken);

        Assert.True(isValid);
    }
}
