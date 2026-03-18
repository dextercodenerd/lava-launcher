using System.Buffers.Binary;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: LavaLauncher.WindowsIconTool <output.ico> <input1.png> [<input2.png>...]");
    return 1;
}

var outputPath = Path.GetFullPath(args[0]);
var imagePaths = args.Skip(1)
    .Select(Path.GetFullPath)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();

if (imagePaths.Length == 0)
{
    Console.Error.WriteLine("At least one PNG input is required.");
    return 1;
}

var images = new List<IconImage>(imagePaths.Length);

foreach (var imagePath in imagePaths)
{
    if (!File.Exists(imagePath))
    {
        Console.Error.WriteLine($"PNG source not found: {imagePath}");
        return 1;
    }

    var bytes = await File.ReadAllBytesAsync(imagePath);
    if (!TryReadPngDimensions(bytes, out var width, out var height))
    {
        Console.Error.WriteLine($"Invalid PNG file: {imagePath}");
        return 1;
    }

    images.Add(new IconImage(width, height, bytes));
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

using var stream = File.Create(outputPath);
using var writer = new BinaryWriter(stream);

writer.Write((ushort)0);
writer.Write((ushort)1);
writer.Write((ushort)images.Count);

var offset = 6 + (16 * images.Count);
foreach (var image in images.OrderBy(i => i.Width).ThenBy(i => i.Height))
{
    writer.Write(ToIconSize(image.Width));
    writer.Write(ToIconSize(image.Height));
    writer.Write((byte)0);
    writer.Write((byte)0);
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write(image.Bytes.Length);
    writer.Write(offset);
    offset += image.Bytes.Length;
}

foreach (var image in images.OrderBy(i => i.Width).ThenBy(i => i.Height))
{
    writer.Write(image.Bytes);
}

return 0;

static bool TryReadPngDimensions(byte[] bytes, out int width, out int height)
{
    width = 0;
    height = 0;

    ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
    if (bytes.Length < 24 || !bytes.AsSpan(0, signature.Length).SequenceEqual(signature))
    {
        return false;
    }

    width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
    height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
    return width > 0 && height > 0;
}

static byte ToIconSize(int size) => size >= 256 ? (byte)0 : checked((byte)size);

sealed record IconImage(int Width, int Height, byte[] Bytes);
