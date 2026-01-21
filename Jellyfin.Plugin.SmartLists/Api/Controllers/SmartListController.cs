using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Constants;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Services.Playlists;
using Jellyfin.Plugin.SmartLists.Services.Collections;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using AutoRefreshService = Jellyfin.Plugin.SmartLists.Services.Shared.AutoRefreshService;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Jellyfin.Plugin.SmartLists.Api.Controllers
{
    /// <summary>
    /// SmartLists API controller.
    /// </summary>
    [ApiController]
    [Authorize(Policy = "RequiresElevation")]
    [Route("Plugins/SmartLists")]
    [Produces("application/json")]
    public partial class SmartListController(
        ILogger<SmartListController> logger,
        ILoggerFactory loggerFactory,
        IServerApplicationPaths applicationPaths,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IPlaylistManager playlistManager,
        ICollectionManager collectionManager,
        IUserDataManager userDataManager,
        IProviderManager providerManager,
        IManualRefreshService manualRefreshService,
        RefreshStatusService refreshStatusService,
        RefreshQueueService refreshQueueService,
        SmartListImageService imageService) : ControllerBase
    {
        private readonly ILoggerFactory _loggerFactory = loggerFactory;
        private readonly IServerApplicationPaths _applicationPaths = applicationPaths;
        private readonly IUserManager _userManager = userManager;
        private readonly ILibraryManager _libraryManager = libraryManager;
        private readonly IPlaylistManager _playlistManager = playlistManager;
        private readonly ICollectionManager _collectionManager = collectionManager;
        private readonly IUserDataManager _userDataManager = userDataManager;
        private readonly IProviderManager _providerManager = providerManager;
        private readonly IManualRefreshService _manualRefreshService = manualRefreshService;
        private readonly RefreshStatusService _refreshStatusService = refreshStatusService;
        private readonly RefreshQueueService _refreshQueueService = refreshQueueService;
        private readonly SmartListImageService _imageService = imageService;

        private Services.Playlists.PlaylistStore GetPlaylistStore()
        {
            var fileSystem = new SmartListFileSystem(_applicationPaths);
            var playlistLogger = _loggerFactory.CreateLogger<Services.Playlists.PlaylistStore>();
            return new Services.Playlists.PlaylistStore(fileSystem, playlistLogger);
        }

        private Services.Collections.CollectionStore GetCollectionStore()
        {
            var fileSystem = new SmartListFileSystem(_applicationPaths);
            var collectionLogger = _loggerFactory.CreateLogger<Services.Collections.CollectionStore>();
            return new Services.Collections.CollectionStore(fileSystem, collectionLogger);
        }

        private Services.Playlists.PlaylistService GetPlaylistService()
        {
            try
            {
                // Use a generic wrapper logger that implements ILogger<PlaylistService>
                var playlistServiceLogger = new ServiceLoggerAdapter<Services.Playlists.PlaylistService>(logger);
                return new Services.Playlists.PlaylistService(_userManager, _libraryManager, _playlistManager, _userDataManager, playlistServiceLogger, _providerManager, _imageService);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create PlaylistService");
                throw;
            }
        }

        private Services.Collections.CollectionService GetCollectionService()
        {
            try
            {
                // Use a generic wrapper logger that implements ILogger<CollectionService>
                var collectionServiceLogger = new ServiceLoggerAdapter<Services.Collections.CollectionService>(logger);
                return new Services.Collections.CollectionService(_libraryManager, _collectionManager, _userManager, _userDataManager, collectionServiceLogger, _providerManager, _imageService);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create CollectionService");
                throw;
            }
        }

        // Generic wrapper class to adapt the controller logger for service-specific loggers
        private sealed class ServiceLoggerAdapter<T>(ILogger logger) : ILogger<T>
        {
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                logger.Log(logLevel, eventId, state, exception, formatter);
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return logger.IsEnabled(logLevel);
            }

            IDisposable? ILogger.BeginScope<TState>(TState state)
            {
                return logger.BeginScope(state);
            }
        }

        /// <summary>
        /// Gets the user ID for a playlist.
        /// </summary>
        /// <param name="playlist">The playlist.</param>
        /// <returns>The user ID, or Guid.Empty if not found.</returns>
        private static Guid GetPlaylistUserId(SmartPlaylistDto playlist)
        {
            // If UserPlaylists exists, use first user
            if (playlist.UserPlaylists != null && playlist.UserPlaylists.Count > 0)
            {
                if (Guid.TryParse(playlist.UserPlaylists[0].UserId, out var userId) && userId != Guid.Empty)
                {
                    return userId;
                }
            }

            // Fallback to UserId field (backwards compatibility)
            // DEPRECATED: This check is for backwards compatibility with old single-user playlists.
            // It is planned to be removed in version 10.12. Use UserPlaylists array instead.
            if (!string.IsNullOrEmpty(playlist.UserId) && Guid.TryParse(playlist.UserId, out var userIdFromField) && userIdFromField != Guid.Empty)
            {
                return userIdFromField;
            }

            return Guid.Empty;
        }

        /// <summary>
        /// Gets all user IDs from a playlist, handling both old (UserId) and new (UserPlaylists) formats.
        /// Normalizes UserIds to consistent format (without dashes) for comparison.
        /// </summary>
        private static HashSet<string> GetPlaylistUserIds(SmartPlaylistDto playlist)
        {
            var userIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (playlist.UserPlaylists != null && playlist.UserPlaylists.Count > 0)
            {
                foreach (var mapping in playlist.UserPlaylists)
                {
                    if (!string.IsNullOrEmpty(mapping.UserId) && Guid.TryParse(mapping.UserId, out var userId) && userId != Guid.Empty)
                    {
                        // Normalize to standard format without dashes for consistent comparison
                        userIds.Add(userId.ToString("N"));
                    }
                }
            }
            else if (!string.IsNullOrEmpty(playlist.UserId) && Guid.TryParse(playlist.UserId, out var parsedUserId) && parsedUserId != Guid.Empty)
            {
                // Fallback to old format, normalize to standard format without dashes
                // DEPRECATED: This fallback is for backwards compatibility with old single-user playlists.
                // It is planned to be removed in version 10.12. Use UserPlaylists array instead.
                userIds.Add(parsedUserId.ToString("N"));
            }

            return userIds;
        }

        /// <summary>
        /// Validates a regex pattern to prevent injection attacks and ReDoS vulnerabilities.
        /// </summary>
        /// <param name="pattern">The regex pattern to validate.</param>
        /// <param name="errorMessage">Output parameter containing error message if validation fails.</param>
        /// <returns>True if the pattern is valid, false otherwise.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3012:Review code for regex injection vulnerabilities", Justification = "Pattern is validated with length limits and timeout to prevent ReDoS attacks")]
        private static bool IsValidRegexPattern(string pattern, out string errorMessage)
        {
            errorMessage = string.Empty;

            // Check for null or empty pattern
            if (string.IsNullOrWhiteSpace(pattern))
            {
                errorMessage = "Regex pattern cannot be null or empty";
                return false;
            }

            // Limit pattern length to prevent ReDoS attacks
            const int maxPatternLength = 1000;
            if (pattern.Length > maxPatternLength)
            {
                errorMessage = $"Regex pattern exceeds maximum length of {maxPatternLength} characters";
                return false;
            }

            // Try to compile the pattern with a timeout to detect ReDoS vulnerabilities
            try
            {
                var regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
                // Test with a simple string to ensure it compiles correctly
                _ = regex.IsMatch("test");
            }
            catch (ArgumentException)
            {
                // Invalid pattern syntax - this is acceptable, will be caught later
                // We just want to ensure it doesn't cause ReDoS
            }
            catch (RegexMatchTimeoutException)
            {
                errorMessage = "Regex pattern is too complex and may cause performance issues";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Extracts the smart list ID from a ZIP entry path.
        /// Handles both old format ({guid}.json) and new format ({guid}/config.json or {guid}/image.jpg).
        /// </summary>
        private static string? GetSmartListIdFromZipEntry(ZipArchiveEntry entry)
        {
            var fullName = entry.FullName;

            // Handle new format: {guid}/config.json or {guid}/image.jpg
            if (fullName.Contains('/'))
            {
                var parts = fullName.Split('/');
                if (parts.Length >= 1 && Guid.TryParse(parts[0], out _))
                {
                    return parts[0];
                }
            }

            // Handle old format: {guid}.json
            if (entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var nameWithoutExt = Path.GetFileNameWithoutExtension(entry.Name);
                if (Guid.TryParse(nameWithoutExt, out _))
                {
                    return nameWithoutExt;
                }
            }

            return null;
        }

        /// <summary>
        /// Extracts image files from ZIP entries and saves them to the smart list folder.
        /// </summary>
        private async Task<int> ExtractImagesForSmartListAsync(
            List<ZipArchiveEntry> entries,
            string smartListId,
            ISmartListFileSystem fileSystem)
        {
            var imageCount = 0;
            var folderPath = fileSystem.GetSmartListFolderPath(smartListId);

            foreach (var entry in entries)
            {
                // Skip JSON files (config)
                if (entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Skip empty entries and system files
                if (string.IsNullOrEmpty(entry.Name) ||
                    entry.Name.StartsWith("._") ||
                    entry.Name.StartsWith(".DS_Store"))
                {
                    continue;
                }

                // Check if it's an image file
                var extension = Path.GetExtension(entry.Name);
                if (!SmartListImageService.ImageFileExtensions.Contains(extension.ToLowerInvariant()))
                {
                    continue;
                }

                try
                {
                    // Ensure folder exists
                    Directory.CreateDirectory(folderPath);

                    var targetPath = Path.Combine(folderPath, entry.Name);

                    // Extract the image
                    using var entryStream = entry.Open();
                    using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await entryStream.CopyToAsync(fileStream);

                    logger.LogDebug("Extracted image {ImageName} for smart list {SmartListId}", entry.Name, smartListId);
                    imageCount++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to extract image {ImageName} for smart list {SmartListId}", entry.Name, smartListId);
                }
            }

            return imageCount;
        }

        /// <summary>
        /// Gets the current user ID from Jellyfin claims.
        /// </summary>
        /// <returns>The current user ID, or Guid.Empty if not found.</returns>
        private Guid GetCurrentUserId()
        {
            try
            {
                logger.LogDebug("Attempting to determine current user ID from Jellyfin claims...");

                // Use centralized extension method for claim parsing
                var userId = User.GetUserId();
                logger.LogDebug("User ID from claims: {UserId}", userId == Guid.Empty ? "not found" : userId.ToString());

                if (userId == Guid.Empty)
                {
                    logger.LogWarning("Could not determine current user ID from Jellyfin-UserId claim");
                }

                return userId;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error getting current user ID");
                return Guid.Empty;
            }
        }

        /// <summary>
        /// Validates the current user ID and retrieves the user object.
        /// Returns an error response if the user ID is invalid or the user is not found.
        /// </summary>
        /// <param name="currentUserId">The current user ID to validate</param>
        /// <param name="errorResult">The error response to return if validation fails</param>
        /// <param name="operationDescription">Description of the operation for error messages (default: "manage collections")</param>
        /// <returns>The User object if validation succeeds, null otherwise</returns>
        private Jellyfin.Database.Implementations.Entities.User? ValidateAndGetCurrentUser(Guid currentUserId, out ActionResult? errorResult, string operationDescription = "manage collections")
        {
            errorResult = null;

            if (currentUserId == Guid.Empty)
            {
                logger.LogError("Could not determine current user for collection operation");
                errorResult = Unauthorized(new ProblemDetails
                {
                    Title = "Authentication Error",
                    Detail = $"User authentication required. Please ensure you are logged in to {operationDescription}.",
                    Status = StatusCodes.Status401Unauthorized
                });
                return null;
            }

            var currentUser = _userManager.GetUserById(currentUserId);
            if (currentUser == null)
            {
                logger.LogError("Current user ID {UserId} exists in claims but was not found in user manager", currentUserId);
                errorResult = StatusCode(StatusCodes.Status401Unauthorized, new ProblemDetails
                {
                    Title = "Authentication Error",
                    Detail = "The authenticated user could not be found. Please log out and log back in.",
                    Status = StatusCodes.Status401Unauthorized
                });
                return null;
            }

            return currentUser;
        }

        /// <summary>
        /// Normalizes a user ID string to canonical "N" format (no dashes, lowercase).
        /// Handles both "N" format and "D" format (with dashes) input.
        /// This matches the format used by the client-side normalization.
        /// </summary>
        /// <param name="userId">The user ID string to normalize</param>
        /// <returns>Normalized user ID in "N" format, or original string if invalid GUID</returns>
        private static string NormalizeUserId(string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                return userId;
            }

            if (Guid.TryParse(userId, out var guid))
            {
                return guid.ToString("N").ToLowerInvariant();
            }

            return userId; // Return as-is if not a valid GUID
        }

        /// <summary>
        /// Normalizes and validates UserPlaylists array, ensuring all user IDs are valid GUIDs
        /// in standard "N" format (no dashes) and removing duplicates.
        /// </summary>
        /// <param name="playlist">The playlist to normalize</param>
        /// <param name="errorMessage">Output parameter containing error message if validation fails</param>
        /// <returns>True if validation succeeded, false otherwise</returns>
        private bool NormalizeAndValidateUserPlaylists(SmartPlaylistDto playlist, out string errorMessage)
        {
            errorMessage = string.Empty;
            if (playlist.UserPlaylists == null || playlist.UserPlaylists.Count == 0)
            {
                errorMessage = "At least one playlist user is required";
                return false;
            }

            var normalizedUserPlaylists = new List<SmartPlaylistDto.UserPlaylistMapping>();
            var seenUserIds = new HashSet<Guid>();

            foreach (var userMapping in playlist.UserPlaylists)
            {
                if (string.IsNullOrEmpty(userMapping.UserId) || !Guid.TryParse(userMapping.UserId, out var userId) || userId == Guid.Empty)
                {
                    errorMessage = "All user IDs must be valid GUIDs";
                    return false;
                }

                // Normalize UserId to standard format (without dashes) and check for duplicates
                // HashSet.Add() returns true if item was added (didn't exist), false if already exists
                if (seenUserIds.Add(userId))
                {
                    normalizedUserPlaylists.Add(new SmartPlaylistDto.UserPlaylistMapping
                    {
                        UserId = userId.ToString("N"), // Standard format without dashes
                        JellyfinPlaylistId = userMapping.JellyfinPlaylistId
                    });
                }
                else
                {
                    logger.LogWarning("Duplicate user ID {UserId} detected in UserPlaylists for playlist {Name}, skipping duplicate", userId, playlist.Name);
                }
            }

            // Replace with normalized and deduplicated list
            playlist.UserPlaylists = normalizedUserPlaylists;

            // Validate we still have at least one user after deduplication
            if (playlist.UserPlaylists.Count == 0)
            {
                errorMessage = "At least one playlist user is required after removing duplicates";
                return false;
            }

            // Set Public = false for multi-user playlists (multi-user playlists are always private)
            if (playlist.UserPlaylists.Count > 1)
            {
                playlist.Public = false;
                logger.LogDebug("Multi-user playlist detected ({UserCount} users), setting Public=false", playlist.UserPlaylists.Count);
            }

            return true;
        }

        /// <summary>
        /// Get all smart lists (playlists and collections).
        /// </summary>
        /// <param name="type">Optional filter by type (Playlist or Collection).</param>
        /// <returns>List of smart lists.</returns>
        [HttpGet]
        public async Task<ActionResult<IEnumerable<SmartListDto>>> GetSmartLists([FromQuery] string? type = null)
        {
            try
            {
                var allLists = new List<SmartListDto>();
                
                // Get playlists
                if (type == null || type.Equals("Playlist", StringComparison.OrdinalIgnoreCase))
                {
                    var playlistStore = GetPlaylistStore();
                    var playlists = await playlistStore.GetAllAsync();
                    foreach (var playlist in playlists)
                    {
                        playlist.MigrateLegacyFields();
                    }
                    allLists.AddRange(playlists);
                }
                
                // Get collections
                if (type == null || type.Equals("Collection", StringComparison.OrdinalIgnoreCase))
                {
                    var collectionStore = GetCollectionStore();
                    var collections = await collectionStore.GetAllAsync();
                    foreach (var collection in collections)
                    {
                        collection.MigrateLegacyFields();
                    }
                    allLists.AddRange(collections);
                }
                
                return Ok(allLists);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error retrieving smart lists");
                return StatusCode(StatusCodes.Status500InternalServerError, "Error retrieving smart lists");
            }
        }

        /// <summary>
        /// Get a specific smart list by ID (playlist or collection).
        /// </summary>
        /// <param name="id">The list ID.</param>
        /// <returns>The smart list.</returns>
        [HttpGet("{id}")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "ID is validated as GUID before use, preventing path injection")]
        public async Task<ActionResult<SmartListDto>> GetSmartList([FromRoute, Required] string id)
        {
            try
            {
                if (!Guid.TryParse(id, out var guidId))
                {
                    return BadRequest("Invalid list ID format");
                }

                // Try playlist first
                var playlistStore = GetPlaylistStore();
                var playlist = await playlistStore.GetByIdAsync(guidId);
                if (playlist != null)
                {
                    playlist.MigrateLegacyFields();
                    return Ok(playlist);
                }

                // Try collection
                var collectionStore = GetCollectionStore();
                var collection = await collectionStore.GetByIdAsync(guidId);
                if (collection != null)
                {
                    collection.MigrateLegacyFields();
                    return Ok(collection);
                }

                return NotFound();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error retrieving smart list {ListId}", id);
                return StatusCode(StatusCodes.Status500InternalServerError, "Error retrieving smart list");
            }
        }

        /// <summary>
        /// Create a new smart list (playlist or collection).
        /// </summary>
        /// <param name="list">The smart list to create (playlist or collection).</param>
        /// <returns>The created smart list.</returns>
        [HttpPost]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3012:Review code for regex injection vulnerabilities", Justification = "Regex patterns are validated with IsValidRegexPattern method including length limits and timeout")]
        public async Task<ActionResult<SmartListDto>> CreateSmartList([FromBody] SmartListDto? list, [FromQuery] bool skipRefresh = false)
        {
            if (list == null)
            {
                logger.LogWarning("CreateSmartList called with null list data");
                return BadRequest(new ProblemDetails
                {
                    Title = "Validation Error",
                    Detail = "List data is required",
                    Status = StatusCodes.Status400BadRequest
                });
            }

            // Route to appropriate handler based on type
            if (list.Type == Core.Enums.SmartListType.Collection)
            {
                return await CreateCollectionInternal(list as SmartCollectionDto ?? JsonSerializer.Deserialize<SmartCollectionDto>(JsonSerializer.Serialize(list))!, skipRefresh);
            }
            else
            {
                return await CreatePlaylistInternal(list as SmartPlaylistDto ?? JsonSerializer.Deserialize<SmartPlaylistDto>(JsonSerializer.Serialize(list))!, skipRefresh);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3012:Review code for regex injection vulnerabilities", Justification = "Regex patterns are validated with IsValidRegexPattern method including length limits and timeout")]
        private async Task<ActionResult<SmartListDto>> CreatePlaylistInternal(SmartPlaylistDto playlist, bool skipRefresh = false)
        {

            // Set defaults for optional fields
            // These fields are optional for creation (we generate/set them)
            if (string.IsNullOrEmpty(playlist.Id))
            {
                playlist.Id = Guid.NewGuid().ToString();
            }

            if (playlist.Order == null)
            {
                playlist.Order = new OrderDto { SortOptions = [] };
            }
            else if (playlist.Order.SortOptions == null || playlist.Order.SortOptions.Count == 0)
            {
                // Order is provided but SortOptions is empty - initialize it
                playlist.Order.SortOptions = [];
            }

            // Ensure Type is set correctly
            playlist.Type = Core.Enums.SmartListType.Playlist;

            // Now validate model state after setting defaults
            if (!ModelState.IsValid)
            {
                var errors = ModelState
                    .Where(x => x.Value?.Errors.Count > 0)
                    .SelectMany(x => x.Value!.Errors.Select(e => 
                    {
                        var fieldName = string.IsNullOrEmpty(x.Key) ? "Unknown" : x.Key;
                        var errorMessage = string.IsNullOrEmpty(e.ErrorMessage) ? "Invalid value" : e.ErrorMessage;
                        // Include exception message if available (for deserialization errors)
                        if (e.Exception != null && !string.IsNullOrEmpty(e.Exception.Message))
                        {
                            errorMessage = $"{errorMessage} ({e.Exception.Message})";
                        }
                        return $"{fieldName}: {errorMessage}";
                    }))
                    .ToList();
                
                var errorMessage = errors.Count > 0 
                    ? string.Join("; ", errors) 
                    : "One or more validation errors occurred";
                
                logger.LogWarning("Model validation failed for CreateSmartPlaylist: {Errors}", errorMessage);
                
                // Return detailed error response that will be serialized properly
                var problemDetails = new ValidationProblemDetails(ModelState)
                {
                    Title = "Validation Error",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = errorMessage
                };
                
                return BadRequest(problemDetails);
            }

            // Additional validation for required fields
            if (string.IsNullOrWhiteSpace(playlist.Name))
            {
                logger.LogWarning("CreateSmartPlaylist called with empty Name");
                return BadRequest(new ProblemDetails
                {
                    Title = "Validation Error",
                    Detail = "Playlist name is required",
                    Status = StatusCodes.Status400BadRequest
                });
            }

            // Migrate old UserId format to UserPlaylists array if needed
            // DEPRECATED: playlist.UserId is for backwards compatibility with old single-user playlists.
            // It is planned to be removed in version 10.12. Use UserPlaylists array instead.
            if (playlist.UserPlaylists == null || playlist.UserPlaylists.Count == 0)
            {
                if (!string.IsNullOrEmpty(playlist.UserId) && Guid.TryParse(playlist.UserId, out var userId) && userId != Guid.Empty)
                {
                    // Migrate single UserId to UserPlaylists array
                    playlist.UserPlaylists = new List<SmartPlaylistDto.UserPlaylistMapping>
                    {
                        new SmartPlaylistDto.UserPlaylistMapping
                        {
                            UserId = playlist.UserId,
                            JellyfinPlaylistId = playlist.JellyfinPlaylistId
                        }
                    };
                }
            }

            // Normalize and validate UserPlaylists
            if (!NormalizeAndValidateUserPlaylists(playlist, out var validationError))
            {
                logger.LogWarning("CreateSmartPlaylist validation failed: {Error}. Name={Name}", validationError, playlist.Name);
                return BadRequest(new ProblemDetails
                {
                    Title = "Validation Error",
                    Detail = validationError,
                    Status = StatusCodes.Status400BadRequest
                });
            }

            // Remove old fields when using new UserPlaylists format (they will be set to null in JSON)
            playlist.UserId = null;
            playlist.JellyfinPlaylistId = null;

            var stopwatch = Stopwatch.StartNew();
            logger.LogDebug("CreateSmartPlaylist called for playlist: {PlaylistName}", playlist.Name);
            logger.LogDebug("Playlist data received: Name={Name}, UserCount={UserCount}, Public={Public}, ExpressionSets={ExpressionSetCount}, MediaTypes={MediaTypes}",
                playlist.Name, playlist.UserPlaylists?.Count ?? 0, playlist.Public, playlist.ExpressionSets?.Count ?? 0,
                playlist.MediaTypes != null ? string.Join(",", playlist.MediaTypes) : "None");

            if (playlist.ExpressionSets != null)
            {
                logger.LogDebug("ExpressionSets count: {Count}", playlist.ExpressionSets.Count);
                for (int i = 0; i < playlist.ExpressionSets.Count; i++)
                {
                    var set = playlist.ExpressionSets[i];
                    logger.LogDebug("ExpressionSet {Index}: {ExpressionCount} expressions", i, set?.Expressions?.Count ?? 0);
                    if (set?.Expressions != null)
                    {
                        for (int j = 0; j < set.Expressions.Count; j++)
                        {
                            var expr = set.Expressions[j];
                            logger.LogDebug("Expression {SetIndex}.{ExprIndex}: {MemberName} {Operator} '{TargetValue}'",
                                i, j, expr?.MemberName, expr?.Operator, expr?.TargetValue);
                        }
                    }
                }
            }

            try
            {
                // Ensure Type is set (should be set by constructor, but ensure it's correct)
                if (playlist.Type == Core.Enums.SmartListType.Collection)
                {
                    logger.LogWarning("CreateSmartPlaylist called with Collection type, this endpoint is for Playlists only");
                    return BadRequest("This endpoint is for creating playlists only. Use the collections endpoint for collections.");
                }
                playlist.Type = Core.Enums.SmartListType.Playlist;

                if (string.IsNullOrEmpty(playlist.Id))
                {
                    playlist.Id = Guid.NewGuid().ToString();
                    logger.LogDebug("Generated new playlist ID: {Id}", playlist.Id);
                }

                // Ensure FileName is set (will be set by store, but initialize here for validation)
                if (string.IsNullOrEmpty(playlist.FileName))
                {
                    playlist.FileName = $"{playlist.Id}.json";
                }

                // Ensure Order is initialized if not provided
                if (playlist.Order == null)
                {
                    playlist.Order = new OrderDto { SortOptions = [] };
                }

                var playlistStore = GetPlaylistStore();

                // Validate regex patterns before saving
                if (playlist.ExpressionSets != null)
                {
                    foreach (var expressionSet in playlist.ExpressionSets)
                    {
                        if (expressionSet.Expressions != null)
                        {
                            foreach (var expression in expressionSet.Expressions)
                            {
                                if (expression.Operator == "MatchRegex" && !string.IsNullOrEmpty(expression.TargetValue))
                                {
                                    // Validate regex pattern to prevent injection attacks
                                    if (!IsValidRegexPattern(expression.TargetValue, out var regexError))
                                    {
                                        return BadRequest($"Invalid regex pattern: {regexError}");
                                    }

                                    try
                                    {
                                        // Use a timeout to prevent ReDoS attacks
                                        // Pattern is already validated by IsValidRegexPattern above
                                        var regex = new Regex(expression.TargetValue, RegexOptions.None, TimeSpan.FromMilliseconds(100));
                                    }
                                    catch (ArgumentException ex)
                                    {
                                        logger.LogError(ex, "Invalid regex pattern '{Pattern}' during validation", expression.TargetValue);
                                        return BadRequest($"Invalid regex pattern '{expression.TargetValue}': {ex.Message}");
                                    }
                                    catch (RegexMatchTimeoutException ex)
                                    {
                                        logger.LogError(ex, "Regex pattern '{Pattern}' timed out during validation", expression.TargetValue);
                                        return BadRequest($"Regex pattern '{expression.TargetValue}' is too complex or caused a timeout");
                                    }
                                }
                            }
                        }
                    }
                }

                // Set DateCreated to current time for new playlists
                playlist.DateCreated = DateTime.UtcNow;

                var createdPlaylist = await playlistStore.SaveAsync(playlist);
                logger.LogInformation("Created smart playlist: {PlaylistName}", playlist.Name);

                // Update the auto-refresh cache with the new playlist
                AutoRefreshService.Instance?.UpdatePlaylistInCache(createdPlaylist);

                // Clear the rule cache to ensure the new playlist rules are properly compiled
                SmartList.ClearRuleCache(logger);
                logger.LogDebug("Cleared rule cache after creating playlist '{PlaylistName}'", playlist.Name);

                // Enqueue refresh operation if the playlist is enabled and skipRefresh is false
                if (createdPlaylist.Enabled && !skipRefresh)
                {
                    logger.LogDebug("Enqueuing refresh for newly created playlist {PlaylistName}", playlist.Name);
                    var listId = createdPlaylist.Id ?? Guid.NewGuid().ToString();
                    
                    var queueItem = new RefreshQueueItem
                    {
                        ListId = listId,
                        ListName = createdPlaylist.Name,
                        ListType = Core.Enums.SmartListType.Playlist,
                        OperationType = RefreshOperationType.Create,
                        ListData = createdPlaylist,
                        UserId = createdPlaylist.UserPlaylists?.FirstOrDefault()?.UserId ?? createdPlaylist.UserId,
                        TriggerType = Core.Enums.RefreshTriggerType.Manual
                    };

                    _refreshQueueService.EnqueueOperation(queueItem);
                    logger.LogInformation("Created smart playlist '{PlaylistName}' and enqueued for refresh", playlist.Name);
                }
                else if (skipRefresh)
                {
                    logger.LogInformation("Created smart playlist '{PlaylistName}' (refresh deferred for image uploads)", playlist.Name);
                }
                else
                {
                    logger.LogInformation("Created disabled smart playlist '{PlaylistName}' (not enqueued for refresh)", playlist.Name);
                }

                // Return the created playlist immediately (refresh will happen in background if enabled)
                // Note: JellyfinPlaylistId will be populated after the queue processes the refresh
                stopwatch.Stop();

                return CreatedAtAction(nameof(GetSmartList), new { id = createdPlaylist.Id }, createdPlaylist);
            }
            catch (ArgumentException ex) when (ex.Message.Contains("Invalid regex pattern"))
            {
                stopwatch.Stop();
                logger.LogError(ex, "Unexpected regex validation error in smart playlist creation after {ElapsedTime}ms", stopwatch.ElapsedMilliseconds);
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                logger.LogError(ex, "Error creating smart playlist after {ElapsedTime}ms", stopwatch.ElapsedMilliseconds);
                return StatusCode(StatusCodes.Status500InternalServerError, "Error creating smart playlist");
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3012:Review code for regex injection vulnerabilities", Justification = "Regex patterns are validated with IsValidRegexPattern method including length limits and timeout")]
        private async Task<ActionResult<SmartListDto>> CreateCollectionInternal(SmartCollectionDto collection, bool skipRefresh = false)
        {
            // Set defaults for optional fields
            if (string.IsNullOrEmpty(collection.Id))
            {
                collection.Id = Guid.NewGuid().ToString();
            }

            if (collection.Order == null)
            {
                collection.Order = new OrderDto { SortOptions = [] };
            }
            else if (collection.Order.SortOptions == null || collection.Order.SortOptions.Count == 0)
            {
                collection.Order.SortOptions = [];
            }

            // Set default owner user if not specified, or normalize if already set
            if (string.IsNullOrEmpty(collection.UserId) || !Guid.TryParse(collection.UserId, out var userId) || userId == Guid.Empty)
            {
                // Default to currently logged-in user
                var currentUserId = GetCurrentUserId();
                var currentUser = ValidateAndGetCurrentUser(currentUserId, out var errorResult);
                if (currentUser == null)
                {
                    return errorResult!;
                }

                collection.UserId = currentUser.Id.ToString("N").ToLowerInvariant();
                logger.LogDebug("Set default collection owner to currently logged-in user: {Username} ({UserId})", currentUser.Username, currentUser.Id);
            }
            else
            {
                // Normalize existing UserId to canonical "N" format (no dashes)
                collection.UserId = NormalizeUserId(collection.UserId);
                logger.LogDebug("Normalized collection UserId to canonical format: {UserId}", collection.UserId);
            }

            // Ensure Type is set correctly
            collection.Type = Core.Enums.SmartListType.Collection;

            // Validate required fields
            if (string.IsNullOrWhiteSpace(collection.Name))
            {
                logger.LogWarning("CreateCollectionInternal called with empty Name");
                return BadRequest(new ProblemDetails
                {
                    Title = "Validation Error",
                    Detail = "Collection name is required",
                    Status = StatusCodes.Status400BadRequest
                });
            }

            var stopwatch = Stopwatch.StartNew();
            logger.LogDebug("CreateCollectionInternal called for collection: {CollectionName}", collection.Name);

            try
            {
                if (string.IsNullOrEmpty(collection.Id))
                {
                    collection.Id = Guid.NewGuid().ToString();
                    logger.LogDebug("Generated new collection ID: {Id}", collection.Id);
                }

                if (string.IsNullOrEmpty(collection.FileName))
                {
                    collection.FileName = $"{collection.Id}.json";
                }

                if (collection.Order == null)
                {
                    collection.Order = new OrderDto { SortOptions = [] };
                }

                var collectionStore = GetCollectionStore();

                // Check for duplicate collection names (Jellyfin doesn't allow collections with the same name)
                var formattedName = NameFormatter.FormatPlaylistName(collection.Name);
                var allCollections = await collectionStore.GetAllAsync();
                var duplicateCollection = allCollections.FirstOrDefault(c => 
                    c.Id != collection.Id && 
                    string.Equals(NameFormatter.FormatPlaylistName(c.Name), formattedName, StringComparison.OrdinalIgnoreCase));
                
                if (duplicateCollection != null)
                {
                    logger.LogWarning("Cannot create collection '{CollectionName}' - a collection with this name already exists", collection.Name);
                    return BadRequest(new ProblemDetails
                    {
                        Title = "Validation Error",
                        Detail = $"A collection named '{formattedName}' already exists. Jellyfin does not allow multiple collections with the same name.",
                        Status = StatusCodes.Status400BadRequest
                    });
                }

                // Validate regex patterns before saving
                if (collection.ExpressionSets != null)
                {
                    foreach (var expressionSet in collection.ExpressionSets)
                    {
                        if (expressionSet.Expressions != null)
                        {
                            foreach (var expression in expressionSet.Expressions)
                            {
                                if (expression.Operator == "MatchRegex" && !string.IsNullOrEmpty(expression.TargetValue))
                                {
                                    if (!IsValidRegexPattern(expression.TargetValue, out var validationError))
                                    {
                                        return BadRequest($"Invalid regex pattern: {validationError}");
                                    }

                                    try
                                    {
                                        var regex = new Regex(expression.TargetValue, RegexOptions.None, TimeSpan.FromMilliseconds(100));
                                    }
                                    catch (ArgumentException ex)
                                    {
                                        logger.LogError(ex, "Invalid regex pattern '{Pattern}' during validation", expression.TargetValue);
                                        return BadRequest($"Invalid regex pattern '{expression.TargetValue}': {ex.Message}");
                                    }
                                    catch (RegexMatchTimeoutException ex)
                                    {
                                        logger.LogError(ex, "Regex pattern '{Pattern}' timed out during validation", expression.TargetValue);
                                        return BadRequest($"Regex pattern '{expression.TargetValue}' is too complex or caused a timeout");
                                    }
                                }
                            }
                        }
                    }
                }

                // Set DateCreated to current time for new collections
                collection.DateCreated = DateTime.UtcNow;

                var createdCollection = await collectionStore.SaveAsync(collection);
                logger.LogInformation("Created smart collection: {CollectionName}", collection.Name);

                // Update the auto-refresh cache with the new collection
                AutoRefreshService.Instance?.UpdateCollectionInCache(createdCollection);

                // Clear the rule cache
                SmartList.ClearRuleCache(logger);
                logger.LogDebug("Cleared rule cache after creating collection '{CollectionName}'", collection.Name);

                // Enqueue refresh operation if the collection is enabled and skipRefresh is false
                if (createdCollection.Enabled && !skipRefresh)
                {
                    logger.LogDebug("Enqueuing refresh for newly created collection {CollectionName}", collection.Name);
                    var listId = createdCollection.Id ?? Guid.NewGuid().ToString();
                    var queueItem = new RefreshQueueItem
                    {
                        ListId = listId,
                        ListName = createdCollection.Name,
                        ListType = Core.Enums.SmartListType.Collection,
                        OperationType = RefreshOperationType.Create,
                        ListData = createdCollection,
                        UserId = createdCollection.UserId,
                        TriggerType = Core.Enums.RefreshTriggerType.Manual
                    };

                    _refreshQueueService.EnqueueOperation(queueItem);
                    logger.LogInformation("Created smart collection '{CollectionName}' and enqueued for refresh", collection.Name);
                }
                else if (skipRefresh)
                {
                    logger.LogInformation("Created smart collection '{CollectionName}' (refresh deferred for image uploads)", collection.Name);
                }
                else
                {
                    logger.LogInformation("Created disabled smart collection '{CollectionName}' (not enqueued for refresh)", collection.Name);
                }

                // Return the created collection immediately (refresh will happen in background if enabled)
                // Note: JellyfinCollectionId will be populated after the queue processes the refresh
                stopwatch.Stop();

                return CreatedAtAction(nameof(GetSmartList), new { id = createdCollection.Id }, createdCollection);
            }
            catch (ArgumentException ex) when (ex.Message.Contains("Invalid regex pattern"))
            {
                stopwatch.Stop();
                logger.LogError(ex, "Unexpected regex validation error in smart collection creation after {ElapsedTime}ms", stopwatch.ElapsedMilliseconds);
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                logger.LogError(ex, "Error creating smart collection after {ElapsedTime}ms", stopwatch.ElapsedMilliseconds);
                return StatusCode(StatusCodes.Status500InternalServerError, "Error creating smart collection");
            }
        }

        /// <summary>
        /// Update an existing smart list (playlist or collection).
        /// </summary>
        /// <param name="id">The list ID.</param>
        /// <param name="list">The updated smart list.</param>
        /// <param name="skipRefresh">If true, skip queueing refresh (caller will trigger refresh after image operations).</param>
        /// <returns>The updated smart list.</returns>
        [HttpPut("{id}")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "ID is validated as GUID before use, preventing path injection")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3012:Review code for regex injection vulnerabilities", Justification = "Regex patterns are validated with IsValidRegexPattern method including length limits and timeout")]
        public async Task<ActionResult<SmartListDto>> UpdateSmartList([FromRoute, Required] string id, [FromBody, Required] SmartListDto list, [FromQuery] bool skipRefresh = false)
        {
            if (list == null)
            {
                return BadRequest("List data is required");
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                if (!Guid.TryParse(id, out var guidId))
                {
                    return BadRequest("Invalid list ID format");
                }

                // Determine type and route to appropriate handler
                // Try to find existing list to determine type
                var playlistStore = GetPlaylistStore();
                var existingPlaylist = await playlistStore.GetByIdAsync(guidId);
                if (existingPlaylist != null)
                {
                    // Handle type conversion: playlist → collection
                    if (list.Type == Core.Enums.SmartListType.Collection)
                    {
                        logger.LogInformation("Converting playlist '{Name}' to collection", existingPlaylist.Name);
                        
                        // Convert to collection DTO and create as new collection
                        var collectionDto = list as SmartCollectionDto ?? JsonSerializer.Deserialize<SmartCollectionDto>(JsonSerializer.Serialize(list))!;
                        collectionDto.Id = id; // Keep the same ID
                        collectionDto.FileName = existingPlaylist.FileName; // Keep the same filename
                        collectionDto.JellyfinCollectionId = null; // Clear old Jellyfin ID
                        
                        // Ensure UserId is set (required for collections for rule evaluation context)
                        if (string.IsNullOrEmpty(collectionDto.UserId))
                        {
                            // Strategy: prefer the authenticated user if they're a playlist owner,
                            // otherwise use the playlist's primary owner (first owner from UserPlaylists)
                            var currentUserId = GetCurrentUserId();
                            
                            // Try to get from existing playlist's UserId first (legacy single-user format)
                            if (!string.IsNullOrEmpty(existingPlaylist.UserId))
                            {
                                collectionDto.UserId = existingPlaylist.UserId;
                                logger.LogDebug("Set collection owner from playlist's UserId field: {UserId}", existingPlaylist.UserId);
                            }
                            // Check if authenticated user is one of the playlist owners (preferred)
                            else if (currentUserId != Guid.Empty && 
                                     existingPlaylist.UserPlaylists != null && 
                                     existingPlaylist.UserPlaylists.Any(up => Guid.TryParse(up.UserId, out var upId) && upId == currentUserId))
                            {
                                collectionDto.UserId = currentUserId.ToString("N").ToLowerInvariant();
                                logger.LogDebug("Set collection owner to authenticated user (who is a playlist owner): {UserId}", currentUserId);
                            }
                            // Use the playlist's primary owner (first user in UserPlaylists array)
                            // Note: This is legitimate - we're using a user who explicitly owns this playlist,
                            // not an arbitrary system user
                            else if (existingPlaylist.UserPlaylists != null && existingPlaylist.UserPlaylists.Count > 0)
                            {
                                collectionDto.UserId = existingPlaylist.UserPlaylists[0].UserId;
                                logger.LogDebug("Set collection owner to playlist's primary owner: {UserId}", collectionDto.UserId);
                            }
                            // Fallback to CreatedByUserId if available
                            else if (!string.IsNullOrEmpty(existingPlaylist.CreatedByUserId))
                            {
                                collectionDto.UserId = existingPlaylist.CreatedByUserId;
                                logger.LogDebug("Set collection owner from playlist's CreatedByUserId: {UserId}", existingPlaylist.CreatedByUserId);
                            }
                            // Last resort: use the currently authenticated user performing the conversion
                            else if (currentUserId != Guid.Empty)
                            {
                                collectionDto.UserId = currentUserId.ToString("N").ToLowerInvariant();
                                logger.LogDebug("Set collection owner to authenticated user performing conversion: {UserId}", currentUserId);
                            }
                            
                            // Validation: if we still don't have a UserId, fail the conversion
                            if (string.IsNullOrEmpty(collectionDto.UserId))
                            {
                                logger.LogError("Cannot convert playlist '{Name}' to collection: unable to determine owner user ID", existingPlaylist.Name);
                                return BadRequest(new ProblemDetails
                                {
                                    Title = "Conversion Error",
                                    Detail = "Cannot determine collection owner. The playlist has no associated users and you are not authenticated.",
                                    Status = StatusCodes.Status400BadRequest
                                });
                            }

                            // Normalize + validate resolved owner exists (collections require valid owner context)
                            if (!Guid.TryParse(collectionDto.UserId, out var resolvedOwnerId) || resolvedOwnerId == Guid.Empty)
                            {
                                logger.LogError("Cannot convert playlist '{Name}' to collection: owner user ID '{UserId}' is not a valid GUID", existingPlaylist.Name, collectionDto.UserId);
                                return BadRequest(new ProblemDetails
                                {
                                    Title = "Conversion Error",
                                    Detail = "Collection owner is not a valid user ID.",
                                    Status = StatusCodes.Status400BadRequest
                                });
                            }

                            var resolvedOwnerUser = _userManager.GetUserById(resolvedOwnerId);
                            if (resolvedOwnerUser == null)
                            {
                                logger.LogError("Cannot convert playlist '{Name}' to collection: owner user {UserId} not found in user manager", existingPlaylist.Name, resolvedOwnerId);
                                return BadRequest(new ProblemDetails
                                {
                                    Title = "Conversion Error",
                                    Detail = "Collection owner user not found. The user may have been deleted. Please choose a valid owner.",
                                    Status = StatusCodes.Status400BadRequest
                                });
                            }

                            // Normalize to canonical "N" format (no dashes) for collections
                            collectionDto.UserId = resolvedOwnerId.ToString("N").ToLowerInvariant();
                            logger.LogDebug("Validated and normalized collection owner: {Username} ({UserId})", resolvedOwnerUser.Username, collectionDto.UserId);
                        }
                        
                        // Preserve creator information from original playlist
                        if (string.IsNullOrEmpty(collectionDto.CreatedByUserId) && !string.IsNullOrEmpty(existingPlaylist.CreatedByUserId))
                        {
                            collectionDto.CreatedByUserId = existingPlaylist.CreatedByUserId;
                        }
                        
                        // Delete-first approach for atomicity: if any deletion fails, original state is preserved
                        var playlistService = GetPlaylistService();
                        await playlistService.DeleteAllJellyfinPlaylistsForUsersAsync(existingPlaylist);

                        // Delete old playlist configuration from store
                        await playlistStore.DeleteAsync(guidId);
                        
                        // Save new collection configuration (only after successful cleanup)
                        var newCollectionStore = GetCollectionStore();
                        await newCollectionStore.SaveAsync(collectionDto);
                        
                        // Enqueue refresh operation for the converted collection only if enabled
                        if (collectionDto.Enabled)
                        {
                            try
                            {
                                var listId = collectionDto.Id ?? Guid.NewGuid().ToString();
                                var queueItem = new RefreshQueueItem
                                {
                                    ListId = listId,
                                    ListName = collectionDto.Name,
                                    ListType = Core.Enums.SmartListType.Collection,
                                    OperationType = RefreshOperationType.Create,
                                    ListData = collectionDto,
                                    UserId = collectionDto.UserId,
                                    TriggerType = Core.Enums.RefreshTriggerType.Manual
                                };

                                _refreshQueueService.EnqueueOperation(queueItem);
                                logger.LogInformation("Successfully converted playlist to collection '{Name}' and enqueued for refresh", collectionDto.Name);
                            }
                            catch (Exception enqueueEx)
                            {
                                // Log but don't fail the conversion - the collection is already saved
                                logger.LogWarning(enqueueEx, "Failed to enqueue refresh for converted collection '{Name}', but conversion succeeded", collectionDto.Name);
                            }
                        }
                        else
                        {
                            logger.LogInformation("Successfully converted playlist to disabled collection '{Name}' (not enqueued for refresh)", collectionDto.Name);
                        }
                        
                        return Ok(collectionDto);
                    }
                    
                    // Normal playlist update
                    return await UpdatePlaylistInternal(id, guidId, list as SmartPlaylistDto ?? JsonSerializer.Deserialize<SmartPlaylistDto>(JsonSerializer.Serialize(list))!, skipRefresh);
                }

                var collectionStore = GetCollectionStore();
                var existingCollection = await collectionStore.GetByIdAsync(guidId);
                if (existingCollection != null)
                {
                    // Handle type conversion: collection → playlist
                    if (list.Type == Core.Enums.SmartListType.Playlist)
                    {
                        logger.LogInformation("Converting collection '{Name}' to playlist", existingCollection.Name);
                        
                        // Convert to playlist DTO and create as new playlist
                        var playlistDto = list as SmartPlaylistDto ?? JsonSerializer.Deserialize<SmartPlaylistDto>(JsonSerializer.Serialize(list))!;
                        playlistDto.Id = id; // Keep the same ID
                        playlistDto.FileName = existingCollection.FileName; // Keep the same filename
                        playlistDto.JellyfinPlaylistId = null; // Clear old Jellyfin ID
                        
                        // Ensure User field is set (required for playlists)
                        if (string.IsNullOrEmpty(playlistDto.UserId))
                        {
                            playlistDto.UserId = existingCollection.UserId; // Carry over from collection
                        }
                        
                        // Preserve creator information from original collection
                        if (string.IsNullOrEmpty(playlistDto.CreatedByUserId) && !string.IsNullOrEmpty(existingCollection.CreatedByUserId))
                        {
                            playlistDto.CreatedByUserId = existingCollection.CreatedByUserId;
                        }
                        
                        // Delete-first approach for atomicity: if any deletion fails, original state is preserved
                        var collectionService = GetCollectionService();
                        await collectionService.DeleteAsync(existingCollection);

                        // Delete old collection configuration from store
                        await collectionStore.DeleteAsync(guidId);

                        // Save new playlist configuration (only after successful cleanup)
                        var newPlaylistStore = GetPlaylistStore();
                        await newPlaylistStore.SaveAsync(playlistDto);
                        
                        // Enqueue refresh operation for the converted playlist only if enabled
                        if (playlistDto.Enabled)
                        {
                            try
                            {
                                var listId = playlistDto.Id ?? Guid.NewGuid().ToString();
                                var queueItem = new RefreshQueueItem
                                {
                                    ListId = listId,
                                    ListName = playlistDto.Name,
                                    ListType = Core.Enums.SmartListType.Playlist,
                                    OperationType = RefreshOperationType.Create,
                                    ListData = playlistDto,
                                    UserId = playlistDto.UserId,
                                    TriggerType = Core.Enums.RefreshTriggerType.Manual
                                };

                                _refreshQueueService.EnqueueOperation(queueItem);
                                logger.LogInformation("Successfully converted collection to playlist '{Name}' and enqueued for refresh", playlistDto.Name);
                            }
                            catch (Exception enqueueEx)
                            {
                                // Log but don't fail the conversion - the playlist is already saved
                                logger.LogWarning(enqueueEx, "Failed to enqueue refresh for converted playlist '{Name}', but conversion succeeded", playlistDto.Name);
                            }
                        }
                        else
                        {
                            logger.LogInformation("Successfully converted collection to disabled playlist '{Name}' (not enqueued for refresh)", playlistDto.Name);
                        }
                        
                        return Ok(playlistDto);
                    }
                    
                    // Normal collection update
                    return await UpdateCollectionInternal(id, guidId, list as SmartCollectionDto ?? JsonSerializer.Deserialize<SmartCollectionDto>(JsonSerializer.Serialize(list))!, skipRefresh);
                }

                return NotFound("Smart list not found");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                logger.LogError(ex, "Error updating smart list {ListId} after {ElapsedTime}ms", id, stopwatch.ElapsedMilliseconds);
                return StatusCode(StatusCodes.Status500InternalServerError, "Error updating smart list");
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3012:Review code for regex injection vulnerabilities", Justification = "Regex patterns are validated with IsValidRegexPattern method including length limits and timeout")]
        private async Task<ActionResult<SmartListDto>> UpdatePlaylistInternal(string id, Guid guidId, SmartPlaylistDto playlist, bool skipRefresh = false)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var playlistStore = GetPlaylistStore();
                var existingPlaylist = await playlistStore.GetByIdAsync(guidId);
                if (existingPlaylist == null)
                {
                    return NotFound("Smart playlist not found");
                }

                // Validate regex patterns before saving
                if (playlist.ExpressionSets != null)
                {
                    foreach (var expressionSet in playlist.ExpressionSets)
                    {
                        if (expressionSet.Expressions != null)
                        {
                            foreach (var expression in expressionSet.Expressions)
                            {
                                if (expression.Operator == "MatchRegex" && !string.IsNullOrEmpty(expression.TargetValue))
                                {
                                    // Validate regex pattern to prevent injection attacks
                                    if (!IsValidRegexPattern(expression.TargetValue, out var regexError))
                                    {
                                        return BadRequest($"Invalid regex pattern: {regexError}");
                                    }

                                    try
                                    {
                                        // Use a timeout to prevent ReDoS attacks
                                        // Pattern is already validated by IsValidRegexPattern above
                                        var regex = new Regex(expression.TargetValue, RegexOptions.None, TimeSpan.FromMilliseconds(100));
                                    }
                                    catch (ArgumentException ex)
                                    {
                                        logger.LogError(ex, "Invalid regex pattern '{Pattern}' during validation", expression.TargetValue);
                                        return BadRequest($"Invalid regex pattern '{expression.TargetValue}': {ex.Message}");
                                    }
                                    catch (RegexMatchTimeoutException ex)
                                    {
                                        logger.LogError(ex, "Regex pattern '{Pattern}' timed out during validation", expression.TargetValue);
                                        return BadRequest($"Regex pattern '{expression.TargetValue}' is too complex or caused a timeout");
                                    }
                                }
                            }
                        }
                    }
                }

                // Migrate old format to new format if needed
                if (existingPlaylist.UserPlaylists == null || existingPlaylist.UserPlaylists.Count == 0)
                {
                    if (!string.IsNullOrEmpty(existingPlaylist.UserId))
                    {
                        existingPlaylist.UserPlaylists = new List<SmartPlaylistDto.UserPlaylistMapping>
                        {
                            new SmartPlaylistDto.UserPlaylistMapping
                            {
                                UserId = Guid.TryParse(existingPlaylist.UserId, out var userId) 
                                    ? userId.ToString("N")  // Normalize to standard format
                                    : existingPlaylist.UserId,
                                JellyfinPlaylistId = existingPlaylist.JellyfinPlaylistId
                            }
                        };
                    }
                }

                if (playlist.UserPlaylists == null || playlist.UserPlaylists.Count == 0)
                {
                    // DEPRECATED: playlist.UserId is for backwards compatibility with old single-user playlists.
                    // It is planned to be removed in version 10.12. Use UserPlaylists array instead.
                    if (!string.IsNullOrEmpty(playlist.UserId))
                    {
                        playlist.UserPlaylists = new List<SmartPlaylistDto.UserPlaylistMapping>
                        {
                            new SmartPlaylistDto.UserPlaylistMapping
                            {
                                UserId = playlist.UserId,
                                JellyfinPlaylistId = null
                            }
                        };
                    }
                    else if (existingPlaylist.UserPlaylists != null && existingPlaylist.UserPlaylists.Count > 0)
                    {
                        // Preserve existing users if not provided
                        playlist.UserPlaylists = existingPlaylist.UserPlaylists.Select(m => new SmartPlaylistDto.UserPlaylistMapping
                        {
                            UserId = m.UserId,
                            JellyfinPlaylistId = m.JellyfinPlaylistId
                        }).ToList();
                    }
                }

                // Normalize and validate UserPlaylists
                if (!NormalizeAndValidateUserPlaylists(playlist, out var validationError))
                {
                    logger.LogWarning("UpdatePlaylist validation failed: {Error}. Name={Name}", validationError, playlist.Name);
                    return BadRequest(new ProblemDetails
                    {
                        Title = "Validation Error",
                        Detail = validationError,
                        Status = StatusCodes.Status400BadRequest
                    });
                }

                // Compare old and new user lists to detect changes
                var oldUserIds = GetPlaylistUserIds(existingPlaylist);
                var newUserIds = GetPlaylistUserIds(playlist);
                var usersToRemove = oldUserIds.Except(newUserIds, StringComparer.OrdinalIgnoreCase).ToList();
                var usersToAdd = newUserIds.Except(oldUserIds, StringComparer.OrdinalIgnoreCase).ToList();
                var usersToKeep = oldUserIds.Intersect(newUserIds, StringComparer.OrdinalIgnoreCase).ToList();

                logger.LogDebug("User changes detected for playlist '{PlaylistName}': Remove={RemoveCount}, Add={AddCount}, Keep={KeepCount}",
                    playlist.Name, usersToRemove.Count, usersToAdd.Count, usersToKeep.Count);

                // Delete Jellyfin playlists for removed users
                var playlistService = GetPlaylistService();
                foreach (var removedUserId in usersToRemove)
                {
                    // Normalize both sides for comparison
                    var normalizedRemovedUserId = Guid.TryParse(removedUserId, out var removedGuid) 
                        ? removedGuid.ToString("N") : removedUserId;
                    var removedMapping = existingPlaylist.UserPlaylists?.FirstOrDefault(m =>
                    {
                        var normalized = Guid.TryParse(m.UserId, out var guid) ? guid.ToString("N") : m.UserId;
                        return string.Equals(normalized, normalizedRemovedUserId, StringComparison.OrdinalIgnoreCase);
                    });
                    if (removedMapping != null && !string.IsNullOrEmpty(removedMapping.JellyfinPlaylistId))
                    {
                        logger.LogDebug("Deleting Jellyfin playlist {JellyfinPlaylistId} for removed user {UserId}", removedMapping.JellyfinPlaylistId, removedUserId);
                        try
                        {
                            // Create a temporary DTO for deletion
                            var tempDto = new SmartPlaylistDto
                            {
                                Id = existingPlaylist.Id,
                                Name = existingPlaylist.Name,
                                UserId = removedUserId,
                                JellyfinPlaylistId = removedMapping.JellyfinPlaylistId
                            };
                            await playlistService.DeleteAsync(tempDto);
                            logger.LogInformation("Deleted Jellyfin playlist {JellyfinPlaylistId} for removed user {UserId}", removedMapping.JellyfinPlaylistId, removedUserId);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to delete Jellyfin playlist {JellyfinPlaylistId} for removed user {UserId}, continuing", removedMapping.JellyfinPlaylistId, removedUserId);
                        }
                    }
                }

                // Preserve JellyfinPlaylistId for kept users
                if (existingPlaylist.UserPlaylists != null && playlist.UserPlaylists != null)
                {
                    foreach (var newMapping in playlist.UserPlaylists)
                    {
                        var existingMapping = existingPlaylist.UserPlaylists.FirstOrDefault(m =>
                        {
                            var normalizedExisting = Guid.TryParse(m.UserId, out var existingGuid) 
                                ? existingGuid.ToString("N") : m.UserId;
                            var normalizedNew = Guid.TryParse(newMapping.UserId, out var newGuid) 
                                ? newGuid.ToString("N") : newMapping.UserId;
                            return string.Equals(normalizedExisting, normalizedNew, StringComparison.OrdinalIgnoreCase);
                        });
                        if (existingMapping != null && !string.IsNullOrEmpty(existingMapping.JellyfinPlaylistId))
                        {
                            newMapping.JellyfinPlaylistId = existingMapping.JellyfinPlaylistId;
                        }
                    }
                }

                bool nameChanging = !string.Equals(existingPlaylist.Name, playlist.Name, StringComparison.OrdinalIgnoreCase);
                bool enabledStatusChanging = existingPlaylist.Enabled != playlist.Enabled;

                // Log enabled status changes
                if (enabledStatusChanging)
                {
                    logger.LogDebug("Playlist enabled status changing from {OldStatus} to {NewStatus} for playlist '{PlaylistName}'",
                        existingPlaylist.Enabled ? "enabled" : "disabled",
                        playlist.Enabled ? "enabled" : "disabled",
                        existingPlaylist.Name);
                    
                    // If disabling the playlist, delete all Jellyfin playlists
                    if (existingPlaylist.Enabled && !playlist.Enabled)
                    {
                        logger.LogInformation("Disabling playlist '{PlaylistName}', deleting Jellyfin playlists", existingPlaylist.Name);
                        try
                        {
                            await playlistService.DeleteAllJellyfinPlaylistsForUsersAsync(existingPlaylist);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to delete Jellyfin playlists when disabling playlist '{PlaylistName}', continuing", existingPlaylist.Name);
                        }
                        
                        // Always clear the Jellyfin playlist IDs regardless of deletion success
                        // This ensures consistency: disabled playlists should not have Jellyfin IDs
                        playlist.JellyfinPlaylistId = null;
                        if (playlist.UserPlaylists != null)
                        {
                            foreach (var userMapping in playlist.UserPlaylists)
                            {
                                userMapping.JellyfinPlaylistId = null;
                            }
                        }
                    }
                }

                if (nameChanging)
                {
                    logger.LogDebug("Playlist name changing from '{OldName}' to '{NewName}'",
                        existingPlaylist.Name, playlist.Name);

                    // Note: Name changes will be handled by the PlaylistService during refresh
                    // The playlist will be updated in place rather than recreated
                }

                // Ensure backwards compatibility: keep UserId and JellyfinPlaylistId populated (first user's values)
                // DEPRECATED: This is for backwards compatibility with old single-user playlists.
                // It is planned to be removed in version 10.12. Use UserPlaylists array instead.
                if (playlist.UserPlaylists != null && playlist.UserPlaylists.Count > 0)
                {
                    var firstUser = playlist.UserPlaylists[0];
                    playlist.UserId = firstUser.UserId;
                    playlist.JellyfinPlaylistId = firstUser.JellyfinPlaylistId;
                }

                playlist.Id = id;

                // Preserve original creation timestamp
                if (existingPlaylist.DateCreated.HasValue)
                {
                    playlist.DateCreated = existingPlaylist.DateCreated;
                }

                // Preserve original creator information
                if (string.IsNullOrEmpty(playlist.CreatedByUserId) && !string.IsNullOrEmpty(existingPlaylist.CreatedByUserId))
                {
                    playlist.CreatedByUserId = existingPlaylist.CreatedByUserId;
                }

                // JellyfinPlaylistId is already set above from first user's mapping

                // Preserve statistics from existing playlist to avoid N/A display until refresh completes
                if (existingPlaylist.ItemCount.HasValue)
                {
                    playlist.ItemCount = existingPlaylist.ItemCount;
                }
                if (existingPlaylist.TotalRuntimeMinutes.HasValue)
                {
                    playlist.TotalRuntimeMinutes = existingPlaylist.TotalRuntimeMinutes;
                }
                if (existingPlaylist.LastRefreshed.HasValue)
                {
                    playlist.LastRefreshed = existingPlaylist.LastRefreshed;
                }

                // Preserve existing CustomImages from the stored playlist
                // (images are managed separately via upload/delete endpoints)
                if (existingPlaylist.CustomImages != null && existingPlaylist.CustomImages.Count > 0)
                {
                    playlist.CustomImages = new Dictionary<string, string>(existingPlaylist.CustomImages);
                }

                var updatedPlaylist = await playlistStore.SaveAsync(playlist);

                // Update the auto-refresh cache with the updated playlist
                AutoRefreshService.Instance?.UpdatePlaylistInCache(updatedPlaylist);

                // Clear the rule cache to ensure any rule changes are properly reflected
                SmartList.ClearRuleCache(logger);
                logger.LogDebug("Cleared rule cache after updating playlist '{PlaylistName}'", playlist.Name);

                // Enqueue refresh operation(s) only if playlist is enabled and skipRefresh is false
                if (updatedPlaylist.Enabled && !skipRefresh)
                {
                    logger.LogDebug("Enqueuing refresh for updated playlist {PlaylistName}", playlist.Name);
                    var listId = updatedPlaylist.Id ?? Guid.NewGuid().ToString();

                    // Enqueue a single refresh operation for the list
                    // Note: We enqueue a single item with deprecated UserId field, but the
                    // queue consumer (RefreshQueueService.ProcessPlaylistRefreshAsync) ignores
                    // this field and instead processes all users from ListData.UserPlaylists.
                    // The first user's ID is used for backwards compatibility with legacy single-user playlists.
                    var queueItem = new RefreshQueueItem
                    {
                        ListId = listId,
                        ListName = updatedPlaylist.Name,
                        ListType = Core.Enums.SmartListType.Playlist,
                        OperationType = RefreshOperationType.Edit,
                        ListData = updatedPlaylist,
                        // DEPRECATED: UserId is for backwards compatibility - ignored by queue consumer for multi-user playlists
                        UserId = updatedPlaylist.UserPlaylists?.FirstOrDefault()?.UserId ?? updatedPlaylist.UserId,
                        TriggerType = Core.Enums.RefreshTriggerType.Manual
                    };

                    _refreshQueueService.EnqueueOperation(queueItem);

                    logger.LogInformation("Updated SmartList: {PlaylistName} and enqueued for refresh in {ElapsedTime}ms", playlist.Name, stopwatch.ElapsedMilliseconds);
                }
                else if (skipRefresh)
                {
                    logger.LogInformation("Updated SmartList: {PlaylistName} (refresh deferred for image operations) in {ElapsedTime}ms", playlist.Name, stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    logger.LogInformation("Updated disabled SmartList: {PlaylistName} (not enqueued for refresh) in {ElapsedTime}ms", playlist.Name, stopwatch.ElapsedMilliseconds);
                }

                stopwatch.Stop();

                return Ok(updatedPlaylist);
            }
            catch (ArgumentException ex) when (ex.Message.Contains("Invalid regex pattern"))
            {
                stopwatch.Stop();
                logger.LogError(ex, "Unexpected regex validation error in smart playlist update after {ElapsedTime}ms", stopwatch.ElapsedMilliseconds);
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                logger.LogError(ex, "Error updating smart playlist {PlaylistId} after {ElapsedTime}ms", id, stopwatch.ElapsedMilliseconds);
                return StatusCode(StatusCodes.Status500InternalServerError, "Error updating smart playlist");
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3012:Review code for regex injection vulnerabilities", Justification = "Regex patterns are validated with IsValidRegexPattern method including length limits and timeout")]
        private async Task<ActionResult<SmartListDto>> UpdateCollectionInternal(string id, Guid guidId, SmartCollectionDto collection, bool skipRefresh = false)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var collectionStore = GetCollectionStore();
                var existingCollection = await collectionStore.GetByIdAsync(guidId);
                if (existingCollection == null)
                {
                    return NotFound("Smart collection not found");
                }

                // Set default owner user if not specified, or normalize if already set (same as CreateCollectionInternal)
                if (string.IsNullOrEmpty(collection.UserId) || !Guid.TryParse(collection.UserId, out var userId) || userId == Guid.Empty)
                {
                    // Default to currently logged-in user
                    var currentUserId = GetCurrentUserId();
                    var currentUser = ValidateAndGetCurrentUser(currentUserId, out var errorResult);
                    if (currentUser == null)
                    {
                        return errorResult!;
                    }

                    collection.UserId = currentUser.Id.ToString("N").ToLowerInvariant();
                    logger.LogDebug("Set default collection owner to currently logged-in user during update: {Username} ({UserId})", currentUser.Username, currentUser.Id);
                }
                else
                {
                    // Normalize existing UserId to canonical "N" format (no dashes)
                    collection.UserId = NormalizeUserId(collection.UserId);
                    logger.LogDebug("Normalized collection UserId to canonical format during update: {UserId}", collection.UserId);
                }

                // Check for duplicate collection names (Jellyfin doesn't allow collections with the same name)
                // Only check if the name is changing
                bool nameChanging = !string.Equals(existingCollection.Name, collection.Name, StringComparison.OrdinalIgnoreCase);
                if (nameChanging)
                {
                    var formattedName = NameFormatter.FormatPlaylistName(collection.Name);
                    var allCollections = await collectionStore.GetAllAsync();
                    var duplicateCollection = allCollections.FirstOrDefault(c => 
                        c.Id != guidId.ToString() && 
                        string.Equals(NameFormatter.FormatPlaylistName(c.Name), formattedName, StringComparison.OrdinalIgnoreCase));
                    
                    if (duplicateCollection != null)
                    {
                        logger.LogWarning("Cannot update collection '{OldName}' to '{NewName}' - a collection with this name already exists", 
                            existingCollection.Name, collection.Name);
                        return BadRequest(new ProblemDetails
                        {
                            Title = "Validation Error",
                            Detail = $"A collection named '{formattedName}' already exists. Jellyfin does not allow multiple collections with the same name.",
                            Status = StatusCodes.Status400BadRequest
                        });
                    }
                }

                // Validate regex patterns before saving
                if (collection.ExpressionSets != null)
                {
                    foreach (var expressionSet in collection.ExpressionSets)
                    {
                        if (expressionSet.Expressions != null)
                        {
                            foreach (var expression in expressionSet.Expressions)
                            {
                                if (expression.Operator == "MatchRegex" && !string.IsNullOrEmpty(expression.TargetValue))
                                {
                                    if (!IsValidRegexPattern(expression.TargetValue, out var validationError))
                                    {
                                        return BadRequest($"Invalid regex pattern: {validationError}");
                                    }

                                    try
                                    {
                                        var regex = new Regex(expression.TargetValue, RegexOptions.None, TimeSpan.FromMilliseconds(100));
                                    }
                                    catch (ArgumentException ex)
                                    {
                                        logger.LogError(ex, "Invalid regex pattern '{Pattern}' during validation", expression.TargetValue);
                                        return BadRequest($"Invalid regex pattern '{expression.TargetValue}': {ex.Message}");
                                    }
                                    catch (RegexMatchTimeoutException ex)
                                    {
                                        logger.LogError(ex, "Regex pattern '{Pattern}' timed out during validation", expression.TargetValue);
                                        return BadRequest($"Regex pattern '{expression.TargetValue}' is too complex or caused a timeout");
                                    }
                                }
                            }
                        }
                    }
                }

                collection.Id = id;

                // Preserve original creation timestamp
                if (existingCollection.DateCreated.HasValue)
                {
                    collection.DateCreated = existingCollection.DateCreated;
                }

                // Preserve original creator information
                if (string.IsNullOrEmpty(collection.CreatedByUserId) && !string.IsNullOrEmpty(existingCollection.CreatedByUserId))
                {
                    collection.CreatedByUserId = existingCollection.CreatedByUserId;
                }

                // Preserve the Jellyfin collection ID from the existing collection if it exists
                if (!string.IsNullOrEmpty(existingCollection.JellyfinCollectionId))
                {
                    collection.JellyfinCollectionId = existingCollection.JellyfinCollectionId;
                    logger.LogDebug("Preserved Jellyfin collection ID {JellyfinCollectionId} from existing collection", existingCollection.JellyfinCollectionId);
                }

                // Check if enabled status is changing
                bool enabledStatusChanging = existingCollection.Enabled != collection.Enabled;
                if (enabledStatusChanging)
                {
                    logger.LogDebug("Collection enabled status changing from {OldStatus} to {NewStatus} for collection '{CollectionName}'",
                        existingCollection.Enabled ? "enabled" : "disabled",
                        collection.Enabled ? "enabled" : "disabled",
                        existingCollection.Name);
                    
                    // If disabling the collection, delete the Jellyfin collection
                    if (existingCollection.Enabled && !collection.Enabled)
                    {
                        logger.LogInformation("Disabling collection '{CollectionName}', deleting Jellyfin collection", existingCollection.Name);
                        try
                        {
                            var collectionService = GetCollectionService();
                            await collectionService.DeleteAsync(existingCollection);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to delete Jellyfin collection when disabling collection '{CollectionName}', continuing", existingCollection.Name);
                        }
                        
                        // Always clear the Jellyfin collection ID regardless of deletion success
                        // This ensures consistency: disabled collections should not have Jellyfin IDs
                        collection.JellyfinCollectionId = null;
                    }
                }

                // Preserve statistics from existing collection to avoid N/A display until refresh completes
                if (existingCollection.ItemCount.HasValue)
                {
                    collection.ItemCount = existingCollection.ItemCount;
                }
                if (existingCollection.TotalRuntimeMinutes.HasValue)
                {
                    collection.TotalRuntimeMinutes = existingCollection.TotalRuntimeMinutes;
                }
                if (existingCollection.LastRefreshed.HasValue)
                {
                    collection.LastRefreshed = existingCollection.LastRefreshed;
                }

                // Preserve existing CustomImages from the stored collection
                // (images are managed separately via upload/delete endpoints)
                if (existingCollection.CustomImages != null && existingCollection.CustomImages.Count > 0)
                {
                    collection.CustomImages = new Dictionary<string, string>(existingCollection.CustomImages);
                }

                var updatedCollection = await collectionStore.SaveAsync(collection);

                // Update the auto-refresh cache with the updated collection
                AutoRefreshService.Instance?.UpdateCollectionInCache(updatedCollection);

                // Clear the rule cache to ensure any rule changes are properly reflected
                SmartList.ClearRuleCache(logger);
                logger.LogDebug("Cleared rule cache after updating collection '{CollectionName}'", collection.Name);

                // Enqueue refresh operation only if collection is enabled and skipRefresh is false
                if (updatedCollection.Enabled && !skipRefresh)
                {
                    logger.LogDebug("Enqueuing refresh for updated collection {CollectionName}", collection.Name);
                    var listId = updatedCollection.Id ?? Guid.NewGuid().ToString();
                    var queueItem = new RefreshQueueItem
                    {
                        ListId = listId,
                        ListName = updatedCollection.Name,
                        ListType = Core.Enums.SmartListType.Collection,
                        OperationType = RefreshOperationType.Edit,
                        ListData = updatedCollection,
                        UserId = updatedCollection.UserId,
                        TriggerType = Core.Enums.RefreshTriggerType.Manual
                    };

                    _refreshQueueService.EnqueueOperation(queueItem);
                    logger.LogInformation("Updated SmartList: {CollectionName} and enqueued for refresh in {ElapsedTime}ms", collection.Name, stopwatch.ElapsedMilliseconds);
                }
                else if (skipRefresh)
                {
                    logger.LogInformation("Updated SmartList: {CollectionName} (refresh deferred for image operations) in {ElapsedTime}ms", collection.Name, stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    logger.LogInformation("Updated disabled SmartList: {CollectionName} (not enqueued for refresh) in {ElapsedTime}ms", collection.Name, stopwatch.ElapsedMilliseconds);
                }

                stopwatch.Stop();

                return Ok(updatedCollection);
            }
            catch (ArgumentException ex) when (ex.Message.Contains("Invalid regex pattern"))
            {
                stopwatch.Stop();
                logger.LogError(ex, "Unexpected regex validation error in smart collection update after {ElapsedTime}ms", stopwatch.ElapsedMilliseconds);
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                logger.LogError(ex, "Error updating smart collection {CollectionId} after {ElapsedTime}ms", id, stopwatch.ElapsedMilliseconds);
                return StatusCode(StatusCodes.Status500InternalServerError, "Error updating smart collection");
            }
        }

        /// <summary>
        /// Delete a smart list (playlist or collection).
        /// </summary>
        /// <param name="id">The list ID.</param>
        /// <param name="deleteJellyfinList">Whether to also delete the corresponding Jellyfin playlist/collection. Defaults to true for backward compatibility.</param>
        /// <returns>No content.</returns>
        [HttpDelete("{id}")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "ID is validated as GUID before use, preventing path injection")]
        public async Task<ActionResult> DeleteSmartList([FromRoute, Required] string id, [FromQuery] bool deleteJellyfinList = true)
        {
            try
            {
                if (!Guid.TryParse(id, out var guidId))
                {
                    return BadRequest("Invalid list ID format");
                }

                // Normalize ID to dashed format for consistent image folder operations
                var normalizedId = guidId.ToString("D");

                // Try playlist first
                var playlistStore = GetPlaylistStore();
                var playlist = await playlistStore.GetByIdAsync(guidId);
                if (playlist != null)
                {
                    if (deleteJellyfinList)
                    {
                        var playlistService = GetPlaylistService();
                        await playlistService.DeleteAllJellyfinPlaylistsForUsersAsync(playlist);
                        logger.LogInformation("Deleted smart playlist: {PlaylistName}", playlist.Name);
                    }
                    else
                    {
                        await RemoveSmartSuffixFromAllPlaylistsAsync(playlist);
                        logger.LogInformation("Deleted smart playlist configuration: {PlaylistName}", playlist.Name);
                    }

                    // Delete custom images using normalized ID
                    await _imageService.DeleteAllImagesAsync(normalizedId).ConfigureAwait(false);

                    await playlistStore.DeleteAsync(guidId).ConfigureAwait(false);
                    AutoRefreshService.Instance?.RemovePlaylistFromCache(normalizedId);
                    return NoContent();
                }

                // Try collection
                var collectionStore = GetCollectionStore();
                var collection = await collectionStore.GetByIdAsync(guidId);
                if (collection != null)
                {
                    var collectionService = GetCollectionService();
                    if (deleteJellyfinList)
                    {
                        await collectionService.DeleteAsync(collection);
                        logger.LogInformation("Deleted smart collection: {CollectionName}", collection.Name);
                    }
                    else
                    {
                        await collectionService.RemoveSmartSuffixAsync(collection);
                        logger.LogInformation("Deleted smart collection configuration: {CollectionName}", collection.Name);
                    }

                    // Delete custom images using normalized ID
                    await _imageService.DeleteAllImagesAsync(normalizedId).ConfigureAwait(false);

                    await collectionStore.DeleteAsync(guidId).ConfigureAwait(false);
                    AutoRefreshService.Instance?.RemoveCollectionFromCache(normalizedId);
                    return NoContent();
                }

                return NotFound("Smart list not found");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error deleting smart list {ListId}", id);
                return StatusCode(StatusCodes.Status500InternalServerError, "Error deleting smart list");
            }
        }

        /// <summary>
        /// Get available field options for smart playlist rules.
        /// </summary>
        /// <returns>Available field options.</returns>
        [HttpGet("fields")]
        public ActionResult<object> GetAvailableFields()
        {
            // Use the shared field definitions (DRY principle)
            return Ok(SharedFieldDefinitions.GetAvailableFields());
        }

        /// <summary>
        /// Static readonly field operators dictionary for performance optimization.
        /// </summary>
        private static readonly Dictionary<string, string[]> _fieldOperators = Core.Constants.Operators.GetFieldOperatorsDictionary();

        /// <summary>
        /// Gets the field operators dictionary using centralized constants.
        /// </summary>
        /// <returns>Dictionary mapping field names to their allowed operators</returns>
        private static Dictionary<string, string[]> GetFieldOperators()
        {
            return _fieldOperators;
        }

        /// <summary>
        /// Get all users for the user selection dropdown.
        /// </summary>
        /// <returns>List of users.</returns>
        [HttpGet("users")]
        public ActionResult<object> GetUsers()
        {
            try
            {
                var users = _userManager.Users
                    .Select(u => new
                    {
                        u.Id,
                        Name = u.Username,
                    })
                    .OrderBy(u => u.Name)
                    .ToList();

                return Ok(users);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error retrieving users");
                return StatusCode(StatusCodes.Status500InternalServerError, "Error retrieving users");
            }
        }

        /// <summary>
        /// Get the current user's information.
        /// </summary>
        /// <returns>Current user info.</returns>
        [HttpGet("currentuser")]
        public ActionResult<object> GetCurrentUser()
        {
            try
            {
                // Use the improved helper method to get current user ID
                var userId = GetCurrentUserId();

                if (userId == Guid.Empty)
                {
                    return BadRequest("Unable to determine current user");
                }

                var user = _userManager.GetUserById(userId);
                if (user == null)
                {
                    return NotFound("Current user not found");
                }

                return Ok(new
                {
                    user.Id,
                    Name = user.Username,
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error getting current user");
                return StatusCode(StatusCodes.Status500InternalServerError, "Error getting current user");
            }
        }

        /// <summary>
        /// Get all libraries for collection assignment.
        /// </summary>
        /// <returns>List of libraries.</returns>
        [HttpGet("libraries")]
        public ActionResult<object> GetLibraries()
        {
            try
            {
                // Get virtual folders (libraries) from library manager
                var virtualFolders = _libraryManager.GetVirtualFolders();
                
                var libraries = virtualFolders
                    .Select(vf => new
                    {
                        Id = vf.ItemId.ToString(),
                        Name = vf.Name,
                        CollectionType = vf.CollectionType
                    })
                    .OrderBy(l => l.Name)
                    .ToList();

                return Ok(libraries);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error retrieving libraries");
                return StatusCode(StatusCodes.Status500InternalServerError, "Error retrieving libraries");
            }
        }

        /// <summary>
        /// Enable a smart list (playlist or collection).
        /// </summary>
        /// <param name="id">The list ID.</param>
        /// <returns>Success message.</returns>
        [HttpPost("{id}/enable")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "ID is validated as GUID before use, preventing path injection")]
        public async Task<ActionResult> EnableSmartList([FromRoute, Required] string id)
        {
            try
            {
                if (!Guid.TryParse(id, out var guidId))
                {
                    return BadRequest("Invalid list ID format");
                }

                // Try playlist first
                var playlistStore = GetPlaylistStore();
                var playlist = await playlistStore.GetByIdAsync(guidId);
                if (playlist != null)
                {
                    // Temporarily set enabled state for the Jellyfin operation
                    var originalEnabledState = playlist.Enabled;
                    playlist.Enabled = true;

                    try
                    {
                        // Save the configuration first (before enqueuing)
                        await playlistStore.SaveAsync(playlist);

                        // Update the auto-refresh cache with the enabled playlist
                        AutoRefreshService.Instance?.UpdatePlaylistInCache(playlist);

                        // Enqueue refresh operation after successful save
                        // Note: We enqueue a single item with deprecated UserId field, but the
                        // queue consumer (RefreshQueueService.ProcessPlaylistRefreshAsync) ignores
                        // this field and instead processes all users from ListData.UserPlaylists.
                        // This works correctly but is inconsistent with create/update which enqueue
                        // one item per user. Consider refactoring in future for consistency.
                        try
                        {
                            var listId = playlist.Id ?? Guid.NewGuid().ToString();
                            var queueItem = new RefreshQueueItem
                            {
                                ListId = listId,
                                ListName = playlist.Name,
                                ListType = Core.Enums.SmartListType.Playlist,
                                OperationType = RefreshOperationType.Refresh,
                                ListData = playlist,
                                // DEPRECATED: playlist.UserId is for backwards compatibility with old single-user playlists.
                                // It is planned to be removed in version 10.12. Use UserPlaylists array instead.
                                UserId = playlist.UserId, // DEPRECATED - ignored by queue consumer
                                TriggerType = Core.Enums.RefreshTriggerType.Manual
                            };

                            _refreshQueueService.EnqueueOperation(queueItem);
                        }
                        catch (Exception enqueueEx)
                        {
                            // Log but don't fail the enable - the playlist is already saved and enabled
                            logger.LogWarning(enqueueEx, "Failed to enqueue refresh for enabled playlist '{Name}', but enable succeeded", playlist.Name);
                        }

                        logger.LogInformation("Enabled smart playlist: {PlaylistId} - {PlaylistName}", id, playlist.Name);
                        return Ok(new { message = $"Smart playlist '{playlist.Name}' has been enabled" });
                    }
                    catch (Exception jellyfinEx)
                    {
                        playlist.Enabled = originalEnabledState;
                        logger.LogError(jellyfinEx, "Failed to enable Jellyfin playlist for {PlaylistId} - {PlaylistName}", id, playlist.Name);
                        throw;
                    }
                }

                // Try collection
                var collectionStore = GetCollectionStore();
                var collection = await collectionStore.GetByIdAsync(guidId);
                if (collection != null)
                {
                    var originalEnabledState = collection.Enabled;
                    collection.Enabled = true;

                    try
                    {
                        // Save the configuration first (before enqueuing)
                        await collectionStore.SaveAsync(collection);
                        AutoRefreshService.Instance?.UpdateCollectionInCache(collection);

                        // Enqueue refresh operation after successful save
                        try
                        {
                            var listId = collection.Id ?? Guid.NewGuid().ToString();
                            var queueItem = new RefreshQueueItem
                            {
                                ListId = listId,
                                ListName = collection.Name,
                                ListType = Core.Enums.SmartListType.Collection,
                                OperationType = RefreshOperationType.Refresh,
                                ListData = collection,
                                UserId = collection.UserId,
                                TriggerType = Core.Enums.RefreshTriggerType.Manual
                            };

                            _refreshQueueService.EnqueueOperation(queueItem);
                        }
                        catch (Exception enqueueEx)
                        {
                            // Log but don't fail the enable - the collection is already saved and enabled
                            logger.LogWarning(enqueueEx, "Failed to enqueue refresh for enabled collection '{Name}', but enable succeeded", collection.Name);
                        }

                        logger.LogInformation("Enabled smart collection: {CollectionId} - {CollectionName}", id, collection.Name);
                        return Ok(new { message = $"Smart collection '{collection.Name}' has been enabled" });
                    }
                    catch (Exception jellyfinEx)
                    {
                        collection.Enabled = originalEnabledState;
                        logger.LogError(jellyfinEx, "Failed to enable Jellyfin collection for {CollectionId} - {CollectionName}", id, collection.Name);
                        throw;
                    }
                }

                return NotFound("Smart list not found");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error enabling smart list {ListId}", id);
                return StatusCode(StatusCodes.Status500InternalServerError, "Error enabling smart list");
            }
        }

        /// <summary>
        /// Disable a smart list (playlist or collection).
        /// </summary>
        /// <param name="id">The list ID.</param>
        /// <returns>Success message.</returns>
        [HttpPost("{id}/disable")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "ID is validated as GUID before use, preventing path injection")]
        public async Task<ActionResult> DisableSmartList([FromRoute, Required] string id)
        {
            try
            {
                if (!Guid.TryParse(id, out var guidId))
                {
                    return BadRequest("Invalid list ID format");
                }

                // Try playlist first
                var playlistStore = GetPlaylistStore();
                var playlist = await playlistStore.GetByIdAsync(guidId);
                if (playlist != null)
                {
                    // Temporarily set disabled state for the Jellyfin operation
                    var originalEnabledState = playlist.Enabled;
                    playlist.Enabled = false;

                    try
                    {
                        // Remove all Jellyfin playlists FIRST using service method
                        var playlistService = GetPlaylistService();
                        await playlistService.DeleteAllJellyfinPlaylistsForUsersAsync(playlist);

                        // Clear the Jellyfin playlist IDs since the playlists no longer exist
                        playlist.JellyfinPlaylistId = null;
                        if (playlist.UserPlaylists != null)
                        {
                            foreach (var userMapping in playlist.UserPlaylists)
                            {
                                userMapping.JellyfinPlaylistId = null;
                            }
                        }

                        // Only save the configuration if the Jellyfin operation succeeds
                        await playlistStore.SaveAsync(playlist);

                        // Update the auto-refresh cache with the disabled playlist
                        AutoRefreshService.Instance?.UpdatePlaylistInCache(playlist);

                        logger.LogInformation("Disabled smart playlist: {PlaylistId} - {PlaylistName}", id, playlist.Name);
                        return Ok(new { message = $"Smart playlist '{playlist.Name}' has been disabled" });
                    }
                    catch (Exception jellyfinEx)
                    {
                        playlist.Enabled = originalEnabledState;
                        logger.LogError(jellyfinEx, "Failed to disable Jellyfin playlist for {PlaylistId} - {PlaylistName}", id, playlist.Name);
                        throw;
                    }
                }

                // Try collection
                var collectionStore = GetCollectionStore();
                var collection = await collectionStore.GetByIdAsync(guidId);
                if (collection != null)
                {
                    var originalEnabledState = collection.Enabled;
                    collection.Enabled = false;

                    try
                    {
                        var collectionService = GetCollectionService();
                        await collectionService.DisableAsync(collection);

                        collection.JellyfinCollectionId = null;
                        await collectionStore.SaveAsync(collection);
                        AutoRefreshService.Instance?.UpdateCollectionInCache(collection);

                        logger.LogInformation("Disabled smart collection: {CollectionId} - {CollectionName}", id, collection.Name);
                        return Ok(new { message = $"Smart collection '{collection.Name}' has been disabled" });
                    }
                    catch (Exception jellyfinEx)
                    {
                        collection.Enabled = originalEnabledState;
                        logger.LogError(jellyfinEx, "Failed to disable Jellyfin collection for {CollectionId} - {CollectionName}", id, collection.Name);
                        throw;
                    }
                }

                return NotFound("Smart list not found");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error disabling smart list {ListId}", id);
                return StatusCode(StatusCodes.Status500InternalServerError, "Error disabling smart list");
            }
        }

        /// <summary>
        /// Trigger a refresh of a specific smart list (playlist or collection).
        /// </summary>
        /// <param name="id">The list ID.</param>
        /// <returns>Success message.</returns>
        [HttpPost("{id}/refresh")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "ID is validated as GUID before use, preventing path injection")]
        public async Task<ActionResult> TriggerSingleListRefresh([FromRoute, Required] string id)
        {
            string? listName = null;
            Core.Enums.SmartListType? listType = null;
            
            try
            {
                if (!Guid.TryParse(id, out var guidId))
                {
                    // Track error in status service
                    _refreshStatusService?.StartOperation(
                        id,
                        $"List ({id})",
                        Core.Enums.SmartListType.Playlist,
                        Core.Enums.RefreshTriggerType.Manual,
                        0);
                    var duration = _refreshStatusService?.GetElapsedTime(id) ?? TimeSpan.Zero;
                    _refreshStatusService?.CompleteOperation(id, false, duration, "Invalid list ID format");
                    
                    return BadRequest("Invalid list ID format");
                }

                // Try playlist first
                var playlistStore = GetPlaylistStore();
                var playlist = await playlistStore.GetByIdAsync(guidId);
                if (playlist != null)
                {
                    listName = playlist.Name;
                    listType = Core.Enums.SmartListType.Playlist;
                    
                    // Validate that the playlist is enabled before allowing refresh
                    if (playlist.Enabled == false)
                    {
                        // Start operation to properly track this failed attempt
                        _refreshStatusService?.StartOperation(
                            id,
                            playlist.Name,
                            Core.Enums.SmartListType.Playlist,
                            Core.Enums.RefreshTriggerType.Manual,
                            0);
                        
                        var duration = _refreshStatusService?.GetElapsedTime(id) ?? TimeSpan.Zero;
                        _refreshStatusService?.CompleteOperation(id, false, duration, "Cannot refresh disabled list");
                        
                        return BadRequest(new { message = $"Cannot refresh disabled playlist '{playlist.Name}'. Please enable the playlist first." });
                    }
                    
                    var (success, message, jellyfinPlaylistId) = await _manualRefreshService.RefreshSinglePlaylistAsync(playlist);

                    if (success)
                    {
                        if (!string.IsNullOrEmpty(jellyfinPlaylistId))
                        {
                            playlist.JellyfinPlaylistId = jellyfinPlaylistId;
                        }

                        await playlistStore.SaveAsync(playlist);
                        return Ok(new { message });
                    }
                    else
                    {
                        return BadRequest(new { message });
                    }
                }

                // Try collection
                var collectionStore = GetCollectionStore();
                var collection = await collectionStore.GetByIdAsync(guidId);
                if (collection != null)
                {
                    listName = collection.Name;
                    listType = Core.Enums.SmartListType.Collection;
                    
                    // Validate that the collection is enabled before allowing refresh
                    if (collection.Enabled == false)
                    {
                        // Start operation to properly track this failed attempt
                        _refreshStatusService?.StartOperation(
                            id,
                            collection.Name,
                            Core.Enums.SmartListType.Collection,
                            Core.Enums.RefreshTriggerType.Manual,
                            0);
                        
                        var duration = _refreshStatusService?.GetElapsedTime(id) ?? TimeSpan.Zero;
                        _refreshStatusService?.CompleteOperation(id, false, duration, "Cannot refresh disabled list");
                        
                        return BadRequest(new { message = $"Cannot refresh disabled collection '{collection.Name}'. Please enable the collection first." });
                    }
                    
                    var (success, message, jellyfinCollectionId) = await _manualRefreshService.RefreshSingleCollectionAsync(collection);

                    if (success)
                    {
                        if (!string.IsNullOrEmpty(jellyfinCollectionId))
                        {
                            collection.JellyfinCollectionId = jellyfinCollectionId;
                        }

                        await collectionStore.SaveAsync(collection);
                        return Ok(new { message });
                    }
                    else
                    {
                        return BadRequest(new { message });
                    }
                }

                // List not found - track error in status service
                _refreshStatusService?.StartOperation(
                    id,
                    $"List ({id})",
                    Core.Enums.SmartListType.Playlist,
                    Core.Enums.RefreshTriggerType.Manual,
                    0);
                var notFoundDuration = _refreshStatusService?.GetElapsedTime(id) ?? TimeSpan.Zero;
                _refreshStatusService?.CompleteOperation(id, false, notFoundDuration, "Smart list not found");
                
                return NotFound("Smart list not found");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error refreshing single smart list {ListId}", id);
                
                // Track error in status service if not already tracked
                if (!_refreshStatusService?.HasOngoingOperation(id) ?? true)
                {
                    _refreshStatusService?.StartOperation(
                        id,
                        listName ?? $"List ({id})",
                        listType ?? Core.Enums.SmartListType.Playlist,
                        Core.Enums.RefreshTriggerType.Manual,
                        0);
                }
                var errorDuration = _refreshStatusService?.GetElapsedTime(id) ?? TimeSpan.Zero;
                _refreshStatusService?.CompleteOperation(id, false, errorDuration, $"Error refreshing smart list: {ex.Message}");
                
                return StatusCode(StatusCodes.Status500InternalServerError, "Error refreshing smart list");
            }
        }

        /// <summary>
        /// Trigger a refresh of all smart playlists.
        /// </summary>
        /// <returns>Success message.</returns>
        [HttpPost("refresh")]
        public async Task<ActionResult> TriggerRefresh()
        {
            try
            {
                // Use ManualRefreshService to refresh all playlists directly
                var result = await _manualRefreshService.RefreshAllPlaylistsAsync();

                if (result.Success)
                {
                    return Ok(new { message = result.NotificationMessage });
                }
                else
                {
                    // Map "already in progress" to HTTP 409 Conflict for better API semantics
                    if (result.NotificationMessage.Contains("already in progress"))
                    {
                        return Conflict(new { message = result.NotificationMessage });
                    }
                    return StatusCode(StatusCodes.Status500InternalServerError, result.NotificationMessage);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error triggering smart playlist refresh");
                return StatusCode(StatusCodes.Status500InternalServerError, "Error triggering smart playlist refresh");
            }
        }

        /// <summary>
        /// Directly refresh all smart lists (both playlists and collections).
        /// This method processes all enabled lists sequentially for each user.
        /// </summary>
        /// <returns>Success message.</returns>
        [HttpPost("refresh-direct")]
        public async Task<ActionResult> RefreshAllPlaylistsDirect()
        {
            try
            {
                // The ManualRefreshService now handles lock acquisition internally for the entire operation
                // This now refreshes both playlists and collections
                var result = await _manualRefreshService.RefreshAllListsAsync();

                if (result.Success)
                {
                    return Ok(new { message = result.NotificationMessage });
                }
                else
                {
                    // Map "already in progress" to HTTP 409 Conflict for better API semantics
                    if (result.NotificationMessage.Contains("already in progress"))
                    {
                        return Conflict(new { message = result.NotificationMessage });
                    }
                    return StatusCode(StatusCodes.Status500InternalServerError, result.NotificationMessage);
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Manual list refresh was cancelled by client");
                return StatusCode(499, "Refresh operation was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during manual list refresh");
                return StatusCode(StatusCodes.Status500InternalServerError, "Error during manual list refresh");
            }
        }


        /// <summary>
        /// Export all smart lists as a ZIP file.
        /// Includes config.json and any custom images for each list.
        /// </summary>
        /// <returns>ZIP file containing all smart list folders with config and images.</returns>
        [HttpPost("export")]
        public async Task<ActionResult> ExportPlaylists()
        {
            try
            {
                var fileSystem = new SmartListFileSystem(_applicationPaths);
                var (playlists, collections) = await fileSystem.GetAllSmartListsAsync();

                var allLists = playlists.Cast<SmartListDto>().Concat(collections.Cast<SmartListDto>()).ToList();

                if (allLists.Count == 0)
                {
                    return BadRequest(new { message = "No smart lists found to export" });
                }

                using var zipStream = new MemoryStream();
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
                {
                    foreach (var list in allLists)
                    {
                        if (string.IsNullOrEmpty(list.Id))
                        {
                            continue;
                        }

                        var folderPath = fileSystem.GetSmartListFolderPath(list.Id);

                        // Add config.json
                        var configPath = fileSystem.GetSmartListConfigPath(list.Id);
                        if (System.IO.File.Exists(configPath))
                        {
                            var entryName = $"{list.Id}/config.json";
                            var entry = archive.CreateEntry(entryName);
                            using var entryStream = entry.Open();
                            using var fileStream = System.IO.File.OpenRead(configPath);
                            await fileStream.CopyToAsync(entryStream);
                        }

                        // Add any image files from the folder
                        if (Directory.Exists(folderPath))
                        {
                            foreach (var imagePath in Directory.GetFiles(folderPath))
                            {
                                var fileName = Path.GetFileName(imagePath);

                                // Skip config.json (already added) and temp files
                                if (fileName.Equals("config.json", StringComparison.OrdinalIgnoreCase) ||
                                    fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                // Add image file
                                var imageEntryName = $"{list.Id}/{fileName}";
                                var imageEntry = archive.CreateEntry(imageEntryName);
                                using var imageEntryStream = imageEntry.Open();
                                using var imageFileStream = System.IO.File.OpenRead(imagePath);
                                await imageFileStream.CopyToAsync(imageEntryStream);
                            }
                        }
                    }
                }

                zipStream.Position = 0;
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                var zipFileName = $"smartlists_export_{timestamp}.zip";

                logger.LogInformation("Exported {ListCount} smart lists to {FileName}", allLists.Count, zipFileName);

                return File(zipStream.ToArray(), "application/zip", zipFileName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error exporting smart lists");
                return StatusCode(StatusCodes.Status500InternalServerError, "Error exporting smart lists");
            }
        }

        /// <summary>
        /// Import smart lists (playlists and collections) from a ZIP file.
        /// Supports both old format (flat JSON files) and new format (folders with config.json and images).
        /// </summary>
        /// <param name="file">ZIP file containing smart list JSON files or folders.</param>
        /// <returns>Import results with counts of imported and skipped lists.</returns>
        [HttpPost("import")]
        public async Task<ActionResult> ImportPlaylists([FromForm] IFormFile file)
        {
            try
            {
                if (file == null || file.Length == 0)
                {
                    return BadRequest(new { message = "No file uploaded" });
                }

                if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { message = "File must be a ZIP archive" });
                }

                var playlistStore = GetPlaylistStore();
                var collectionStore = GetCollectionStore();
                var fileSystem = new SmartListFileSystem(_applicationPaths);
                var existingPlaylists = await playlistStore.GetAllAsync();
                var existingCollections = await collectionStore.GetAllAsync();
                var existingPlaylistIds = existingPlaylists.Select(p => p.Id).ToHashSet();
                var existingCollectionIds = existingCollections.Select(c => c.Id).ToHashSet();

                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new JsonStringEnumConverter() }
                };

                var importResults = new List<object>();
                int importedPlaylistCount = 0;
                int importedCollectionCount = 0;
                int skippedCount = 0;
                int errorCount = 0;
                int importedImageCount = 0;

                using var zipStream = new MemoryStream();
                await file.CopyToAsync(zipStream);
                zipStream.Position = 0;

                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

                // Group entries by smart list ID (folder name or filename without extension)
                var entriesByListId = new Dictionary<string, List<ZipArchiveEntry>>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in archive.Entries)
                {
                    // Skip empty entries (directory markers) and system files
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        continue;
                    }

                    if (entry.Name.StartsWith("._") || entry.Name.StartsWith(".DS_Store"))
                    {
                        logger.LogDebug("Skipping system file: {FileName}", entry.FullName);
                        continue;
                    }

                    // Determine the smart list ID from the entry path
                    var listId = GetSmartListIdFromZipEntry(entry);
                    if (string.IsNullOrEmpty(listId))
                    {
                        logger.LogDebug("Skipping entry with invalid path: {FullName}", entry.FullName);
                        continue;
                    }

                    if (!entriesByListId.TryGetValue(listId, out var entries))
                    {
                        entries = new List<ZipArchiveEntry>();
                        entriesByListId[listId] = entries;
                    }
                    entries.Add(entry);
                }

                // Process each smart list
                foreach (var (listId, entries) in entriesByListId)
                {
                    // Find the config entry (either config.json or {guid}.json)
                    var configEntry = entries.FirstOrDefault(e =>
                        e.Name.Equals("config.json", StringComparison.OrdinalIgnoreCase) ||
                        e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

                    if (configEntry == null)
                    {
                        logger.LogDebug("No config file found for list ID {ListId}, skipping", listId);
                        continue;
                    }

                    try
                    {
                        // Read JSON content to check Type property first
                        string jsonContent;
                        using (var entryStream = configEntry.Open())
                        {
                            using var reader = new StreamReader(entryStream);
                            jsonContent = await reader.ReadToEndAsync();
                        }
                        using var jsonDoc = JsonDocument.Parse(jsonContent);

                        // Determine if this is a playlist or collection based on Type property
                        Core.Enums.SmartListType listType = Core.Enums.SmartListType.Playlist; // Default to Playlist for backward compatibility
                        bool hasTypeProperty = jsonDoc.RootElement.TryGetProperty("Type", out var typeElement);

                        if (hasTypeProperty)
                        {
                            if (typeElement.ValueKind == JsonValueKind.String)
                            {
                                var typeString = typeElement.GetString();
                                if (Enum.TryParse<Core.Enums.SmartListType>(typeString, ignoreCase: true, out var parsedType))
                                {
                                    listType = parsedType;
                                }
                            }
                            else if (typeElement.ValueKind == JsonValueKind.Number)
                            {
                                var typeValue = typeElement.GetInt32();
                                listType = typeValue == 1 ? Core.Enums.SmartListType.Collection : Core.Enums.SmartListType.Playlist;
                            }
                        }

                        // Deserialize to the correct type based on the Type field
                        if (listType == Core.Enums.SmartListType.Playlist)
                        {
                            var playlist = JsonSerializer.Deserialize<SmartPlaylistDto>(jsonContent, jsonOptions);
                            if (playlist == null || string.IsNullOrEmpty(playlist.Id))
                            {
                                logger.LogWarning("Invalid playlist data in file {FileName}: {Issue}",
                                    configEntry.Name, playlist == null ? "null playlist" : "empty ID");
                                importResults.Add(new { fileName = configEntry.Name, status = "error", message = "Invalid or empty playlist data" });
                                errorCount++;
                                continue;
                            }

                            if (string.IsNullOrWhiteSpace(playlist.Name))
                            {
                                logger.LogWarning("Playlist in file {FileName} has no name", configEntry.Name);
                                importResults.Add(new { fileName = configEntry.Name, status = "error", message = "Playlist must have a name" });
                                errorCount++;
                                continue;
                            }

                            // Ensure type is set
                            playlist.Type = Core.Enums.SmartListType.Playlist;

                            // Validate and potentially reassign user references
                            bool reassignedUsers = false;
                            Guid currentUserId = Guid.Empty;

                            // Helper function to get current user ID for reassignment
                            Guid GetCurrentUserIdForReassignment()
                            {
                                if (currentUserId == Guid.Empty)
                                {
                                    currentUserId = GetCurrentUserId();
                                }
                                return currentUserId;
                            }

                            // Check multi-user playlists (UserPlaylists array)
                            if (playlist.UserPlaylists != null && playlist.UserPlaylists.Count > 0)
                            {
                                var validUserMappings = new List<SmartPlaylistDto.UserPlaylistMapping>();

                                foreach (var userMapping in playlist.UserPlaylists)
                                {
                                    if (string.IsNullOrEmpty(userMapping.UserId) || 
                                        !Guid.TryParse(userMapping.UserId, out var userId) || 
                                        userId == Guid.Empty)
                                    {
                                        continue;
                                    }

                                    var user = _userManager.GetUserById(userId);
                                    if (user == null)
                                    {
                                        // Get current user ID for reassignment
                                        var reassignmentUserId = GetCurrentUserIdForReassignment();
                                        if (reassignmentUserId == Guid.Empty)
                                        {
                                            logger.LogWarning("Playlist '{PlaylistName}' references non-existent user {User} in UserPlaylists but cannot determine importing user for reassignment",
                                                playlist.Name, userMapping.UserId);
                                            // Continue to next user - we'll handle the case where all users are invalid below
                                            continue;
                                        }

                                        logger.LogWarning("Playlist '{PlaylistName}' references non-existent user {User} in UserPlaylists, reassigning to importing user {CurrentUserId}",
                                            playlist.Name, userMapping.UserId, reassignmentUserId);

                                        // Reassign to importing user
                                        validUserMappings.Add(new SmartPlaylistDto.UserPlaylistMapping
                                        {
                                            UserId = reassignmentUserId.ToString("N"),
                                            JellyfinPlaylistId = null  // Clear old ID - playlist doesn't exist for new user
                                        });
                                        reassignedUsers = true;
                                    }
                                    else
                                    {
                                        // User exists, keep the mapping
                                        validUserMappings.Add(userMapping);
                                    }
                                }

                                // Check if we have any valid users left
                                if (validUserMappings.Count == 0)
                                {
                                    logger.LogWarning("Playlist '{PlaylistName}' has no valid users in UserPlaylists after validation", playlist.Name);
                                    importResults.Add(new { fileName = configEntry.Name, status = "error", message = "Playlist has no valid users" });
                                    errorCount++;
                                    continue; // Skip this entire playlist
                                }

                                // Update UserPlaylists with valid/reassigned users
                                playlist.UserPlaylists = validUserMappings;

                                // Normalize and deduplicate UserPlaylists (consistent with create/update paths)
                                var normalizedUserPlaylists = new List<SmartPlaylistDto.UserPlaylistMapping>();
                                var seenUserIds = new HashSet<Guid>();

                                foreach (var userMapping in validUserMappings)
                                {
                                    if (Guid.TryParse(userMapping.UserId, out var userId) && seenUserIds.Add(userId))
                                    {
                                        normalizedUserPlaylists.Add(new SmartPlaylistDto.UserPlaylistMapping
                                        {
                                            UserId = userId.ToString("N"), // Standard format without dashes
                                            JellyfinPlaylistId = userMapping.JellyfinPlaylistId
                                        });
                                    }
                                    else
                                    {
                                        logger.LogDebug("Duplicate user ID {UserId} detected during import for playlist {Name}, skipping", userId, playlist.Name);
                                    }
                                }

                                playlist.UserPlaylists = normalizedUserPlaylists;

                                // Also update the deprecated UserId field for backwards compatibility (first user's ID)
                                if (normalizedUserPlaylists.Count > 0 && Guid.TryParse(normalizedUserPlaylists[0].UserId, out var firstUserId))
                                {
                                    playlist.UserId = firstUserId.ToString("D");
                                }
                            }
                            // Check single-user playlist (backwards compatibility - top-level UserId)
                            // DEPRECATED: playlist.UserId is for backwards compatibility with old single-user playlists.
                            // It is planned to be removed in version 10.12. Use UserPlaylists array instead.
                            else if (!string.IsNullOrEmpty(playlist.UserId) && Guid.TryParse(playlist.UserId, out var playlistUserIdParsed) && playlistUserIdParsed != Guid.Empty)
                            {
                                var user = _userManager.GetUserById(playlistUserIdParsed);
                                if (user == null)
                                {
                                    // Get current user ID for reassignment
                                    var reassignmentUserId = GetCurrentUserIdForReassignment();
                                    if (reassignmentUserId == Guid.Empty)
                                    {
                                        logger.LogWarning("Playlist '{PlaylistName}' references non-existent user {User} but cannot determine importing user for reassignment",
                                            playlist.Name, playlist.UserId);
                                        importResults.Add(new { fileName = configEntry.Name, status = "error", message = "Cannot reassign playlist - unable to determine importing user" });
                                        errorCount++;
                                        continue; // Skip this entire playlist
                                    }

                                    logger.LogWarning("Playlist '{PlaylistName}' references non-existent user {User}, reassigning to importing user {CurrentUserId}",
                                        playlist.Name, playlist.UserId, reassignmentUserId);

                                    // DEPRECATED: playlist.UserId is for backwards compatibility with old single-user playlists.
                                    // It is planned to be removed in version 10.12. Use UserPlaylists array instead.
                                    playlist.UserId = reassignmentUserId.ToString("D");
                                    reassignedUsers = true;
                                }
                            }
                            else
                            {
                                // No users specified at all - this is invalid
                                logger.LogWarning("Playlist '{PlaylistName}' has no users specified (neither UserId nor UserPlaylists)", playlist.Name);
                                importResults.Add(new { fileName = configEntry.Name, status = "error", message = "Playlist must have at least one user" });
                                errorCount++;
                                continue; // Skip this entire playlist
                            }

                            // Note: We don't reassign user-specific expression rules if the referenced user doesn't exist.
                            // The system will naturally fall back to the playlist user for such rules.

                            // Add note to import results if users were reassigned
                            if (reassignedUsers)
                            {
                                logger.LogInformation("Reassigned user references in playlist '{PlaylistName}' due to non-existent users", playlist.Name);
                            }

                            if (existingPlaylistIds.Contains(playlist.Id))
                            {
                                importResults.Add(new { fileName = configEntry.Name, listName = playlist.Name, listType = "Playlist", status = "skipped", message = "Playlist with this ID already exists" });
                                skippedCount++;
                                continue;
                            }

                            // Import the playlist
                            await playlistStore.SaveAsync(playlist);

                            // Extract any image files for this playlist
                            var imageCount = await ExtractImagesForSmartListAsync(entries, playlist.Id, fileSystem);
                            importedImageCount += imageCount;

                            // Update the auto-refresh cache with the imported playlist
                            AutoRefreshService.Instance?.UpdatePlaylistInCache(playlist);

                            var message = imageCount > 0 ? $"Successfully imported with {imageCount} image(s)" : "Successfully imported";
                            importResults.Add(new { fileName = configEntry.Name, listName = playlist.Name, listType = "Playlist", status = "imported", message });
                            importedPlaylistCount++;

                            logger.LogDebug("Imported playlist {PlaylistName} (ID: {PlaylistId}) from {FileName} with {ImageCount} images",
                                playlist.Name, playlist.Id, configEntry.Name, imageCount);
                        }
                        else if (listType == Core.Enums.SmartListType.Collection)
                        {
                            var collection = JsonSerializer.Deserialize<SmartCollectionDto>(jsonContent, jsonOptions);
                            if (collection == null || string.IsNullOrEmpty(collection.Id))
                            {
                                logger.LogWarning("Invalid collection data in file {FileName}: {Issue}",
                                    configEntry.Name, collection == null ? "null collection" : "empty ID");
                                importResults.Add(new { fileName = configEntry.Name, status = "error", message = "Invalid or empty collection data" });
                                errorCount++;
                                continue;
                            }

                            if (string.IsNullOrWhiteSpace(collection.Name))
                            {
                                logger.LogWarning("Collection in file {FileName} has no name", configEntry.Name);
                                importResults.Add(new { fileName = configEntry.Name, status = "error", message = "Collection must have a name" });
                                errorCount++;
                                continue;
                            }

                            // Ensure type is set
                            collection.Type = Core.Enums.SmartListType.Collection;

                            // Validate and potentially reassign user references (collections use User property from base class)
                            bool reassignedUsers = false;
                            Guid currentUserId = Guid.Empty;

                            // Check collection user
                            if (!string.IsNullOrEmpty(collection.UserId) && Guid.TryParse(collection.UserId, out var collectionUserIdParsed) && collectionUserIdParsed != Guid.Empty)
                            {
                                var user = _userManager.GetUserById(collectionUserIdParsed);
                                if (user == null)
                                {
                                    // Only get current user ID when we need to reassign
                                    if (currentUserId == Guid.Empty)
                                    {
                                        currentUserId = GetCurrentUserId();
                                        if (currentUserId == Guid.Empty)
                                        {
                                            logger.LogWarning("Collection '{CollectionName}' references non-existent user {User} but cannot determine importing user for reassignment",
                                                collection.Name, collection.UserId);
                                            importResults.Add(new { fileName = configEntry.Name, status = "error", message = "Cannot reassign collection - unable to determine importing user" });
                                            errorCount++;
                                            continue; // Skip this entire collection,
                                        }
                                    }

                                    logger.LogWarning("Collection '{CollectionName}' references non-existent user {User}, reassigning to importing user {CurrentUserId}",
                                        collection.Name, collection.UserId, currentUserId);

                                    collection.UserId = currentUserId.ToString("D");
                                    reassignedUsers = true;
                                }
                            }

                            // Add note to import results if users were reassigned
                            if (reassignedUsers)
                            {
                                logger.LogInformation("Reassigned user references in collection '{CollectionName}' due to non-existent users", collection.Name);
                            }

                            if (existingCollectionIds.Contains(collection.Id))
                            {
                                importResults.Add(new { fileName = configEntry.Name, listName = collection.Name, listType = "Collection", status = "skipped", message = "Collection with this ID already exists" });
                                skippedCount++;
                                continue;
                            }

                            // Import the collection
                            await collectionStore.SaveAsync(collection);

                            // Extract any image files for this collection
                            var imageCount = await ExtractImagesForSmartListAsync(entries, collection.Id, fileSystem);
                            importedImageCount += imageCount;

                            // Update the auto-refresh cache with the imported collection
                            AutoRefreshService.Instance?.UpdateCollectionInCache(collection);

                            var message = imageCount > 0 ? $"Successfully imported with {imageCount} image(s)" : "Successfully imported";
                            importResults.Add(new { fileName = configEntry.Name, listName = collection.Name, listType = "Collection", status = "imported", message });
                            importedCollectionCount++;

                            logger.LogDebug("Imported collection {CollectionName} (ID: {CollectionId}) from {FileName} with {ImageCount} images",
                                collection.Name, collection.Id, configEntry.Name, imageCount);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error importing smart list from {FileName}", configEntry.FullName);
                        importResults.Add(new { fileName = configEntry.Name, status = "error", message = ex.Message });
                        errorCount++;
                    }
                }

                var totalImported = importedPlaylistCount + importedCollectionCount;
                var summary = new
                {
                    totalLists = entriesByListId.Count,
                    imported = totalImported,
                    importedPlaylists = importedPlaylistCount,
                    importedCollections = importedCollectionCount,
                    importedImages = importedImageCount,
                    skipped = skippedCount,
                    errors = errorCount,
                    details = importResults,
                };

                logger.LogInformation("Import completed: {Imported} imported ({Playlists} playlists, {Collections} collections, {Images} images), {Skipped} skipped, {Errors} errors",
                    totalImported, importedPlaylistCount, importedCollectionCount, importedImageCount, skippedCount, errorCount);

                return Ok(summary);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error importing smart lists");
                return StatusCode(StatusCodes.Status500InternalServerError, "Error importing smart lists");
            }
        }

        /// <summary>
        /// Restart the schedule timer (useful for debugging timer issues)
        /// </summary>
        [HttpPost("Timer/Restart")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public IActionResult RestartScheduleTimer()
        {
            try
            {
                var autoRefreshService = AutoRefreshService.Instance;
                if (autoRefreshService == null)
                {
                    return StatusCode(StatusCodes.Status500InternalServerError, "AutoRefreshService is not available");
                }

                autoRefreshService.RestartScheduleTimer();

                var nextCheck = autoRefreshService.GetNextScheduledCheckTime();
                var isRunning = autoRefreshService.IsScheduleTimerRunning();

                return Ok(new
                {
                    message = "Schedule timer restarted successfully",
                    isRunning = isRunning,
                    nextScheduledCheck = nextCheck?.ToString("o"),
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error restarting schedule timer");
                return StatusCode(StatusCodes.Status500InternalServerError, "Error restarting schedule timer");
            }
        }

        /// <summary>
        /// Get schedule timer status
        /// </summary>
        [HttpGet("Timer/Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public IActionResult GetScheduleTimerStatus()
        {
            try
            {
                var autoRefreshService = AutoRefreshService.Instance;
                if (autoRefreshService == null)
                {
                    return StatusCode(StatusCodes.Status500InternalServerError, "AutoRefreshService is not available");
                }

                var isRunning = autoRefreshService.IsScheduleTimerRunning();
                var nextCheck = autoRefreshService.GetNextScheduledCheckTime();

                return Ok(new
                {
                    isRunning = isRunning,
                    nextScheduledCheck = nextCheck?.ToString("o"),
                    currentTime = DateTime.Now.ToString("o"),
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error getting schedule timer status");
                return StatusCode(StatusCodes.Status500InternalServerError, "Error getting schedule timer status");
            }
        }

        /// <summary>
        /// Get refresh status including ongoing operations, history, and statistics
        /// </summary>
        [HttpGet("Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public IActionResult GetRefreshStatus()
        {
            try
            {
                if (_refreshStatusService == null)
                {
                    logger.LogWarning("RefreshStatusService is null in GetRefreshStatus");
                    return StatusCode(StatusCodes.Status500InternalServerError, "RefreshStatusService is not available");
                }

                var ongoing = _refreshStatusService.GetOngoingOperations().Select(op => new
                {
                    listId = op.ListId,
                    listName = op.ListName,
                    listType = op.ListType.ToString(),
                    triggerType = op.TriggerType.ToString(),
                    startTime = op.StartTime.ToString("o"),
                    totalItems = op.TotalItems,
                    processedItems = op.ProcessedItems,
                    estimatedTimeRemaining = op.EstimatedTimeRemaining?.TotalSeconds,
                    elapsedTime = op.ElapsedTime.TotalSeconds,
                    errorMessage = op.ErrorMessage,
                    batchCurrentIndex = op.BatchCurrentIndex,
                    batchTotalCount = op.BatchTotalCount
                }).ToList();

                var history = _refreshStatusService.GetRefreshHistory().Select(h => new
                {
                    listId = h.ListId,
                    listName = h.ListName,
                    listType = h.ListType.ToString(),
                    triggerType = h.TriggerType.ToString(),
                    startTime = h.StartTime.ToString("o"),
                    endTime = h.EndTime?.ToString("o"),
                    duration = h.Duration.TotalSeconds,
                    success = h.Success,
                    errorMessage = h.ErrorMessage
                }).ToList();

                var statistics = _refreshStatusService.GetStatistics();

                return Ok(new
                {
                    ongoingOperations = ongoing,
                    history = history,
                    statistics = new
                    {
                        totalLists = statistics.TotalLists,
                        ongoingOperationsCount = statistics.OngoingOperationsCount,
                        queuedOperationsCount = statistics.QueuedOperationsCount,
                        lastRefreshTime = statistics.LastRefreshTime?.ToString("o"),
                        averageRefreshDuration = statistics.AverageRefreshDuration?.TotalSeconds,
                        successfulRefreshes = statistics.SuccessfulRefreshes,
                        failedRefreshes = statistics.FailedRefreshes
                    }
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error getting refresh status");
                return StatusCode(StatusCodes.Status500InternalServerError, "Error getting refresh status");
            }
        }

        /// <summary>
        /// Get refresh history (last refresh per list)
        /// </summary>
        [HttpGet("Status/History")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public IActionResult GetRefreshHistory()
        {
            try
            {
                var history = _refreshStatusService.GetRefreshHistory().Select(h => new
                {
                    listId = h.ListId,
                    listName = h.ListName,
                    listType = h.ListType.ToString(),
                    triggerType = h.TriggerType.ToString(),
                    startTime = h.StartTime.ToString("o"),
                    endTime = h.EndTime?.ToString("o"),
                    duration = h.Duration.TotalSeconds,
                    success = h.Success,
                    errorMessage = h.ErrorMessage
                }).ToList();

                return Ok(history);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error getting refresh history");
                return StatusCode(StatusCodes.Status500InternalServerError, "Error getting refresh history");
            }
        }

        /// <summary>
        /// Get ongoing refresh operations
        /// </summary>
        [HttpGet("Status/Ongoing")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public IActionResult GetOngoingOperations()
        {
            try
            {
                var ongoing = _refreshStatusService.GetOngoingOperations().Select(op => new
                {
                    listId = op.ListId,
                    listName = op.ListName,
                    listType = op.ListType.ToString(),
                    triggerType = op.TriggerType.ToString(),
                    startTime = op.StartTime.ToString("o"),
                    totalItems = op.TotalItems,
                    processedItems = op.ProcessedItems,
                    estimatedTimeRemaining = op.EstimatedTimeRemaining?.TotalSeconds,
                    elapsedTime = op.ElapsedTime.TotalSeconds,
                    errorMessage = op.ErrorMessage,
                    batchCurrentIndex = op.BatchCurrentIndex,
                    batchTotalCount = op.BatchTotalCount
                }).ToList();

                return Ok(ongoing);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error getting ongoing operations");
                return StatusCode(StatusCodes.Status500InternalServerError, "Error getting ongoing operations");
            }
        }

        /// <summary>
        /// Deletes all Jellyfin playlists for all users in a smart playlist.
        /// Handles both UserPlaylists array and legacy JellyfinPlaylistId field.
        /// </summary>
        /// <summary>
        /// Removes smart suffix from all Jellyfin playlists for all users in a smart playlist.
        /// Handles both UserPlaylists array and legacy JellyfinPlaylistId field.
        /// </summary>
        private async Task RemoveSmartSuffixFromAllPlaylistsAsync(SmartPlaylistDto playlist)
        {
            var playlistService = GetPlaylistService();
            
            // Remove smart suffix from all Jellyfin playlists for all users
            if (playlist.UserPlaylists != null && playlist.UserPlaylists.Count > 0)
            {
                logger.LogDebug("Removing smart suffix from {Count} Jellyfin playlists for multi-user playlist {PlaylistName}", playlist.UserPlaylists.Count, playlist.Name);
                foreach (var userMapping in playlist.UserPlaylists)
                {
                    if (!string.IsNullOrEmpty(userMapping.JellyfinPlaylistId))
                    {
                        try
                        {
                            var tempDto = new SmartPlaylistDto
                            {
                                Id = playlist.Id,
                                Name = playlist.Name,
                                UserId = userMapping.UserId,
                                JellyfinPlaylistId = userMapping.JellyfinPlaylistId
                            };
                            await playlistService.RemoveSmartSuffixAsync(tempDto);
                            logger.LogDebug("Removed smart suffix from Jellyfin playlist {JellyfinPlaylistId} for user {UserId}", userMapping.JellyfinPlaylistId, userMapping.UserId);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to remove smart suffix from Jellyfin playlist {JellyfinPlaylistId} for user {UserId}, continuing", userMapping.JellyfinPlaylistId, userMapping.UserId);
                        }
                    }
                }
            }
            else
            {
                await playlistService.RemoveSmartSuffixAsync(playlist);
            }
        }

        /// <summary>
        /// Uploads an image for a smart list.
        /// </summary>
        /// <param name="id">The smart list ID.</param>
        /// <param name="file">The image file.</param>
        /// <param name="imageType">The image type (Primary, Backdrop, Banner, etc.).</param>
        /// <returns>The uploaded image info.</returns>
        [HttpPost("{id}/images")]
        public async Task<ActionResult<SmartListImageDto>> UploadImage(
            [FromRoute, Required] string id,
            [FromForm] IFormFile file,
            [FromForm, Required] string imageType)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(new { message = "No file uploaded" });
            }

            if (!SmartListImageService.ValidImageTypes.Contains(imageType))
            {
                return BadRequest(new { message = $"Invalid image type: {imageType}. Valid types: {string.Join(", ", SmartListImageService.ValidImageTypes)}" });
            }

            // Validate the smart list exists
            if (!Guid.TryParse(id, out var guidId))
            {
                return BadRequest(new { message = "Invalid smart list ID format" });
            }

            // Normalize ID to dashed format for consistent image folder operations
            var normalizedId = guidId.ToString("D");

            var playlistStore = GetPlaylistStore();
            var collectionStore = GetCollectionStore();
            var playlist = await playlistStore.GetByIdAsync(guidId);
            var collection = playlist == null ? await collectionStore.GetByIdAsync(guidId) : null;

            if (playlist == null && collection == null)
            {
                return NotFound(new { message = "Smart list not found" });
            }

            // Validate the image
            var validation = SmartListImageService.ValidateImage(file.FileName, file.Length, file.ContentType);
            if (!validation.IsValid)
            {
                return BadRequest(new { message = validation.ErrorMessage });
            }

            try
            {
                // Save the image using normalized ID
                await using var stream = file.OpenReadStream();
                var fileName = await _imageService.SaveImageAsync(normalizedId, imageType, stream, file.FileName);

                // Update the smart list's CustomImages
                var smartList = (SmartListDto?)playlist ?? collection;
                smartList!.CustomImages ??= new Dictionary<string, string>();
                smartList.CustomImages[imageType] = fileName;

                // Save the updated smart list
                if (playlist != null)
                {
                    await playlistStore.SaveAsync(playlist);
                    AutoRefreshService.Instance?.UpdatePlaylistInCache(playlist);

                    // Do NOT queue refresh here - let the UI trigger one manual refresh after all uploads complete
                    // This avoids queueing multiple refreshes when uploading multiple images
                }
                else if (collection != null)
                {
                    await collectionStore.SaveAsync(collection);
                    AutoRefreshService.Instance?.UpdateCollectionInCache(collection);

                    // Do NOT queue refresh here - let the UI trigger one manual refresh after all uploads complete
                }

                logger.LogInformation("Uploaded {ImageType} image for smart list {SmartListId}", imageType, normalizedId);

                return Ok(new SmartListImageDto
                {
                    ImageType = imageType,
                    FileName = fileName,
                    ContentType = file.ContentType,
                    FileSize = file.Length
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to upload image for smart list {SmartListId}", id);
                return StatusCode(500, new { message = "Failed to upload image", error = ex.Message });
            }
        }

        /// <summary>
        /// Deletes a specific image for a smart list.
        /// </summary>
        /// <param name="id">The smart list ID.</param>
        /// <param name="imageType">The image type to delete.</param>
        [HttpDelete("{id}/images/{imageType}")]
        public async Task<ActionResult> DeleteImage(
            [FromRoute, Required] string id,
            [FromRoute, Required] string imageType)
        {
            // Validate imageType to prevent path traversal
            if (!SmartListImageService.ValidImageTypes.Contains(imageType))
            {
                return BadRequest(new { message = $"Invalid image type: {imageType}. Valid types: {string.Join(", ", SmartListImageService.ValidImageTypes)}" });
            }

            if (!Guid.TryParse(id, out var guidId))
            {
                return BadRequest(new { message = "Invalid smart list ID format" });
            }

            // Normalize ID to dashed format for consistent image folder operations
            var normalizedId = guidId.ToString("D");

            var playlistStore = GetPlaylistStore();
            var collectionStore = GetCollectionStore();
            var playlist = await playlistStore.GetByIdAsync(guidId);
            var collection = playlist == null ? await collectionStore.GetByIdAsync(guidId) : null;

            if (playlist == null && collection == null)
            {
                return NotFound(new { message = "Smart list not found" });
            }

            try
            {
                var smartList = (SmartListDto?)playlist ?? collection;

                // Delete the image file from smartlists/images/ folder
                await _imageService.DeleteImageAsync(normalizedId, imageType);

                // Also delete from Jellyfin playlist/collection folder
                // This is an explicit delete action, so we should remove the image from Jellyfin too
                // For playlists, we need to delete from ALL user playlists (multi-user format) or the single playlist (legacy format)
                var jellyfinItemsToUpdate = new List<MediaBrowser.Controller.Entities.BaseItem>();

                if (smartList is SmartPlaylistDto playlistDto)
                {
                    // Check legacy single-user format first
                    if (!string.IsNullOrEmpty(playlistDto.JellyfinPlaylistId) &&
                        Guid.TryParse(playlistDto.JellyfinPlaylistId, out var playlistJellyfinId))
                    {
                        var item = _libraryManager.GetItemById(playlistJellyfinId);
                        if (item != null)
                        {
                            jellyfinItemsToUpdate.Add(item);
                        }
                    }

                    // Check multi-user format (UserPlaylists)
                    if (playlistDto.UserPlaylists != null)
                    {
                        foreach (var userPlaylist in playlistDto.UserPlaylists)
                        {
                            if (!string.IsNullOrEmpty(userPlaylist.JellyfinPlaylistId) &&
                                Guid.TryParse(userPlaylist.JellyfinPlaylistId, out var userJellyfinId))
                            {
                                var item = _libraryManager.GetItemById(userJellyfinId);
                                if (item != null && !jellyfinItemsToUpdate.Contains(item))
                                {
                                    jellyfinItemsToUpdate.Add(item);
                                }
                            }
                        }
                    }
                }
                else if (smartList is SmartCollectionDto collectionDto)
                {
                    if (!string.IsNullOrEmpty(collectionDto.JellyfinCollectionId) &&
                        Guid.TryParse(collectionDto.JellyfinCollectionId, out var collectionJellyfinId))
                    {
                        var item = _libraryManager.GetItemById(collectionJellyfinId);
                        if (item != null)
                        {
                            jellyfinItemsToUpdate.Add(item);
                        }
                    }
                }

                if (jellyfinItemsToUpdate.Count > 0)
                {
                    foreach (var jellyfinItem in jellyfinItemsToUpdate)
                    {
                        await _imageService.DeleteImageFromJellyfinItemAsync(jellyfinItem, imageType);
                    }
                    logger.LogDebug("DeleteImage: Deleted {ImageType} from {Count} Jellyfin item(s)", imageType, jellyfinItemsToUpdate.Count);
                }
                else
                {
                    logger.LogDebug("DeleteImage: No Jellyfin items found for smart list {SmartListId}", normalizedId);
                }

                // Update the smart list's CustomImages
                if (smartList!.CustomImages != null && smartList.CustomImages.ContainsKey(imageType))
                {
                    smartList.CustomImages.Remove(imageType);
                    if (smartList.CustomImages.Count == 0)
                    {
                        smartList.CustomImages = null;
                    }

                    // Save the updated DTO
                    if (playlist != null)
                    {
                        await playlistStore.SaveAsync(playlist);
                        AutoRefreshService.Instance?.UpdatePlaylistInCache(playlist);
                    }
                    else if (collection != null)
                    {
                        await collectionStore.SaveAsync(collection);
                        AutoRefreshService.Instance?.UpdateCollectionInCache(collection);
                    }
                }

                logger.LogInformation("Deleted {ImageType} image for smart list {SmartListId}", imageType, normalizedId);
                return Ok(new { message = $"Image '{imageType}' deleted successfully" });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete image for smart list {SmartListId}", normalizedId);
                return StatusCode(500, new { message = "Failed to delete image", error = ex.Message });
            }
        }

        /// <summary>
        /// Gets all images for a smart list.
        /// </summary>
        /// <param name="id">The smart list ID.</param>
        [HttpGet("{id}/images")]
        public async Task<ActionResult<List<SmartListImageDto>>> GetImages([FromRoute, Required] string id)
        {
            if (!Guid.TryParse(id, out var guidId))
            {
                return BadRequest(new { message = "Invalid smart list ID format" });
            }

            // Normalize ID to dashed format for consistent image folder operations
            var normalizedId = guidId.ToString("D");

            var playlistStore = GetPlaylistStore();
            var collectionStore = GetCollectionStore();
            var playlist = await playlistStore.GetByIdAsync(guidId);
            var collection = playlist == null ? await collectionStore.GetByIdAsync(guidId) : null;

            if (playlist == null && collection == null)
            {
                return NotFound(new { message = "Smart list not found" });
            }

            var images = _imageService.GetImagesForSmartList(normalizedId);

            var result = images.Select(kvp => new SmartListImageDto
            {
                ImageType = kvp.Key,
                FileName = kvp.Value,
                FileSize = GetImageFileSize(normalizedId, kvp.Key)
            }).ToList();

            return Ok(result);
        }

        /// <summary>
        /// Gets a specific image file for a smart list.
        /// </summary>
        /// <param name="id">The smart list ID.</param>
        /// <param name="imageType">The image type.</param>
        [HttpGet("{id}/images/{imageType}/file")]
        [AllowAnonymous]
        public ActionResult GetImageFile(
            [FromRoute, Required] string id,
            [FromRoute, Required] string imageType)
        {
            // Validate imageType to prevent path traversal
            if (!SmartListImageService.ValidImageTypes.Contains(imageType))
            {
                return NotFound(new { message = "Image not found" });
            }

            // Normalize ID to dashed format for consistent image folder operations
            if (!Guid.TryParse(id, out var guidId))
            {
                return NotFound(new { message = "Image not found" });
            }
            var normalizedId = guidId.ToString("D");

            var imagePath = _imageService.GetImagePath(normalizedId, imageType);
            if (string.IsNullOrEmpty(imagePath) || !System.IO.File.Exists(imagePath))
            {
                return NotFound(new { message = "Image not found" });
            }

            var extension = Path.GetExtension(imagePath).ToLowerInvariant();
            var contentType = extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".avif" => "image/avif",
                ".svg" => "image/svg+xml",
                ".tiff" or ".tif" => "image/tiff",
                ".apng" => "image/apng",
                ".ico" => "image/x-icon",
                _ => "application/octet-stream"
            };

            return PhysicalFile(imagePath, contentType);
        }

        /// <summary>
        /// Gets the file size for an image.
        /// </summary>
        private long? GetImageFileSize(string smartListId, string imageType)
        {
            var imagePath = _imageService.GetImagePath(smartListId, imageType);
            if (string.IsNullOrEmpty(imagePath) || !System.IO.File.Exists(imagePath))
            {
                return null;
            }

            return new FileInfo(imagePath).Length;
        }
    }
}