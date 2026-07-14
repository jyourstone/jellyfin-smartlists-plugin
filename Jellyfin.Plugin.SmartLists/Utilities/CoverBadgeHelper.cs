using System;
using System.IO;
using System.Linq;
using System.Reflection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

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
        /// Badge PNG bytes, or null when the embedded resource can't be loaded. Loading
        /// never throws out of the Lazy - a cached exception would otherwise disable all
        /// cover generation for the process lifetime.
        /// </summary>
        private static readonly Lazy<byte[]?> BadgeBytes = new(LoadBadgeBytes);

        /// <summary>
        /// Gets a value indicating whether the badge overlay is enabled in the plugin configuration.
        /// </summary>
        public static bool IsEnabled => Plugin.Instance?.Configuration?.ShowSmartListBadge ?? true;

        /// <summary>
        /// Draws the badge in the top-left corner of the image. Never throws: a badge
        /// failure degrades to an unbadged cover instead of aborting cover generation.
        /// </summary>
        /// <param name="image">The image to stamp. Mutated in place.</param>
        public static void ApplyBadge(Image image)
        {
            var badgeBytes = BadgeBytes.Value;
            if (badgeBytes == null)
            {
                return;
            }

            try
            {
                var shortEdge = Math.Min(image.Width, image.Height);
                var size = Math.Max(1, (int)(shortEdge * BadgeRelativeSize));
                var inset = (int)(shortEdge * BadgeRelativeInset);

                using var badge = Image.Load<Rgba32>(badgeBytes);
                badge.Mutate(ctx => ctx.Resize(size, size));
                image.Mutate(ctx => ctx.DrawImage(badge, new Point(inset, inset), 1.0f));
            }
            catch (Exception)
            {
                // An unbadged cover beats no cover; the caller's pipeline continues.
            }
        }

        private static byte[]? LoadBadgeBytes()
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

                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
