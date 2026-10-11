using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.SmartLists.Utilities
{
    /// <summary>
    /// Builds the 2x2 grid collage JPEGs used as auto-generated covers for smart
    /// playlists and collections. Shared by PlaylistService and CollectionService.
    /// </summary>
    public static class CollageBuilder
    {
        /// <summary>
        /// Filename for the plugin's auto-generated Primary cover (collage or cropped copy).
        /// CollectionService's manual-image classification whitelists this exact name.
        /// </summary>
        public const string CollageFileName = "smartlist-collage.jpg";

        /// <summary>
        /// Filename for the plugin's auto-generated Thumb cover.
        /// </summary>
        public const string ThumbCollageFileName = "smartlist-thumb-collage.jpg";

        /// <summary>
        /// Encode quality for all generated covers (JPEG and WebP; PNG ignores it).
        /// </summary>
        private const int Quality = 90;

        /// <summary>
        /// Gets the sampling used for every scaled draw: bilinear across mipmap levels, so large
        /// downscales average source pixels instead of aliasing, and a 1:1 draw on whole pixels
        /// is an exact copy.
        /// </summary>
        internal static SKSamplingOptions Sampling { get; } = new(SKFilterMode.Linear, SKMipmapMode.Linear);

        /// <summary>
        /// Gets the Jellyfin tile aspect ratio covers are cropped to for an image type:
        /// playlists use square Primary tiles, collections 2:3 poster tiles; Thumb tiles
        /// are 16:9 for both. Single policy point - the upload paths and the generated-cover
        /// paths must stay in lockstep.
        /// </summary>
        private static (int Width, int Height) GetTileAspect(MediaBrowser.Model.Entities.ImageType imageType, bool forPlaylist)
            => imageType == MediaBrowser.Model.Entities.ImageType.Thumb ? (16, 9) : forPlaylist ? (1, 1) : (2, 3);

        /// <summary>
        /// Creates a badged, center-cropped copy of a cover at the Jellyfin tile aspect ratio
        /// for the given image type. Shared by the upload and generated-cover paths of both
        /// playlists and collections so the crop policy stays in one place.
        /// </summary>
        /// <param name="sourcePath">The source image path.</param>
        /// <param name="outputPath">The destination path.</param>
        /// <param name="imageType">The Jellyfin image type (drives the tile aspect ratio).</param>
        /// <param name="forPlaylist">True for playlist covers (square Primary tiles), false for collections (2:3).</param>
        /// <param name="targetWidth">Output width in pixels, or 0 to crop at native resolution.</param>
        /// <param name="logger">Logger for failures.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>False when the source cannot be decoded or the copy cannot be saved -
        /// the caller should fall back to the unmodified source.</returns>
        public static Task<bool> TryCreateBadgedTileCoverAsync(
            string sourcePath,
            string outputPath,
            MediaBrowser.Model.Entities.ImageType imageType,
            bool forPlaylist,
            int targetWidth,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var (aspectWidth, aspectHeight) = GetTileAspect(imageType, forPlaylist);
            return TryCreateCroppedCoverAsync(sourcePath, outputPath, aspectWidth, aspectHeight, targetWidth, applyBadge: true, logger, cancellationToken);
        }

        /// <summary>
        /// Creates a 2x2 collage from up to four source images and saves it as a JPEG.
        /// Images are center-cropped to fill their quadrant. Unreadable sources are
        /// skipped (their quadrant stays black), matching previous behavior.
        /// </summary>
        /// <param name="imagePaths">Source image paths (first four are used).</param>
        /// <param name="outputPath">Destination .jpg path.</param>
        /// <param name="width">Collage width in pixels (quadrants are width/2).</param>
        /// <param name="height">Collage height in pixels (quadrants are height/2).</param>
        /// <param name="applyBadge">Whether to stamp the smart list badge on the result.</param>
        /// <param name="logger">Logger for per-image load failures.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public static async Task CreateGridCollageAsync(
            IReadOnlyList<string> imagePaths,
            string outputPath,
            int width,
            int height,
            bool applyBadge,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var quadrantWidth = width / 2;
            var quadrantHeight = height / 2;

            using var collage = new SKBitmap(width, height);
            using (var canvas = new SKCanvas(collage))
            {
                canvas.Clear(SKColors.Black);

                for (int i = 0; i < 4 && i < imagePaths.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var sourceImage = LoadOrientedBitmap(imagePaths[i], quadrantWidth, quadrantHeight);

                        // Quadrants fill left-to-right, top-to-bottom. Scale to cover each while
                        // maintaining aspect ratio, cropping the overflow.
                        DrawCenterCropped(canvas, sourceImage, SKRectI.Create(i % 2 * quadrantWidth, i / 2 * quadrantHeight, quadrantWidth, quadrantHeight));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Failed to load image {ImagePath} for collage, skipping", imagePaths[i]);
                    }
                }
            }

            if (applyBadge)
            {
                CoverBadgeHelper.ApplyBadge(collage, logger);
            }

            await SaveCoverAsync(collage, outputPath, SKEncodedImageFormat.Jpeg, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Creates a center-cropped copy of a single source image at the given aspect ratio,
        /// optionally stamping the smart list badge. Used for covers when the badge is enabled:
        /// Jellyfin tiles crop covers (square for playlists, poster/thumb ratios for
        /// collections), so a badge in the corner of an off-ratio cover would fall outside the
        /// visible area — and off-ratio covers also distort overview grids. The encoder is
        /// inferred from the output extension; one Skia cannot encode returns false without
        /// touching the source, so the caller's raw-copy fallback uses the upload as-is.
        /// </summary>
        /// <param name="sourcePath">The source image path.</param>
        /// <param name="outputPath">The destination path.</param>
        /// <param name="aspectWidth">Width component of the target aspect ratio (e.g. 1, 2, 16).</param>
        /// <param name="aspectHeight">Height component of the target aspect ratio (e.g. 1, 3, 9).</param>
        /// <param name="targetWidth">Output width in pixels, or 0 to crop at the source's native
        /// resolution (largest crop of the target ratio that fits, no scaling).</param>
        /// <param name="applyBadge">Whether to stamp the smart list badge on the result.</param>
        /// <param name="logger">Logger for failures.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>False when the source cannot be decoded or the copy cannot be saved.</returns>
        public static async Task<bool> TryCreateCroppedCoverAsync(
            string sourcePath,
            string outputPath,
            int aspectWidth,
            int aspectHeight,
            int targetWidth,
            bool applyBadge,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var format = GetEncoderFormat(outputPath);
            if (format == null)
            {
                logger.LogDebug("Cannot save a cropped cover as {OutputPath}: no encoder for that extension", outputPath);
                return false;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A fixed-width cover's size is known before decoding, so the decode can scale down to it.
                var fixedHeight = targetWidth > 0 ? Math.Max(1, targetWidth * aspectHeight / aspectWidth) : 0;

                // Decoding applies the EXIF orientation of JPEG and WebP sources (Skia ignores a PNG
                // eXIf chunk) BEFORE the crop geometry below reads Width/Height, and the saved copy
                // carries no orientation tag for clients to re-apply.
                using var image = LoadOrientedBitmap(sourcePath, targetWidth, fixedHeight);
                using var cover = targetWidth > 0
                    ? DrawScaledCover(image, targetWidth, fixedHeight)
                    : ExtractNativeCrop(image, aspectWidth, aspectHeight);

                if (applyBadge)
                {
                    CoverBadgeHelper.ApplyBadge(cover, logger);
                }

                await SaveCoverAsync(cover, outputPath, format.Value, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Could not create cropped cover from {SourcePath}", sourcePath);
                return false;
            }
        }

        /// <summary>
        /// Computes the region of a source image that, scaled uniformly, exactly covers a target
        /// of the given size: the largest centered rectangle with the target's aspect ratio
        /// (a center-anchored crop-to-fill). An unscaled (1:1) crop's origin is floored to whole
        /// pixels so it copies pixels exactly; a scaled crop keeps the exact centered origin.
        /// </summary>
        /// <param name="sourceWidth">Source width in pixels.</param>
        /// <param name="sourceHeight">Source height in pixels.</param>
        /// <param name="targetWidth">Target width in pixels.</param>
        /// <param name="targetHeight">Target height in pixels.</param>
        /// <returns>The source rectangle to draw into the full target.</returns>
        internal static SKRect GetCenterCropSourceRect(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
        {
            double cropWidth;
            double cropHeight;
            if ((long)sourceWidth * targetHeight >= (long)sourceHeight * targetWidth)
            {
                // Source is wider than the target: full height, centered horizontal crop
                cropHeight = sourceHeight;
                cropWidth = (double)sourceHeight * targetWidth / targetHeight;
            }
            else
            {
                // Source is taller than the target: full width, centered vertical crop
                cropWidth = sourceWidth;
                cropHeight = (double)sourceWidth * targetHeight / targetWidth;
            }

            var left = (sourceWidth - cropWidth) / 2;
            var top = (sourceHeight - cropHeight) / 2;
            if (cropWidth == targetWidth)
            {
                left = Math.Floor(left);
                top = Math.Floor(top);
            }

            return SKRect.Create((float)left, (float)top, (float)cropWidth, (float)cropHeight);
        }

        /// <summary>
        /// Draws the centered crop of a source scaled to fill a new bitmap of the given size.
        /// </summary>
        private static SKBitmap DrawScaledCover(SKBitmap source, int width, int height)
        {
            var cover = new SKBitmap(width, height);
            using var canvas = new SKCanvas(cover);
            canvas.Clear(SKColors.Transparent);
            DrawCenterCropped(canvas, source, SKRectI.Create(0, 0, width, height));
            return cover;
        }

        /// <summary>
        /// Takes the largest centered crop of the aspect ratio at native resolution. The result
        /// shares the source's pixels instead of copying them, so the source must outlive it.
        /// </summary>
        private static SKBitmap ExtractNativeCrop(SKBitmap source, int aspectWidth, int aspectHeight)
        {
            var crop = GetCenterCropSourceRect(source.Width, source.Height, aspectWidth, aspectHeight);

            // Degenerate sources (1px edges) can truncate a dimension to 0, which would
            // produce an empty bitmap that cannot be encoded.
            var width = Math.Max(1, (int)crop.Width);
            var height = Math.Max(1, (int)crop.Height);

            var subset = new SKBitmap();
            if (!source.ExtractSubset(subset, SKRectI.Create((source.Width - width) / 2, (source.Height - height) / 2, width, height)))
            {
                subset.Dispose();
                throw new InvalidOperationException("Could not extract the cover crop");
            }

            return subset;
        }

        /// <summary>
        /// Returns a copy of a decoded bitmap rotated/mirrored from its stored orientation into
        /// its display orientation (EXIF orientation). Origins LeftTop through LeftBottom swap
        /// width and height. The matrices are Skia's SkEncodedOriginToMatrix.
        /// </summary>
        /// <param name="source">The bitmap as stored in the file.</param>
        /// <param name="origin">The encoded origin reported by the codec.</param>
        /// <returns>A new bitmap the caller owns; the source is left untouched.</returns>
        internal static SKBitmap ApplyEncodedOrigin(SKBitmap source, SKEncodedOrigin origin)
        {
            float w = source.Width;
            float h = source.Height;
            var matrix = origin switch
            {
                SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
                SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
                SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
                SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
                SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
                _ => SKMatrix.Identity,
            };

            var info = SwapsAxes(origin) ? source.Info.WithSize(source.Height, source.Width) : source.Info;

            var oriented = new SKBitmap(info);
            try
            {
                using var canvas = new SKCanvas(oriented);
                canvas.Clear(SKColors.Transparent);
                canvas.SetMatrix(matrix);

                // Whole-pixel 90-degree/mirror transforms map pixel centers onto pixel
                // centers, so the default nearest sampling copies pixels losslessly.
                canvas.DrawBitmap(source, 0, 0);
                return oriented;
            }
            catch
            {
                oriented.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Whether an encoded origin is a 90-degree rotation, which swaps width and height.
        /// </summary>
        private static bool SwapsAxes(SKEncodedOrigin origin)
            => origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        /// <summary>
        /// Decodes an image file into an N32 premultiplied sRGB bitmap in its display
        /// orientation. When the caller will draw it at a known size, the decode is scaled down
        /// as far as the codec allows while still covering that size, so a large source never
        /// holds its full-resolution pixels.
        /// </summary>
        /// <param name="path">The image path.</param>
        /// <param name="minWidth">Display width the crop is drawn at, or 0 for native resolution.</param>
        /// <param name="minHeight">Display height the crop is drawn at, or 0 for native resolution.</param>
        /// <returns>The bitmap; the caller owns it.</returns>
        /// <exception cref="InvalidDataException">The file is missing, corrupt, or in a format
        /// Skia has no decoder for (e.g. SVG, TIFF, AVIF).</exception>
        internal static SKBitmap LoadOrientedBitmap(string path, int minWidth, int minHeight)
        {
            using var codec = SKCodec.Create(path, out var result)
                ?? throw new InvalidDataException("Cannot decode " + path + ": " + result);

            var origin = codec.EncodedOrigin;
            var size = SwapsAxes(origin)
                ? GetDecodeSize(codec, minHeight, minWidth)
                : GetDecodeSize(codec, minWidth, minHeight);

            // Decode into sRGB, converting from any embedded ICC profile: saved covers carry no profile.
            var info = new SKImageInfo(size.Width, size.Height, SKImageInfo.PlatformColorType, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
            var decoded = SKBitmap.Decode(codec, info)
                ?? throw new InvalidDataException("Cannot decode " + path);
            if (origin == SKEncodedOrigin.TopLeft)
            {
                return decoded;
            }

            using (decoded)
            {
                // Immutable lets the orientation draw share the pixels instead of taking a
                // hidden full-size copy of them.
                decoded.SetImmutable();
                return ApplyEncodedOrigin(decoded, origin);
            }
        }

        /// <summary>
        /// Picks the decode size: the smallest the codec can scale to that still covers
        /// minWidth x minHeight, so the crop drawn from it is never upscaled. JPEG scales in
        /// eighths, WebP freely; other formats decode at full size.
        /// </summary>
        /// <param name="codec">The codec.</param>
        /// <param name="minWidth">Minimum width in stored orientation, or 0 for full size.</param>
        /// <param name="minHeight">Minimum height in stored orientation, or 0 for full size.</param>
        private static SKSizeI GetDecodeSize(SKCodec codec, int minWidth, int minHeight)
        {
            var full = codec.Info.Size;
            if (minWidth <= 0 || minHeight <= 0)
            {
                return full;
            }

            var scale = Math.Max((float)minWidth / full.Width, (float)minHeight / full.Height);
            if (scale >= 1)
            {
                return full;
            }

            // JPEG rounds to the nearest eighth: step up one when that undershoots.
            var scaled = codec.GetScaledDimensions(scale);
            if (scaled.Width < minWidth || scaled.Height < minHeight)
            {
                scaled = codec.GetScaledDimensions(Math.Min(1f, scale + 0.125f));
            }

            return scaled.Width >= minWidth && scaled.Height >= minHeight ? scaled : full;
        }

        /// <summary>
        /// Draws the centered crop of a source bitmap scaled to fill the destination rectangle exactly.
        /// </summary>
        private static void DrawCenterCropped(SKCanvas canvas, SKBitmap source, SKRectI destination)
        {
            var sourceRect = GetCenterCropSourceRect(source.Width, source.Height, destination.Width, destination.Height);

            // Immutable lets the image share the bitmap's pixels instead of copying them.
            source.SetImmutable();
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, sourceRect, destination, Sampling);
        }

        /// <summary>
        /// Maps an output extension to its Skia encoder. Skia encodes only JPEG, PNG and WebP.
        /// </summary>
        private static SKEncodedImageFormat? GetEncoderFormat(string outputPath)
            => Path.GetExtension(outputPath).ToUpperInvariant() switch
            {
                ".JPG" or ".JPEG" => SKEncodedImageFormat.Jpeg,
                ".PNG" => SKEncodedImageFormat.Png,
                ".WEBP" => SKEncodedImageFormat.Webp,
                _ => null,
            };

        /// <summary>
        /// Encodes a cover and writes it to disk via a temp file, so a failed encode or an
        /// interrupted write leaves any existing cover intact. The temp name is unique per save,
        /// so concurrent saves to the same cover never touch each other's file.
        /// </summary>
        internal static async Task SaveCoverAsync(SKBitmap bitmap, string outputPath, SKEncodedImageFormat format, CancellationToken cancellationToken)
        {
            using var data = bitmap.Encode(format, Quality)
                ?? throw new InvalidOperationException("Skia failed to encode the cover as " + format);
            using var encoded = data.AsStream();
            var tempPath = outputPath + "." + Path.GetRandomFileName() + ".tmp";
            try
            {
                var file = File.Create(tempPath);
                await using (file.ConfigureAwait(false))
                {
                    await encoded.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }

                File.Move(tempPath, outputPath, overwrite: true);
            }
            finally
            {
                File.Delete(tempPath);
            }
        }
    }
}
