using System.Linq;

namespace Jellyfin.Plugin.SmartLists.Core.Constants
{
    /// <summary>
    /// Represents a Live TV channel resolution with its display name and numeric value for comparisons.
    /// </summary>
    public record ChannelResolutionInfo(string Value, string DisplayName, int Height);

    /// <summary>
    /// Centralized channel resolution definitions for Live TV channels.
    /// These are text-based values from IPTV metadata, not actual video stream heights.
    /// </summary>
    public static class ChannelResolutionTypes
    {
        /// <summary>
        /// All available channel resolution options for the UI dropdown.
        /// Heights are assigned for comparison purposes (not actual video heights).
        /// </summary>
        public static readonly ChannelResolutionInfo[] AllResolutions =
        [
            new ChannelResolutionInfo("SD", "SD (Standard Definition)", 480),
            new ChannelResolutionInfo("SD (PAL)", "SD (PAL)", 576),
            new ChannelResolutionInfo("HD", "HD (High Definition)", 720),
            new ChannelResolutionInfo("Full HD", "Full HD (1080p)", 1080),
            new ChannelResolutionInfo("UHD", "UHD (4K)", 2160)
        ];

        /// <summary>
        /// Gets a channel resolution info by its value.
        /// </summary>
        /// <param name="value">The resolution value (e.g., "HD", "Full HD")</param>
        /// <returns>The resolution info or null if not found</returns>
        public static ChannelResolutionInfo? GetByValue(string value)
        {
            return AllResolutions.FirstOrDefault(r =>
                string.Equals(r.Value, value, System.StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Gets a channel resolution info by its height.
        /// </summary>
        /// <param name="height">The resolution height value</param>
        /// <returns>The resolution info or null if not found</returns>
        public static ChannelResolutionInfo? GetByHeight(int height)
        {
            return AllResolutions.FirstOrDefault(r => r.Height == height);
        }

        /// <summary>
        /// Gets all resolution values for API responses.
        /// </summary>
        /// <returns>Array of resolution values</returns>
        public static string[] GetAllValues()
        {
            return [.. AllResolutions.Select(r => r.Value)];
        }

        /// <summary>
        /// Gets all resolution display names for UI dropdowns.
        /// </summary>
        /// <returns>Array of resolution display names</returns>
        public static string[] GetAllDisplayNames()
        {
            return [.. AllResolutions.Select(r => r.DisplayName)];
        }

        /// <summary>
        /// Gets the numeric height value for a channel resolution string.
        /// This enables numeric comparisons (greater than, less than) for channel resolution.
        /// </summary>
        /// <param name="resolutionValue">The resolution value (e.g., "HD", "Full HD")</param>
        /// <returns>The height value for comparison, or -1 if not found or empty</returns>
        public static int GetHeightForResolution(string resolutionValue)
        {
            if (string.IsNullOrEmpty(resolutionValue))
                return -1;

            var resolution = GetByValue(resolutionValue);
            return resolution?.Height ?? -1;
        }
    }
}
