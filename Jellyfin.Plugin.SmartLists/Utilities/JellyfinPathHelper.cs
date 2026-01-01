using System;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Utilities
{
    /// <summary>
    /// Helper utilities for working with Jellyfin paths.
    /// </summary>
    public static class JellyfinPathHelper
    {
        /// <summary>
        /// Gets the Jellyfin data path by navigating up from a collection or playlist path.
        /// Paths are typically: {DataPath}/data/collections/{name}/ or {DataPath}/data/playlists/{name}/
        /// Returns: {DataPath}/data/
        /// </summary>
        /// <param name="libraryManager">The library manager to query items</param>
        /// <param name="logger">Logger for debugging</param>
        /// <param name="itemKind">The item kind to query (BoxSet or Playlist)</param>
        /// <returns>The Jellyfin data path, or null if it cannot be determined</returns>
        public static string? GetJellyfinDataPath(ILibraryManager libraryManager, ILogger logger, BaseItemKind itemKind)
        {
            try
            {
                // Get any collection/playlist to determine the data path structure
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = [itemKind],
                    Limit = 1
                };
                
                var items = libraryManager.GetItemsResult(query).Items;
                var sampleItem = items.Count > 0 ? items[0] : null;
                if (sampleItem != null && !string.IsNullOrEmpty(sampleItem.Path))
                {
                    // Path format: /path/to/jellyfin/data/collections|playlists/ItemName/
                    // Go up 2 levels to get: /path/to/jellyfin/data/
                    var itemTypeDir = System.IO.Path.GetDirectoryName(sampleItem.Path);
                    if (!string.IsNullOrEmpty(itemTypeDir))
                    {
                        var dataDir = System.IO.Path.GetDirectoryName(itemTypeDir);
                        if (!string.IsNullOrEmpty(dataDir))
                        {
                            logger.LogDebug("Determined Jellyfin data path: {DataPath}", dataDir);
                            return dataDir;
                        }
                    }
                }
                
                logger.LogWarning("Could not determine Jellyfin data path from {ItemKind} paths", itemKind);
                return null;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error determining Jellyfin data path");
                return null;
            }
        }
    }
}
