using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace GenericLauncher.Misc;

public static class FileJsonExtensions
{
    extension(File)
    {
        public static async Task<TValue?> DeserializeJsonAsync<TValue>(
            string path,
            JsonTypeInfo<TValue> jsonTypeInfo,
            CancellationToken cancellationToken = default)
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(stream, jsonTypeInfo, cancellationToken);
        }
    }
}
