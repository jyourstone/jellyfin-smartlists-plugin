using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Utilities
{
    /// <summary>
    /// Utility class for common ILibraryManager operations using reflection.
    /// </summary>
    public static class LibraryManagerHelper
    {
        /// <summary>
        /// Triggers a library scan using reflection to call QueueLibraryScan if available.
        /// </summary>
        /// <param name="libraryManager">The library manager instance</param>
        /// <param name="logger">Optional logger for diagnostics</param>
        /// <returns>True if the scan was successfully queued, false otherwise</returns>
        public static bool QueueLibraryScan(ILibraryManager libraryManager, ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(libraryManager);
            
            try
            {
                logger?.LogDebug("Triggering library scan");
                var queueScanMethod = libraryManager.GetType().GetMethod("QueueLibraryScan");
                if (queueScanMethod != null)
                {
                    queueScanMethod.Invoke(libraryManager, null);
                    logger?.LogDebug("Queued library scan");
                    return true;
                }
                else
                {
                    logger?.LogWarning("QueueLibraryScan method not found on ILibraryManager");
                    return false;
                }
            }
            catch (TargetInvocationException ex)
            {
                // Unwrap TargetInvocationException to get the actual inner exception
                logger?.LogWarning(ex.InnerException ?? ex, "Failed to trigger library scan");
                return false;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to trigger library scan");
                return false;
            }
        }
        /// <summary>
        /// Gets TopParentIds for actual user libraries by resolving VirtualFolder physical paths
        /// to their folder BaseItem IDs. Excludes internal folders like live TV recordings.
        /// </summary>
        /// <param name="libraryManager">The library manager instance</param>
        /// <returns>Array of Guid IDs for library top parent folders</returns>
        public static Guid[] GetLibraryTopParentIds(ILibraryManager libraryManager)
        {
            var ids = new List<Guid>();
            foreach (var vf in libraryManager.GetVirtualFolders())
            {
                if (vf.Locations == null)
                {
                    continue;
                }

                foreach (var location in vf.Locations)
                {
                    if (string.IsNullOrEmpty(location))
                    {
                        continue;
                    }

                    // Skip live TV recording locations
                    if (location.Contains("/livetv/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var folder = libraryManager.FindByPath(location, true);
                    if (folder != null)
                    {
                        ids.Add(folder.Id);
                    }
                }
            }

            return ids.ToArray();
        }

        /// <summary>
        /// Parent item kinds that can own extras (behind the scenes, deleted scenes, featurettes, etc.).
        /// </summary>
        private static readonly BaseItemKind[] ExtrasParentKinds =
        [
            BaseItemKind.Movie,
            BaseItemKind.Series,
            BaseItemKind.Season,
            BaseItemKind.MusicVideo,
        ];

        /// <summary>
        /// Fetches extras from parent items and builds an optional reverse mapping from extra ID
        /// to owning series ID. Extras are linked to parent items via ExtraIds and cannot be found
        /// through standard library queries.
        /// </summary>
        /// <param name="libraryManager">The library manager instance</param>
        /// <param name="user">User context for the query</param>
        /// <param name="topParentIds">TopParentIds to scope the parent query</param>
        /// <param name="existingItems">Already-fetched items used to deduplicate extras</param>
        /// <param name="extraOwnerMap">Optional map to populate with extra ID → owning series ID</param>
        /// <param name="logger">Optional logger for diagnostics</param>
        /// <param name="listName">Optional list name for log messages</param>
        /// <returns>List of extras not already present in existingItems</returns>
        public static List<BaseItem> FetchExtras(
            ILibraryManager libraryManager,
            User user,
            Guid[] topParentIds,
            IReadOnlyList<BaseItem> existingItems,
            ConcurrentDictionary<Guid, Guid>? extraOwnerMap = null,
            ILogger? logger = null,
            string? listName = null)
        {
            logger?.LogDebug("IncludeExtras enabled for '{Name}', fetching extras from parent items", listName);

            var parentQuery = new InternalItemsQuery(user)
            {
                IncludeItemTypes = ExtrasParentKinds,
                Recursive = true,
                IsVirtualItem = false,
                TopParentIds = topParentIds,
            };
            var parents = libraryManager.GetItemsResult(parentQuery).Items;

            var seenIds = new HashSet<Guid>(existingItems.Select(i => i.Id));
            var extrasList = new List<BaseItem>();

            foreach (var parent in parents)
            {
                if (parent.ExtraIds == null || parent.ExtraIds.Length == 0)
                {
                    continue;
                }

                // Resolve series ID for this parent (if applicable) for reverse mapping
                Guid? parentSeriesId = null;
                if (extraOwnerMap != null)
                {
                    if (parent is Series)
                    {
                        parentSeriesId = parent.Id;
                    }
                    else if (parent is Season season)
                    {
                        parentSeriesId = season.SeriesId != Guid.Empty ? season.SeriesId : null;
                    }
                }

                foreach (var extra in parent.GetExtras())
                {
                    if (seenIds.Add(extra.Id))
                    {
                        extrasList.Add(extra);

                        // Build reverse mapping: extra ID → owning Series ID
                        if (parentSeriesId.HasValue)
                        {
                            extraOwnerMap!.TryAdd(extra.Id, parentSeriesId.Value);
                        }
                    }
                }
            }

            logger?.LogDebug("Found {ExtrasCount} extras from {ParentCount} parent items for '{Name}'",
                extrasList.Count, parents.Count, listName);

            return extrasList;
        }
    }
}
