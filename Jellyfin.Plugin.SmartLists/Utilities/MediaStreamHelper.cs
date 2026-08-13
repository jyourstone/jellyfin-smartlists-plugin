using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Utilities
{
    /// <summary>
    /// Shared media-stream derivations used by both rule evaluation
    /// (<c>OperandFactory.ExtractResolution</c>) and sorting (<c>ResolutionOrder</c>).
    ///
    /// Both need the same thing - the maximum video stream height for an item, read through
    /// reflection so it stays ABI-portable across Jellyfin versions, and served from
    /// <c>RefreshCache.MediaStreamsCache</c> so a refresh reads each item's streams once.
    /// This is the single implementation of that; neither caller re-derives it.
    /// </summary>
    internal static class MediaStreamHelper
    {
        /// <summary>
        /// The height reported for an item with no readable video stream (audio, books, or an
        /// item whose streams cannot be reflected). Deliberately 0 rather than -1 or int.MaxValue:
        /// it matches the zero-ish sentinel every other scalar order in Core/Orders uses, and it is
        /// the same value <c>ExtractResolution</c> already treats as "no resolution".
        /// </summary>
        public const int UnknownVideoHeight = 0;

        /// <summary>
        /// Gets an item's media streams, served from the refresh cache when one is supplied and
        /// stored back into it on a miss. With no cache the streams are read fresh every call.
        /// </summary>
        /// <param name="item">The item to read streams from</param>
        /// <param name="cache">Refresh cache to read/populate, or null when none is available</param>
        /// <param name="logger">Logger for diagnostics</param>
        /// <returns>The item's media streams; empty when none could be read</returns>
        public static IEnumerable<object> GetMediaStreams(BaseItem item, RefreshQueueService.RefreshCache? cache, ILogger? logger)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (cache == null)
            {
                return OperandFactory.TryGetAllMediaStreams(item, logger);
            }

            if (cache.MediaStreamsCache.TryGetValue(item.Id, out var cachedStreams))
            {
                return cachedStreams;
            }

            var mediaStreams = OperandFactory.TryGetAllMediaStreams(item, logger);
            cache.MediaStreamsCache[item.Id] = mediaStreams;
            return mediaStreams;
        }

        /// <summary>
        /// Gets the highest video stream height in pixels for an item.
        /// </summary>
        /// <param name="item">The item to inspect</param>
        /// <param name="cache">Refresh cache to read/populate, or null when none is available</param>
        /// <param name="logger">Logger for diagnostics</param>
        /// <returns>The maximum video height, or <see cref="UnknownVideoHeight"/> when there is none</returns>
        public static int GetMaxVideoHeight(BaseItem item, RefreshQueueService.RefreshCache? cache, ILogger? logger)
        {
            ArgumentNullException.ThrowIfNull(item);

            var mediaStreams = GetMediaStreams(item, cache, logger);

            int maxHeight = UnknownVideoHeight;
            foreach (var stream in mediaStreams)
            {
                try
                {
                    var typeProperty = stream.GetType().GetProperty("Type");
                    var heightProperty = stream.GetType().GetProperty("Height");

                    if (typeProperty != null && heightProperty != null)
                    {
                        var streamType = typeProperty.GetValue(stream);
                        var height = heightProperty.GetValue(stream);

                        // Check if it's a video stream
                        if (streamType != null && streamType.ToString() == "Video" && height != null)
                        {
                            if (int.TryParse(height.ToString(), out int heightValue) && heightValue > maxHeight)
                            {
                                maxHeight = heightValue;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Failed to process individual stream for item {Name}", item.Name);
                }
            }

            return maxHeight;
        }
    }
}
