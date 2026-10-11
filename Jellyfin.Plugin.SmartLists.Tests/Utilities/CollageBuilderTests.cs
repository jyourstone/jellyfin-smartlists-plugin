using Jellyfin.Plugin.SmartLists.Utilities;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Jellyfin.Plugin.SmartLists.Tests.Utilities;

/// <summary>
/// Tests for <see cref="CollageBuilder"/>'s SkiaSharp pipeline: the EXIF orientation
/// transform, the scaled decode, the center-crop geometry, and end-to-end cover files on disk.
/// </summary>
public sealed class CollageBuilderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("smartlists-collage-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------

    private static SKColor ColorFor(char c) => c switch
    {
        'R' => SKColors.Red,
        'G' => SKColors.Lime,
        'B' => SKColors.Blue,
        'Y' => SKColors.Yellow,
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    private static SKBitmap Solid(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        return bitmap;
    }

    private string Write(string name, SKBitmap bitmap, SKEncodedImageFormat format, ushort exifOrientation = 1)
    {
        var path = Path.Combine(_dir, name);
        using var data = bitmap.Encode(format, 95);
        var bytes = exifOrientation == 1 ? data.ToArray() : WithExifOrientation(data.ToArray(), exifOrientation);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void AssertNear(SKColor expected, SKColor actual, int tolerance = 12)
    {
        Assert.True(
            Math.Abs(expected.Red - actual.Red) <= tolerance
            && Math.Abs(expected.Green - actual.Green) <= tolerance
            && Math.Abs(expected.Blue - actual.Blue) <= tolerance,
            $"Expected ~{expected}, got {actual}");
    }

    /// <summary>
    /// Inserts an APP1 Exif segment holding only an Orientation tag right after the JPEG SOI.
    /// </summary>
    private static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        byte[] app1 =
        [
            0xFF, 0xE1, 0x00, 0x22,                         // APP1, length 34
            0x45, 0x78, 0x69, 0x66, 0x00, 0x00,             // "Exif\0\0"
            0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, // big-endian TIFF header, IFD0 at 8
            0x00, 0x01,                                     // one entry
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, // Orientation, SHORT, count 1
            (byte)(orientation >> 8), (byte)orientation, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,                         // no next IFD
        ];
        return [.. jpeg[..2], .. app1, .. jpeg[2..]];
    }

    // ---------------------------------------------------------------------------------
    // Orientation
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A 3x2 stored bitmap with red/green/blue/yellow at its TL/TR/BL/BR corners must come
    /// out with the display corners (TL, TR, BL, BR) that EXIF orientation defines.
    /// </summary>
    [Theory]
    [InlineData(SKEncodedOrigin.TopLeft, "RGBY", 3, 2)]
    [InlineData(SKEncodedOrigin.TopRight, "GRYB", 3, 2)]
    [InlineData(SKEncodedOrigin.BottomRight, "YBGR", 3, 2)]
    [InlineData(SKEncodedOrigin.BottomLeft, "BYRG", 3, 2)]
    [InlineData(SKEncodedOrigin.LeftTop, "RBGY", 2, 3)]
    [InlineData(SKEncodedOrigin.RightTop, "BRYG", 2, 3)]
    [InlineData(SKEncodedOrigin.RightBottom, "YGBR", 2, 3)]
    [InlineData(SKEncodedOrigin.LeftBottom, "GYRB", 2, 3)]
    public void ApplyEncodedOrigin_MovesCornersToTheirDisplayPositions(SKEncodedOrigin origin, string corners, int width, int height)
    {
        using var stored = Solid(3, 2, SKColors.White);
        stored.SetPixel(0, 0, SKColors.Red);
        stored.SetPixel(2, 0, SKColors.Lime);
        stored.SetPixel(0, 1, SKColors.Blue);
        stored.SetPixel(2, 1, SKColors.Yellow);

        using var oriented = CollageBuilder.ApplyEncodedOrigin(stored, origin);

        Assert.Equal(width, oriented.Width);
        Assert.Equal(height, oriented.Height);
        Assert.Equal(ColorFor(corners[0]), oriented.GetPixel(0, 0));
        Assert.Equal(ColorFor(corners[1]), oriented.GetPixel(width - 1, 0));
        Assert.Equal(ColorFor(corners[2]), oriented.GetPixel(0, height - 1));
        Assert.Equal(ColorFor(corners[3]), oriented.GetPixel(width - 1, height - 1));
    }

    // ---------------------------------------------------------------------------------
    // Scaled decode
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The decode scales down as far as the codec allows while still covering the drawn size
    /// (in display orientation), and never below it.
    /// </summary>
    [Theory]
    [InlineData("jpg", 1600, 1200, 1, 0, 0, 1600, 1200)]       // native resolution: full decode
    [InlineData("jpg", 1600, 1200, 1, 100, 100, 200, 150)]     // JPEG 1/8 still covers 100x100
    [InlineData("jpg", 1000, 1000, 1, 300, 300, 375, 375)]     // 0.3 rounds to 2/8 (250 < 300): steps up to 3/8
    [InlineData("jpg", 1000, 1000, 1, 900, 900, 1000, 1000)]   // 7/8 (875) undershoots: full decode
    [InlineData("jpg", 1600, 1200, 6, 100, 200, 150, 200)]     // rotated: minimum mapped to stored axes
    [InlineData("webp", 1000, 1000, 1, 300, 300, 300, 300)]    // WebP scales freely
    [InlineData("png", 1600, 1200, 1, 100, 100, 1600, 1200)]   // PNG has no scaled decode
    public void LoadOrientedBitmap_ScalesDecodeToCoverTheDrawnSize(
        string extension, int storedWidth, int storedHeight, ushort orientation,
        int minWidth, int minHeight, int expectedWidth, int expectedHeight)
    {
        using var stored = Solid(storedWidth, storedHeight, SKColors.White);
        var format = extension switch
        {
            "jpg" => SKEncodedImageFormat.Jpeg,
            "webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png,
        };
        var path = Write("source." + extension, stored, format, orientation);

        using var bitmap = CollageBuilder.LoadOrientedBitmap(path, minWidth, minHeight);

        Assert.Equal(expectedWidth, bitmap.Width);
        Assert.Equal(expectedHeight, bitmap.Height);
    }

    // ---------------------------------------------------------------------------------
    // Crop geometry
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(400, 200, 100, 100, 100f, 0f, 200f, 200f)]       // wider: full height, centered
    [InlineData(200, 400, 100, 100, 0f, 100f, 200f, 200f)]       // taller: full width, centered
    [InlineData(300, 300, 100, 100, 0f, 0f, 300f, 300f)]         // same ratio: whole source
    [InlineData(1000, 500, 200, 300, 333.3333f, 0f, 333.3333f, 500f)] // 2:3 from 2:1, scaled: exact centered origin
    [InlineData(50, 40, 600, 600, 5f, 0f, 40f, 40f)]             // small source: upscaled crop
    [InlineData(401, 200, 100, 100, 100.5f, 0f, 200f, 200f)]     // scaled: origin not snapped
    [InlineData(401, 200, 200, 200, 100f, 0f, 200f, 200f)]       // unscaled 1:1 crop: origin snapped to whole pixels
    public void GetCenterCropSourceRect_CoversTargetWithCenteredCrop(
        int sourceWidth, int sourceHeight, int targetWidth, int targetHeight,
        float left, float top, float width, float height)
    {
        var rect = CollageBuilder.GetCenterCropSourceRect(sourceWidth, sourceHeight, targetWidth, targetHeight);

        Assert.Equal(left, rect.Left, 3);
        Assert.Equal(top, rect.Top, 3);
        Assert.Equal(width, rect.Width, 3);
        Assert.Equal(height, rect.Height, 3);
    }

    // ---------------------------------------------------------------------------------
    // End to end
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task TryCreateCroppedCoverAsync_WritesCroppedCover_AndRejectsUnsupportedFiles()
    {
        using var source = Solid(400, 200, SKColors.White);
        var sourcePath = Write("source.png", source, SKEncodedImageFormat.Png);

        // Native resolution: largest centered square, badged in place (dark badge center at 20)
        var nativePath = Path.Combine(_dir, "native.jpg");
        Assert.True(await CollageBuilder.TryCreateCroppedCoverAsync(sourcePath, nativePath, 1, 1, 0, applyBadge: true, NullLogger.Instance, CancellationToken.None));
        using (var native = SKBitmap.Decode(nativePath))
        {
            Assert.Equal(200, native.Width);
            Assert.Equal(200, native.Height);
            Assert.True(native.GetPixel(20, 20).Red < 100, "Badge not drawn");
            AssertNear(SKColors.White, native.GetPixel(100, 100));
        }

        // Fixed width: upscaled to 600x600 and badged (dark badge center at inset + size/2 = 60)
        var badgedPath = Path.Combine(_dir, "badged.jpg");
        Assert.True(await CollageBuilder.TryCreateCroppedCoverAsync(sourcePath, badgedPath, 1, 1, 600, applyBadge: true, NullLogger.Instance, CancellationToken.None));
        using (var badged = SKBitmap.Decode(badgedPath))
        {
            Assert.Equal(600, badged.Width);
            Assert.Equal(600, badged.Height);
            Assert.True(badged.GetPixel(60, 60).Red < 100, "Badge not drawn");
            AssertNear(SKColors.White, badged.GetPixel(300, 300));
        }

        // No Skia encoder for .gif: false, nothing written
        var gifPath = Path.Combine(_dir, "cover.gif");
        Assert.False(await CollageBuilder.TryCreateCroppedCoverAsync(sourcePath, gifPath, 1, 1, 0, applyBadge: false, NullLogger.Instance, CancellationToken.None));
        Assert.False(File.Exists(gifPath));

        // Undecodable source: false
        var bogusPath = Path.Combine(_dir, "bogus.png");
        File.WriteAllText(bogusPath, "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        Assert.False(await CollageBuilder.TryCreateCroppedCoverAsync(bogusPath, Path.Combine(_dir, "bogus-out.png"), 1, 1, 0, applyBadge: false, NullLogger.Instance, CancellationToken.None));
    }

    [Fact]
    public async Task SaveCoverAsync_InterruptedSave_KeepsExistingCover_AndRemovesTempFile()
    {
        var coverPath = Path.Combine(_dir, "cover.jpg");
        File.WriteAllBytes(coverPath, [1, 2, 3]);
        using var bitmap = Solid(10, 10, SKColors.White);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CollageBuilder.SaveCoverAsync(bitmap, coverPath, SKEncodedImageFormat.Jpeg, new CancellationToken(canceled: true)));

        Assert.Equal([1, 2, 3], File.ReadAllBytes(coverPath));
        Assert.Equal([coverPath], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task TryCreateCroppedCoverAsync_AppliesExifOrientationBeforeCropping()
    {
        // Stored 40x20 landscape tagged RightTop (6): displays as 20x40 portrait. A 1:2 crop
        // of the oriented image is the whole 20x40; of the unrotated one it would be 10x20.
        using var stored = Solid(40, 20, SKColors.White);
        var sourcePath = Write("rotated.jpg", stored, SKEncodedImageFormat.Jpeg, exifOrientation: 6);

        var outputPath = Path.Combine(_dir, "rotated-out.jpg");
        Assert.True(await CollageBuilder.TryCreateCroppedCoverAsync(sourcePath, outputPath, 1, 2, 0, applyBadge: false, NullLogger.Instance, CancellationToken.None));

        using var output = SKBitmap.Decode(outputPath);
        Assert.Equal(20, output.Width);
        Assert.Equal(40, output.Height);
    }

    [Fact]
    public async Task CreateGridCollageAsync_FillsQuadrants_AndLeavesUnreadableSourcesBlack()
    {
        using var red = Solid(100, 100, SKColors.Red);
        using var green = Solid(300, 100, SKColors.Lime);
        using var blue = Solid(50, 80, SKColors.Blue);
        var bogusPath = Path.Combine(_dir, "bogus.jpg");
        File.WriteAllText(bogusPath, "not an image");
        var sources = new[]
        {
            Write("red.png", red, SKEncodedImageFormat.Png),
            Write("green.jpg", green, SKEncodedImageFormat.Jpeg),
            bogusPath,
            Write("blue.webp", blue, SKEncodedImageFormat.Webp),
        };

        var outputPath = Path.Combine(_dir, "collage.jpg");
        await CollageBuilder.CreateGridCollageAsync(sources, outputPath, 200, 300, applyBadge: false, NullLogger.Instance, CancellationToken.None);

        using var collage = SKBitmap.Decode(outputPath);
        Assert.Equal(200, collage.Width);
        Assert.Equal(300, collage.Height);
        AssertNear(SKColors.Red, collage.GetPixel(50, 75));
        AssertNear(SKColors.Lime, collage.GetPixel(150, 75));
        AssertNear(SKColors.Black, collage.GetPixel(50, 225));
        AssertNear(SKColors.Blue, collage.GetPixel(150, 225));
    }
}
