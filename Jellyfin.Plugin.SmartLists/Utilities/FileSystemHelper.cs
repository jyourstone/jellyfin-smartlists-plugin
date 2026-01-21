using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Utilities;

/// <summary>
/// Provides common file system utility methods for safe file and directory operations.
/// </summary>
public static class FileSystemHelper
{
    /// <summary>
    /// System files that should be ignored when checking if a directory is empty.
    /// </summary>
    private static readonly HashSet<string> SystemFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store",
        "Thumbs.db",
        "desktop.ini"
    };

    /// <summary>
    /// Checks if a directory is effectively empty (contains no files except system files like .DS_Store).
    /// </summary>
    /// <param name="directoryPath">The directory path to check.</param>
    /// <returns>True if the directory is effectively empty, false otherwise.</returns>
    public static bool IsDirectoryEffectivelyEmpty(string directoryPath)
    {
        // Check if there are any subdirectories
        if (Directory.EnumerateDirectories(directoryPath).Any())
        {
            return false;
        }

        // Check if there are any non-system files
        return !Directory.EnumerateFiles(directoryPath)
            .Any(f => !SystemFiles.Contains(Path.GetFileName(f)));
    }

    /// <summary>
    /// Safely deletes a file, logging any errors.
    /// </summary>
    /// <param name="filePath">The file path to delete.</param>
    /// <param name="logger">Optional logger for debug/warning messages.</param>
    public static void SafeDeleteFile(string filePath, ILogger? logger = null)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                logger?.LogDebug("Deleted file {FilePath}", filePath);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to delete file {FilePath}", filePath);
        }
    }

    /// <summary>
    /// Safely deletes a directory recursively, logging any errors.
    /// </summary>
    /// <param name="directoryPath">The directory path to delete.</param>
    /// <param name="logger">Optional logger for debug/warning messages.</param>
    public static void SafeDeleteDirectory(string directoryPath, ILogger? logger = null)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
                logger?.LogDebug("Deleted directory {DirectoryPath}", directoryPath);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to delete directory {DirectoryPath}", directoryPath);
        }
    }

    /// <summary>
    /// Tries to delete a directory if it's effectively empty (no files except system files like .DS_Store).
    /// </summary>
    /// <param name="directoryPath">The directory path to delete if empty.</param>
    /// <param name="logger">Optional logger for debug/warning messages.</param>
    public static void TryDeleteEmptyDirectory(string directoryPath, ILogger? logger = null)
    {
        try
        {
            if (Directory.Exists(directoryPath) && IsDirectoryEffectivelyEmpty(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
                logger?.LogDebug("Deleted empty directory {DirectoryPath}", directoryPath);
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Could not delete directory {DirectoryPath}", directoryPath);
        }
    }
}
