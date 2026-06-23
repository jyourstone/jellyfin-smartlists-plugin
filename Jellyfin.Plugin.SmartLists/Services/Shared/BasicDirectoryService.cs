using System.Collections.Generic;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.SmartLists.Services.Shared
{
    /// <summary>
    /// Basic DirectoryService implementation for metadata refresh operations.
    /// Used by both playlist and collection services.
    /// </summary>
    public class BasicDirectoryService : IDirectoryService
    {
        public List<FileSystemMetadata> GetDirectories(string path) => [];
        public List<FileSystemMetadata> GetFiles(string path) => [];
        public FileSystemMetadata[] GetFileSystemEntries(string path) => [];
        public FileSystemMetadata? GetFile(string path) => null;
        public FileSystemMetadata? GetDirectory(string path) => null;
        public FileSystemMetadata? GetFileSystemEntry(string path) => null;
        public IReadOnlyList<string> GetFilePaths(string path) => [];
#if NET10_0_OR_GREATER
        public IReadOnlyList<string> GetFilePaths(string path, bool clearCache) => GetFilePaths(path);
#endif
        public IReadOnlyList<string> GetFilePaths(string path, bool clearCache, bool sort) => GetFilePaths(path);
        public bool IsAccessible(string path) => false;
    }
}
