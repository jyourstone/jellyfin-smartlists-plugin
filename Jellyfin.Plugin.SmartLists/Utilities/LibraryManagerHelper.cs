using System;
using System.Collections.Generic;
using System.Reflection;
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
    }
}
