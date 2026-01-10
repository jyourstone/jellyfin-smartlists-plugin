using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Enums;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Services.Playlists;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Api.Controllers
{
    /// <summary>
    /// User-facing SmartList API controller for creating and managing personal smart playlists.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("Plugins/SmartLists/User")]
    [Produces("application/json")]
    public class UserSmartListController : ControllerBase
    {
        private readonly ILogger<UserSmartListController> _logger;
        private readonly IServerApplicationPaths _applicationPaths;
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IPlaylistManager _playlistManager;
        private readonly ICollectionManager _collectionManager;
        private readonly IUserDataManager _userDataManager;
        private readonly IProviderManager _providerManager;
        private readonly ISessionManager _sessionManager;
        private readonly Services.Shared.RefreshQueueService _refreshQueueService;

        public UserSmartListController(
            ILogger<UserSmartListController> logger,
            IServerApplicationPaths applicationPaths,
            IUserManager userManager,
            ILibraryManager libraryManager,
            IPlaylistManager playlistManager,
            ICollectionManager collectionManager,
            IUserDataManager userDataManager,
            IProviderManager providerManager,
            ISessionManager sessionManager,
            Services.Shared.RefreshQueueService refreshQueueService)
        {
            _logger = logger;
            _applicationPaths = applicationPaths;
            _userManager = userManager;
            _libraryManager = libraryManager;
            _playlistManager = playlistManager;
            _collectionManager = collectionManager;
            _userDataManager = userDataManager;
            _providerManager = providerManager;
            _sessionManager = sessionManager;
            _refreshQueueService = refreshQueueService;
        }

        private PlaylistStore GetPlaylistStore()
        {
            var fileSystem = new SmartListFileSystem(_applicationPaths);
            return new PlaylistStore(fileSystem);
        }

        private Services.Collections.CollectionStore GetCollectionStore()
        {
            var fileSystem = new SmartListFileSystem(_applicationPaths);
            return new Services.Collections.CollectionStore(fileSystem);
        }

        private PlaylistService GetPlaylistService()
        {
            var loggerFactory = new LoggerFactory();
            var playlistServiceLogger = loggerFactory.CreateLogger<PlaylistService>();
            return new PlaylistService(_userManager, _libraryManager, _playlistManager, _userDataManager, playlistServiceLogger, _providerManager);
        }

        /// <summary>
        /// Gets the current user's ID from the session.
        /// </summary>
        private Guid GetCurrentUserId()
        {
            var userId = User.GetUserId();
            if (userId == Guid.Empty)
            {
                throw new UnauthorizedAccessException("User not authenticated");
            }
            return userId;
        }

        /// <summary>
        /// Checks if the current user has permission to manage collections.
        /// </summary>
        private bool CanUserManageCollections()
        {
            var userId = GetCurrentUserId();
            Jellyfin.Database.Implementations.Entities.User? user = _userManager.GetUserById(userId);
            
            if (user == null)
            {
                return false;
            }

            // Admins can always create collections (they have access to everything)
            // The "Allow this user to manage collections" checkbox doesn't apply to admins by default
            if (user.HasPermission(PermissionKind.IsAdministrator))
            {
                return true;
            }

            // Check if user has the "Allow this user to manage collections" permission
            // Uses the HasPermission extension method from Jellyfin.Extensions
            return user.HasPermission(PermissionKind.EnableCollectionManagement);
        }

        /// <summary>
        /// Gets all smart playlists for the current user (base endpoint for compatibility with admin page JS).
        /// </summary>
        /// <returns>List of smart playlists owned by the current user.</returns>
        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<IEnumerable<SmartListDto>>> GetUserPlaylistsBase()
        {
            // Delegate to the main GetUserPlaylists method
            return await GetUserPlaylists().ConfigureAwait(false);
        }

        /// <summary>
        /// Creates a new smart list (playlist or collection) for the current user (base endpoint for compatibility with admin page JS).
        /// </summary>
        /// <param name="list">The smart list to create.</param>
        /// <returns>The created smart list.</returns>
        [HttpPost]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<SmartPlaylistDto>> CreateUserSmartList([FromBody] SmartPlaylistDto? list)
        {
            if (list == null)
            {
                return BadRequest(new { error = "List data is required" });
            }

            try
            {
                var userId = GetCurrentUserId();
                var user = _userManager.GetUserById(userId);
                
                if (user == null)
                {
                    return BadRequest(new { error = "User not found" });
                }

                // Log the incoming type for debugging
                _logger.LogInformation("CreateUserSmartList: Received Type={Type}, Name={Name}", list.Type, list.Name);

                // Check if user can create collections
                if (list.Type == Core.Enums.SmartListType.Collection && !CanUserManageCollections())
                {
                    return StatusCode(403, new { error = "You do not have permission to create collections" });
                }

                // Override user selection - always use current user
                list.UserId = userId.ToString();
                list.UserPlaylists = new List<SmartPlaylistDto.UserPlaylistMapping>
                {
                    new SmartPlaylistDto.UserPlaylistMapping
                    {
                        UserId = userId.ToString(),
                        JellyfinPlaylistId = null
                    }
                };

                // Validate required fields
                if (string.IsNullOrWhiteSpace(list.Name))
                {
                    return BadRequest(new { error = $"{(list.Type == Core.Enums.SmartListType.Collection ? "Collection" : "Playlist")} name is required" });
                }

                // Set defaults
                if (string.IsNullOrEmpty(list.Id))
                {
                    list.Id = Guid.NewGuid().ToString();
                }

                if (list.Order == null)
                {
                    list.Order = new OrderDto { SortOptions = new List<SortOption>() };
                }

                // Save to appropriate store based on type
                if (list.Type == Core.Enums.SmartListType.Collection)
                {
                    // For collections, we need to use SmartCollectionDto
                    var collectionDto = new SmartCollectionDto
                    {
                        Id = list.Id,
                        Name = list.Name,
                        Type = Core.Enums.SmartListType.Collection,
                        UserId = userId.ToString(),
                        MediaTypes = list.MediaTypes,
                        ExpressionSets = list.ExpressionSets,
                        Order = list.Order,
                        Enabled = list.Enabled,
                        MaxItems = list.MaxItems,
                        MaxPlayTimeMinutes = list.MaxPlayTimeMinutes,
                        DateCreated = DateTime.UtcNow
                    };

                    var collectionStore = GetCollectionStore();
                    var createdCollection = await collectionStore.SaveAsync(collectionDto).ConfigureAwait(false);

                    // Update the auto-refresh cache with the new collection
                    Services.Shared.AutoRefreshService.Instance?.UpdateCollectionInCache(createdCollection);

                    // Clear the rule cache
                    SmartList.ClearRuleCache(_logger);

                    // Enqueue refresh operation to actually create the Jellyfin collection
                    if (createdCollection.Enabled)
                    {
                        _logger.LogDebug("Enqueuing refresh for newly created collection {CollectionName}", createdCollection.Name);
                        var queueItem = new Services.Shared.RefreshQueueItem
                        {
                            ListId = createdCollection.Id ?? Guid.NewGuid().ToString(),
                            ListName = createdCollection.Name,
                            ListType = Core.Enums.SmartListType.Collection,
                            OperationType = Services.Shared.RefreshOperationType.Create,
                            ListData = createdCollection,
                            UserId = createdCollection.UserId,
                            TriggerType = Core.Enums.RefreshTriggerType.Manual
                        };

                        _refreshQueueService.EnqueueOperation(queueItem);
                        _logger.LogInformation("User {UserId} created smart collection '{Name}' and enqueued for refresh", userId, createdCollection.Name);
                    }
                    else
                    {
                        _logger.LogInformation("User {UserId} created disabled smart collection '{Name}' (not enqueued for refresh)", userId, createdCollection.Name);
                    }

                    return CreatedAtAction(nameof(GetUserPlaylists), new { id = createdCollection.Id }, createdCollection);
                }
                else
                {
                    // Set DateCreated for playlists
                    list.DateCreated = DateTime.UtcNow;

                    var store = GetPlaylistStore();
                    var createdPlaylist = await store.SaveAsync(list).ConfigureAwait(false);

                    // Update the auto-refresh cache with the new playlist
                    Services.Shared.AutoRefreshService.Instance?.UpdatePlaylistInCache(createdPlaylist);

                    // Clear the rule cache
                    SmartList.ClearRuleCache(_logger);

                    // Enqueue refresh operation to actually create the Jellyfin playlist
                    if (createdPlaylist.Enabled)
                    {
                        _logger.LogDebug("Enqueuing refresh for newly created playlist {PlaylistName}", createdPlaylist.Name);
                        var queueItem = new Services.Shared.RefreshQueueItem
                        {
                            ListId = createdPlaylist.Id ?? Guid.NewGuid().ToString(),
                            ListName = createdPlaylist.Name,
                            ListType = Core.Enums.SmartListType.Playlist,
                            OperationType = Services.Shared.RefreshOperationType.Create,
                            ListData = createdPlaylist,
                            UserId = userId.ToString(),
                            TriggerType = Core.Enums.RefreshTriggerType.Manual
                        };

                        _refreshQueueService.EnqueueOperation(queueItem);
                        _logger.LogInformation("User {UserId} created smart playlist '{Name}' and enqueued for refresh", userId, createdPlaylist.Name);
                    }
                    else
                    {
                        _logger.LogInformation("User {UserId} created disabled smart playlist '{Name}' (not enqueued for refresh)", userId, createdPlaylist.Name);
                    }

                    return CreatedAtAction(nameof(GetUserPlaylists), new { id = createdPlaylist.Id }, createdPlaylist);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating user smart list");
                return StatusCode(500, new { error = "Failed to create smart list" });
            }
        }

        /// <summary>
        /// Gets all smart playlists for the current user.
        /// </summary>
        /// <returns>List of smart playlists owned by the current user.</returns>
        [HttpGet("Playlists")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<IEnumerable<SmartListDto>>> GetUserPlaylists()
        {
            try
            {
                var userId = GetCurrentUserId();
                var normalizedUserId = userId.ToString("N"); // Normalize to format without dashes
                
                // Get both playlists and collections
                var playlistStore = GetPlaylistStore();
                var collectionStore = GetCollectionStore();
                
                var allPlaylists = await playlistStore.GetAllAsync().ConfigureAwait(false);
                var allCollections = await collectionStore.GetAllAsync().ConfigureAwait(false);
                
                // Filter playlists where the current user is in the UserPlaylists array or is the UserId owner
                var userPlaylists = allPlaylists.Where(p => IsUserInPlaylist(p, normalizedUserId)).Cast<SmartListDto>();
                
                // Filter collections where the current user is the owner
                var userCollections = allCollections.Where(c => 
                    !string.IsNullOrEmpty(c.UserId) && 
                    Guid.TryParse(c.UserId, out var collectionUserId) &&
                    collectionUserId.ToString("N").Equals(normalizedUserId, StringComparison.OrdinalIgnoreCase)
                ).Cast<SmartListDto>();
                
                // Merge both lists
                var allUserLists = userPlaylists.Concat(userCollections).ToList();
                
                return Ok(allUserLists);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving user playlists");
                return StatusCode(500, new { error = "Failed to retrieve playlists" });
            }
        }

        /// <summary>
        /// Checks if a user is associated with a playlist (handles both old UserId and new UserPlaylists formats).
        /// </summary>
        private static bool IsUserInPlaylist(SmartPlaylistDto playlist, string normalizedUserId)
        {
            // Check new format: UserPlaylists array
            if (playlist.UserPlaylists != null && playlist.UserPlaylists.Count > 0)
            {
                foreach (var mapping in playlist.UserPlaylists)
                {
                    if (Guid.TryParse(mapping.UserId, out var mappingUserId))
                    {
                        if (mappingUserId.ToString("N").Equals(normalizedUserId, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
            
            // Check old format: single UserId (backwards compatibility)
            if (!string.IsNullOrEmpty(playlist.UserId) && Guid.TryParse(playlist.UserId, out var playlistUserId))
            {
                return playlistUserId.ToString("N").Equals(normalizedUserId, StringComparison.OrdinalIgnoreCase);
            }
            
            return false;
        }

        /// <summary>
        /// Creates a new smart playlist for the current user.
        /// </summary>
        /// <param name="request">The playlist creation request.</param>
        /// <returns>The created smart playlist.</returns>
        [HttpPost("Playlists")]
        [ProducesResponseType(StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<SmartPlaylistDto>> CreateUserPlaylist([FromBody] CreatePlaylistRequest request)
        {
            try
            {
                var userId = GetCurrentUserId();
                var user = _userManager.GetUserById(userId);
                
                if (user == null)
                {
                    return BadRequest(new { error = "User not found" });
                }

                // Validate request
                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return BadRequest(new { error = "Playlist name is required" });
                }

                if (request.MediaTypes == null || request.MediaTypes.Count == 0)
                {
                    return BadRequest(new { error = "At least one media type must be selected" });
                }

                // Create the smart playlist DTO
                var smartPlaylistDto = new SmartPlaylistDto
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = request.Name,
                    UserId = userId.ToString(),
                    MediaTypes = request.MediaTypes,
                    ExpressionSets = ConvertRulesToExpressionSets(request.Rules),
                    MaxItems = 0,
                    MaxPlayTimeMinutes = 0,
                    Public = false
                };

                // Save to store
                var store = GetPlaylistStore();
                await store.SaveAsync(smartPlaylistDto).ConfigureAwait(false);

                _logger.LogInformation("User {UserId} created smart playlist '{Name}'", userId, request.Name);

                return CreatedAtAction(nameof(GetUserPlaylists), new { id = smartPlaylistDto.Id }, smartPlaylistDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating user playlist");
                return StatusCode(500, new { error = "Failed to create playlist" });
            }
        }

        /// <summary>
        /// Gets available fields for rules.
        /// </summary>
        [HttpGet("Fields")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<object> GetFields()
        {
            try
            {
                // Use the shared field definitions (DRY principle)
                return Ok(SharedFieldDefinitions.GetAvailableFields());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving fields");
                return StatusCode(500, new { error = "Failed to retrieve fields" });
            }
        }

        /// <summary>
        /// Gets the current user's capabilities for creating different list types.
        /// </summary>
        /// <returns>User capabilities including collection management permission.</returns>
        [HttpGet("Capabilities")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<object> GetUserCapabilities()
        {
            try
            {
                var canManageCollections = CanUserManageCollections();
                
                return Ok(new
                {
                    CanCreatePlaylists = true,  // All authenticated users can create playlists
                    CanCreateCollections = canManageCollections
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving user capabilities");
                return StatusCode(500, new { error = "Failed to retrieve capabilities" });
            }
        }

        /// <summary>
        /// Converts simple rules to ExpressionSets.
        /// </summary>
        private static List<ExpressionSet> ConvertRulesToExpressionSets(List<SimpleRuleGroup> ruleGroups)
        {
            var expressionSets = new List<ExpressionSet>();
            
            foreach (var ruleGroup in ruleGroups)
            {
                var expressions = new List<Expression>();
                
                foreach (var rule in ruleGroup.Rules)
                {
                    expressions.Add(new Expression(
                        rule.Field,
                        rule.Operator,
                        rule.Value ?? string.Empty));
                }
                
                if (expressions.Count > 0)
                {
                    expressionSets.Add(new ExpressionSet
                    {
                        Expressions = expressions,
                        MaxItems = null
                    });
                }
            }
            
            return expressionSets;
        }

        /// <summary>
        /// Request model for creating a playlist.
        /// </summary>
        public class CreatePlaylistRequest
        {
            [Required]
            public string Name { get; set; } = string.Empty;

            [Required]
            public List<string> MediaTypes { get; set; } = new();

            public List<SimpleRuleGroup> Rules { get; set; } = new();
        }

        /// <summary>
        /// Simple rule group model for user input.
        /// </summary>
        public class SimpleRuleGroup
        {
            public List<SimpleRule> Rules { get; set; } = new();
            public string GroupOperator { get; set; } = "And";
        }

        /// <summary>
        /// Simple rule model for user input.
        /// </summary>
        public class SimpleRule
        {
            [Required]
            public string Field { get; set; } = string.Empty;
            
            [Required]
            public string Operator { get; set; } = string.Empty;
            
            public string? Value { get; set; }
        }

        /// <summary>
        /// Refreshes all smart lists (playlists and collections) for the current user.
        /// </summary>
        /// <returns>Success message.</returns>
        [HttpPost("refresh-direct")]
        public async Task<ActionResult> RefreshUserLists()
        {
            try
            {
                var userId = GetCurrentUserId();
                var normalizedUserId = userId.ToString("N");
                var playlistStore = GetPlaylistStore();
                var collectionStore = GetCollectionStore();
                
                // Get all playlists for this user
                var allPlaylists = await playlistStore.GetAllAsync();
                var playlists = allPlaylists
                    .Where(p => IsUserInPlaylist(p, normalizedUserId))
                    .ToList();
                
                // Get all collections for this user
                var allCollections = await collectionStore.GetAllAsync();
                var collections = allCollections
                    .Where(c => c.UserId != null && Guid.TryParse(c.UserId, out var cUserId) && cUserId == userId)
                    .ToList();
                
                var totalLists = playlists.Count + collections.Count;
                
                if (totalLists == 0)
                {
                    return Ok(new { message = "No lists found for the current user." });
                }
                
                // Queue refresh operations for all user's lists
                int queuedCount = 0;
                
#pragma warning disable CS8601 // Possible null reference assignment
                foreach (var playlist in playlists)
                {
                    _refreshQueueService.EnqueueOperation(new RefreshQueueItem
                    {
                        ListId = playlist.Id,
                        ListName = playlist.Name ?? "Unknown",
                        ListType = playlist.Type,
                        OperationType = RefreshOperationType.Refresh,
                        UserId = userId.ToString(),
                        TriggerType = RefreshTriggerType.Manual,
                        QueuedAt = DateTime.UtcNow
                    });
                    queuedCount++;
                }
                
                foreach (var collection in collections)
                {
                    _refreshQueueService.EnqueueOperation(new RefreshQueueItem
                    {
                        ListId = collection.Id,
                        ListName = collection.Name ?? "Unknown",
                        ListType = collection.Type,
                        OperationType = RefreshOperationType.Refresh,
                        UserId = userId.ToString(),
                        TriggerType = RefreshTriggerType.Manual,
                        QueuedAt = DateTime.UtcNow
                    });
                    queuedCount++;
                }
#pragma warning restore CS8601
                
                _logger.LogInformation("Queued {QueuedCount} list(s) for refresh for user {UserId}", queuedCount, userId);
                
                return Ok(new
                {
                    message = $"Queued {queuedCount} list(s) for refresh. They will be processed in the background.",
                    queuedCount = queuedCount
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error queueing user lists for refresh");
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Error queueing lists for refresh" });
            }
        }

        /// <summary>
        /// Enable a smart list (playlist or collection) for the current user.
        /// </summary>
        /// <param name="id">The list ID.</param>
        /// <returns>Success message.</returns>
        [HttpPost("{id}/enable")]
        public async Task<ActionResult> EnableSmartList([FromRoute, Required] string id)
        {
            try
            {
                var userId = GetCurrentUserId();
                var normalizedUserId = userId.ToString("N");

                if (!Guid.TryParse(id, out var guidId))
                {
                    return BadRequest(new { message = "Invalid list ID format" });
                }

                // Try playlist first
                var playlistStore = GetPlaylistStore();
                var playlist = await playlistStore.GetByIdAsync(guidId);
                if (playlist != null)
                {
                    // Verify user owns this playlist
                    if (!IsUserInPlaylist(playlist, normalizedUserId))
                    {
                        return Forbid();
                    }

                    var originalEnabledState = playlist.Enabled;
                    playlist.Enabled = true;

                    try
                    {
                        await playlistStore.SaveAsync(playlist);
                        Services.Shared.AutoRefreshService.Instance?.UpdatePlaylistInCache(playlist);

                        // Enqueue refresh operation
                        try
                        {
#pragma warning disable CS8601 // Possible null reference assignment
                            var queueItem = new RefreshQueueItem
                            {
                                ListId = playlist.Id,
                                ListName = playlist.Name ?? "Unknown",
                                ListType = playlist.Type,
                                OperationType = RefreshOperationType.Refresh,
                                ListData = playlist,
                                UserId = playlist.UserId,
                                TriggerType = RefreshTriggerType.Manual,
                                QueuedAt = DateTime.UtcNow
                            };
#pragma warning restore CS8601

                            _refreshQueueService.EnqueueOperation(queueItem);
                        }
                        catch (Exception enqueueEx)
                        {
                            _logger.LogWarning(enqueueEx, "Failed to enqueue refresh for enabled playlist '{Name}', but enable succeeded", playlist.Name);
                        }

                        _logger.LogInformation("Enabled smart playlist: {PlaylistId} - {PlaylistName}", id, playlist.Name);
                        return Ok(new { message = $"Smart playlist '{playlist.Name}' has been enabled" });
                    }
                    catch (Exception ex)
                    {
                        playlist.Enabled = originalEnabledState;
                        _logger.LogError(ex, "Failed to enable Jellyfin playlist for {PlaylistId} - {PlaylistName}", id, playlist.Name);
                        throw;
                    }
                }

                // Try collection
                var collectionStore = GetCollectionStore();
                var collection = await collectionStore.GetByIdAsync(guidId);
                if (collection != null)
                {
                    // Verify user owns this collection
                    if (collection.UserId != null && Guid.TryParse(collection.UserId, out var cUserId) && cUserId != userId)
                    {
                        return Forbid();
                    }

                    var originalEnabledState = collection.Enabled;
                    collection.Enabled = true;

                    try
                    {
                        await collectionStore.SaveAsync(collection);
                        Services.Shared.AutoRefreshService.Instance?.UpdateCollectionInCache(collection);

                        // Enqueue refresh operation
                        try
                        {
#pragma warning disable CS8601 // Possible null reference assignment
                            var queueItem = new RefreshQueueItem
                            {
                                ListId = collection.Id,
                                ListName = collection.Name ?? "Unknown",
                                ListType = collection.Type,
                                OperationType = RefreshOperationType.Refresh,
                                ListData = collection,
                                UserId = collection.UserId,
                                TriggerType = RefreshTriggerType.Manual,
                                QueuedAt = DateTime.UtcNow
                            };
#pragma warning restore CS8601

                            _refreshQueueService.EnqueueOperation(queueItem);
                        }
                        catch (Exception enqueueEx)
                        {
                            _logger.LogWarning(enqueueEx, "Failed to enqueue refresh for enabled collection '{Name}', but enable succeeded", collection.Name);
                        }

                        _logger.LogInformation("Enabled smart collection: {CollectionId} - {CollectionName}", id, collection.Name);
                        return Ok(new { message = $"Smart collection '{collection.Name}' has been enabled" });
                    }
                    catch (Exception ex)
                    {
                        collection.Enabled = originalEnabledState;
                        _logger.LogError(ex, "Failed to enable Jellyfin collection for {CollectionId} - {CollectionName}", id, collection.Name);
                        throw;
                    }
                }

                return NotFound(new { message = "Smart list not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enabling smart list {ListId}", id);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Error enabling smart list" });
            }
        }

        /// <summary>
        /// Disable a smart list (playlist or collection) for the current user.
        /// </summary>
        /// <param name="id">The list ID.</param>
        /// <returns>Success message.</returns>
        [HttpPost("{id}/disable")]
        public async Task<ActionResult> DisableSmartList([FromRoute, Required] string id)
        {
            try
            {
                var userId = GetCurrentUserId();
                var normalizedUserId = userId.ToString("N");

                if (!Guid.TryParse(id, out var guidId))
                {
                    return BadRequest(new { message = "Invalid list ID format" });
                }

                // Try playlist first
                var playlistStore = GetPlaylistStore();
                var playlist = await playlistStore.GetByIdAsync(guidId);
                if (playlist != null)
                {
                    // Verify user owns this playlist
                    if (!IsUserInPlaylist(playlist, normalizedUserId))
                    {
                        return Forbid();
                    }

                    var originalEnabledState = playlist.Enabled;
                    playlist.Enabled = false;

                    try
                    {
                        var playlistService = GetPlaylistService();
                        await playlistService.DeleteAllJellyfinPlaylistsForUsersAsync(playlist);

                        playlist.JellyfinPlaylistId = null;
                        if (playlist.UserPlaylists != null)
                        {
                            foreach (var userMapping in playlist.UserPlaylists)
                            {
                                userMapping.JellyfinPlaylistId = null;
                            }
                        }

                        await playlistStore.SaveAsync(playlist);
                        Services.Shared.AutoRefreshService.Instance?.UpdatePlaylistInCache(playlist);

                        _logger.LogInformation("Disabled smart playlist: {PlaylistId} - {PlaylistName}", id, playlist.Name);
                        return Ok(new { message = $"Smart playlist '{playlist.Name}' has been disabled" });
                    }
                    catch (Exception ex)
                    {
                        playlist.Enabled = originalEnabledState;
                        _logger.LogError(ex, "Failed to disable Jellyfin playlist for {PlaylistId} - {PlaylistName}", id, playlist.Name);
                        throw;
                    }
                }

                // Try collection
                var collectionStore = GetCollectionStore();
                var collection = await collectionStore.GetByIdAsync(guidId);
                if (collection != null)
                {
                    // Verify user owns this collection
                    if (collection.UserId != null && Guid.TryParse(collection.UserId, out var cUserId) && cUserId != userId)
                    {
                        return Forbid();
                    }

                    var originalEnabledState = collection.Enabled;
                    collection.Enabled = false;

                    try
                    {
                        var collectionService = GetCollectionService();
                        await collectionService.DisableAsync(collection);

                        collection.JellyfinCollectionId = null;
                        await collectionStore.SaveAsync(collection);
                        Services.Shared.AutoRefreshService.Instance?.UpdateCollectionInCache(collection);

                        _logger.LogInformation("Disabled smart collection: {CollectionId} - {CollectionName}", id, collection.Name);
                        return Ok(new { message = $"Smart collection '{collection.Name}' has been disabled" });
                    }
                    catch (Exception ex)
                    {
                        collection.Enabled = originalEnabledState;
                        _logger.LogError(ex, "Failed to disable Jellyfin collection for {CollectionId} - {CollectionName}", id, collection.Name);
                        throw;
                    }
                }

                return NotFound(new { message = "Smart list not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disabling smart list {ListId}", id);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Error disabling smart list" });
            }
        }

        /// <summary>
        /// Delete a smart list (playlist or collection) for the current user.
        /// </summary>
        /// <param name="id">The list ID.</param>
        /// <param name="deleteJellyfinList">Whether to delete the actual Jellyfin list or just the configuration.</param>
        /// <returns>No content on success.</returns>
        [HttpDelete("{id}")]
        public async Task<ActionResult> DeleteSmartList([FromRoute, Required] string id, [FromQuery] bool deleteJellyfinList = true)
        {
            try
            {
                var userId = GetCurrentUserId();
                var normalizedUserId = userId.ToString("N");

                if (!Guid.TryParse(id, out var guidId))
                {
                    return BadRequest(new { message = "Invalid list ID format" });
                }

                // Try playlist first
                var playlistStore = GetPlaylistStore();
                var playlist = await playlistStore.GetByIdAsync(guidId);
                if (playlist != null)
                {
                    // Verify user owns this playlist
                    if (!IsUserInPlaylist(playlist, normalizedUserId))
                    {
                        return Forbid();
                    }

                    if (deleteJellyfinList)
                    {
                        var playlistService = GetPlaylistService();
                        await playlistService.DeleteAllJellyfinPlaylistsForUsersAsync(playlist);
                        _logger.LogInformation("Deleted smart playlist: {PlaylistName}", playlist.Name);
                    }

                    await playlistStore.DeleteAsync(guidId).ConfigureAwait(false);
                    Services.Shared.AutoRefreshService.Instance?.RemovePlaylistFromCache(id);
                    return NoContent();
                }

                // Try collection
                var collectionStore = GetCollectionStore();
                var collection = await collectionStore.GetByIdAsync(guidId);
                if (collection != null)
                {
                    // Verify user owns this collection
                    if (collection.UserId != null && Guid.TryParse(collection.UserId, out var cUserId) && cUserId != userId)
                    {
                        return Forbid();
                    }

                    var collectionService = GetCollectionService();
                    if (deleteJellyfinList)
                    {
                        await collectionService.DeleteAsync(collection);
                        _logger.LogInformation("Deleted smart collection: {CollectionName}", collection.Name);
                    }

                    await collectionStore.DeleteAsync(guidId).ConfigureAwait(false);
                    Services.Shared.AutoRefreshService.Instance?.RemoveCollectionFromCache(id);
                    return NoContent();
                }

                return NotFound(new { message = "Smart list not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting smart list {ListId}", id);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Error deleting smart list" });
            }
        }

        private Services.Collections.CollectionService GetCollectionService()
        {
            var loggerFactory = new LoggerFactory();
            var collectionServiceLogger = loggerFactory.CreateLogger<Services.Collections.CollectionService>();
            return new Services.Collections.CollectionService(
                _libraryManager,
                _collectionManager,
                _userManager,
                _userDataManager,
                collectionServiceLogger,
                _providerManager);
        }
    }

    /// <summary>
    /// Extension methods for user claims.
    /// </summary>
    internal static class UserExtensions
    {
        public static Guid GetUserId(this System.Security.Claims.ClaimsPrincipal user)
        {
            // Jellyfin uses the "Jellyfin-UserId" claim for authenticated users
            var userIdClaim = user.FindFirst("Jellyfin-UserId");
            if (userIdClaim != null && Guid.TryParse(userIdClaim.Value, out var userId))
            {
                return userId;
            }
            return Guid.Empty;
        }
    }
}
