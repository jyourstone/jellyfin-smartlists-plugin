using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.SmartLists.Utilities
{
    /// <summary>
    /// Stamps the smart list badge (embedded Resources/badge.png) onto cover images
    /// so smart playlists/collections are recognizable at a glance.
    /// </summary>
    public static class CoverBadgeHelper
    {
        /// <summary>
        /// Badge diameter relative to the image's shorter edge.
        /// </summary>
        private const float BadgeRelativeSize = 0.14f;

        /// <summary>
        /// Badge inset from the top-left corner relative to the image's shorter edge.
        /// </summary>
        private const float BadgeRelativeInset = 0.03f;

        /// <summary>
        /// Badge image, decoded once, or null when the embedded resource can't be loaded or
        /// decoded. Loading never throws out of the Lazy - a cached exception would otherwise
        /// disable all cover generation for the process lifetime. The image is immutable, so
        /// concurrent cover jobs can draw it.
        /// </summary>
        private static readonly Lazy<SKImage?> Badge = new(LoadBadge);

        /// <summary>
        /// Gets a value indicating whether the badge overlay is enabled in the plugin configuration.
        /// </summary>
        public static bool IsEnabled => Plugin.Instance?.Configuration?.ShowSmartListBadge ?? true;

        /// <summary>
        /// Draws the badge in the top-left corner of the image. Never throws: a badge
        /// failure degrades to an unbadged cover instead of aborting cover generation.
        /// </summary>
        /// <param name="image">The image to stamp. Mutated in place.</param>
        /// <param name="logger">Logger for badge failures.</param>
        public static void ApplyBadge(SKBitmap image, ILogger logger)
        {
            var badge = Badge.Value;
            if (badge == null)
            {
                logger.LogWarning("Smart list badge resource unavailable; cover saved without badge");
                return;
            }

            try
            {
                var shortEdge = Math.Min(image.Width, image.Height);
                var size = Math.Max(1, (int)(shortEdge * BadgeRelativeSize));
                var inset = (int)(shortEdge * BadgeRelativeInset);

                using var canvas = new SKCanvas(image);
                canvas.DrawImage(badge, SKRect.Create(inset, inset, size, size), CollageBuilder.Sampling);
            }
            catch (Exception ex)
            {
                // An unbadged cover beats no cover; the caller's pipeline continues.
                logger.LogWarning(ex, "Failed to stamp smart list badge; cover saved without badge");
            }
        }

        private static SKImage? LoadBadge()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("badge.png", StringComparison.OrdinalIgnoreCase));
                if (resourceName == null)
                {
                    return null;
                }

                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    return null;
                }

                using var bitmap = SKBitmap.Decode(stream);
                if (bitmap == null)
                {
                    return null;
                }

                // Immutable lets the image share the decoded pixels instead of copying them.
                bitmap.SetImmutable();
                return SKImage.FromBitmap(bitmap);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
