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
using static Jellyfin.Plugin.SmartLists.Utilities.InputValidator;

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
        /// Checks if the user page feature is enabled in configuration.
        /// </summary>
        private static bool IsUserPageEnabled()
        {
            var config = Plugin.Instance?.Configuration;
            return config?.EnableUserPage ?? true; // Default to enabled if config not available
        }

        /// <summary>
        /// Checks if the current user is an administrator.
        /// </summary>
        private bool IsCurrentUserAdmin()
        {
            var userId = GetCurrentUserId();
            var user = _userManager.GetUserById(userId);
            return user?.HasPermission(PermissionKind.IsAdministrator) ?? false;
        }

        /// <summary>
        /// Verifies that the user page is enabled and the current user has access.
        /// Admins always have access. For non-admins:
        /// - User page must be enabled
        /// - If AllowedUserPageUsers is empty/null, all users have access
        /// - If AllowedUserPageUsers is populated, user must be in the list
        /// Returns Forbidden if access is denied.
        /// </summary>
        private ActionResult? CheckUserPageAccess()
        {
            // Admins always have access, even if user page is disabled
            if (IsCurrentUserAdmin())
            {
                return null;
            }

            // For non-admin users, check if user page is enabled
            if (!IsUserPageEnabled())
            {
                return StatusCode(StatusCodes.Status403Forbidden, new 
                { 
                    message = "The user page is currently disabled by an administrator. Please contact your administrator for access to SmartLists." 
                });
            }

            // Check if specific users are allowed
            var config = Plugin.Instance?.Configuration;
            var allowedUsers = config?.AllowedUserPageUsers;
            
            // If list is populated, check if current user is in it
            if (allowedUsers != null && allowedUsers.Count > 0)
            {
                // Format GUID without dashes to match how it's stored in configuration
                var userId = GetCurrentUserId().ToString("N");
                if (!allowedUsers.Contains(userId))
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new 
                    { 
                        message = "You do not have permission to access the SmartLists user page. Please contact your administrator for access." 
                    });
                }
            }

            return null;
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
            // Check if user page is enabled
            var accessCheck = CheckUserPageAccess();
            if (accessCheck != null) return accessCheck;

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
            // Check if user page is enabled
            var accessCheck = CheckUserPageAccess();
            if (accessCheck != null) return accessCheck;

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

                // ===== SECURITY VALIDATION =====
                // Validate the entire smart list structure
                var validationResult = ValidateSmartList(list);
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning("Validation failed for user {UserId} creating list '{Name}': {Error}", 
                        userId, list.Name, validationResult.ErrorMessage);
                    return BadRequest(new { error = validationResult.ErrorMessage });
                }

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

                // Validate required fields (after input validation)
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
                        DateCreated = DateTime.UtcNow,
                        AutoRefresh = list.AutoRefresh,
                        Schedules = list.Schedules,
                        VisibilitySchedules = list.VisibilitySchedules
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
            // Check if user page is enabled
            var accessCheck = CheckUserPageAccess();
            if (accessCheck != null) return accessCheck;

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
        /// Gets a specific smart list (playlist or collection) by ID for the current user.
        /// </summary>
        /// <param name="id">The list ID.</param>
        /// <returns>The smart list.</returns>
        [HttpGet("{id}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<SmartListDto>> GetSmartList([FromRoute, Required] string id)
        {
            // Check if user page is enabled
            var accessCheck = CheckUserPageAccess();
            if (accessCheck != null) return accessCheck;

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

                    playlist.MigrateLegacyFields();
                    return Ok(playlist);
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

                    collection.MigrateLegacyFields();
                    return Ok(collection);
                }

                return NotFound(new { message = "Smart list not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving smart list {ListId}", id);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Error retrieving smart list" });
            }
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
            // Check if user page is enabled
            var accessCheck = CheckUserPageAccess();
            if (accessCheck != null) return accessCheck;

            try
            {
                var userId = GetCurrentUserId();
                var user = _userManager.GetUserById(userId);
                
                if (user == null)
                {
                    return BadRequest(new { error = "User not found" });
                }

                // ===== SECURITY VALIDATION =====
                // Validate playlist name
                var nameValidation = ValidateName(request.Name);
                if (!nameValidation.IsValid)
                {
                    _logger.LogWarning("Validation failed for user {UserId} creating playlist: {Error}", 
                        userId, nameValidation.ErrorMessage);
                    return BadRequest(new { error = nameValidation.ErrorMessage });
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

                // Validate media types count
                if (request.MediaTypes.Count > 50)
                {
                    return BadRequest(new { error = "Cannot select more than 50 media types" });
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

                // Validate the complete DTO with security checks
                var validationResult = ValidateSmartList(smartPlaylistDto);
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning("Validation failed for user {UserId} creating playlist '{Name}': {Error}", 
                        userId, request.Name, validationResult.ErrorMessage);
                    return BadRequest(new { error = validationResult.ErrorMessage });
                }

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
            // Check if user page is enabled
            var accessCheck = CheckUserPageAccess();
            if (accessCheck != null) return accessCheck;

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
            // Check if user page is enabled
            var accessCheck = CheckUserPageAccess();
            if (accessCheck != null) return accessCheck;

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
            // Check if user page is enabled
            var accessCheck = CheckUserPageAccess();
            if (accessCheck != null) return accessCheck;

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
                
                foreach (var playlist in playlists)
                {
                    EnqueueRefreshOperation(playlist.Id, playlist.Name, playlist.Type, playlist, playlist.UserId);
                    queuedCount++;
                }
                
                foreach (var collection in collections)
                {
                    EnqueueRefreshOperation(collection.Id, collection.Name, collection.Type, collection, collection.UserId);
                    queuedCount++;
                }
                
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
            // Check if user page is enabled
            var accessCheck = CheckUserPageAccess();
            if (accessCheck != null) return accessCheck;

            try
            {
                return await ExecuteListAction(
                    id,
                    async playlist =>
                    {
                        var playlistStore = GetPlaylistStore();
                        var originalEnabledState = playlist.Enabled;
                        playlist.Enabled = true;

                        try
                        {
                            await playlistStore.SaveAsync(playlist);
                            Services.Shared.AutoRefreshService.Instance?.UpdatePlaylistInCache(playlist);

                            // Enqueue refresh operation
                            try
                            {
                                EnqueueRefreshOperation(playlist.Id, playlist.Name, playlist.Type, playlist, playlist.UserId);
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
                    },
                    async collection =>
                    {
                        var collectionStore = GetCollectionStore();
                        var originalEnabledState = collection.Enabled;
                        collection.Enabled = true;

                        try
                        {
                            await collectionStore.SaveAsync(collection);
                            Services.Shared.AutoRefreshService.Instance?.UpdateCollectionInCache(collection);

                            // Enqueue refresh operation
                            try
                            {
                                EnqueueRefreshOperation(collection.Id, collection.Name, collection.Type, collection, collection.UserId);
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
                    });
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
            // Check if user page is enabled
            var accessCheck = CheckUserPageAccess();
            if (accessCheck != null) return accessCheck;

            try
            {
                return await ExecuteListAction(
                    id,
                    async playlist =>
                    {
                        var playlistStore = GetPlaylistStore();
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
                    },
                    async collection =>
                    {
                        var collectionStore = GetCollectionStore();
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
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disabling smart list {ListId}", id);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Error disabling smart list" });
            }
        }

        /// <summary>
        /// Update a smart list (playlist or collection) for the current user.
        /// </summary>
        /// <param name="id">The list ID.</param>
        /// <param name="list">The updated smart list.</param>
        /// <returns>The updated smart list.</returns>
        [HttpPut("{id}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<SmartListDto>> UpdateSmartList([FromRoute, Required] string id, [FromBody, Required] SmartListDto list)
        {
            // Check if user page is enabled
            var accessCheck = CheckUserPageAccess();
            if (accessCheck != null) return accessCheck;
            if (list == null)
            {
                return BadRequest(new { message = "List data is required" });
            }

            try
            {
                var userId = GetCurrentUserId();
                var normalizedUserId = userId.ToString("N");

                if (!Guid.TryParse(id, out var guidId))
                {
                    return BadRequest(new { message = "Invalid list ID format" });
                }

                // Ensure the list ID matches
                if (list.Id != id)
                {
                    list.Id = id;
                }

                // Determine if it's a playlist or collection and update accordingly
                var playlistStore = GetPlaylistStore();
                var existingPlaylist = await playlistStore.GetByIdAsync(guidId);
                
                if (existingPlaylist != null)
                {
                    // Verify user owns this playlist
                    if (!IsUserInPlaylist(existingPlaylist, normalizedUserId))
                    {
                        return Forbid();
                    }

                    // Convert to SmartPlaylistDto
                    var playlistDto = list as SmartPlaylistDto;
                    if (playlistDto == null)
                    {
                        return BadRequest(new { message = "Invalid playlist data" });
                    }

                    // Preserve the original filename and user ownership
                    playlistDto.FileName = existingPlaylist.FileName;
                    playlistDto.UserId = existingPlaylist.UserId;
                    playlistDto.UserPlaylists = existingPlaylist.UserPlaylists;

                    // Save updated playlist
                    await playlistStore.SaveAsync(playlistDto);
                    
                    // Update cache
                    Services.Shared.AutoRefreshService.Instance?.UpdatePlaylistInCache(playlistDto);

                    // Clear rule cache
                    SmartList.ClearRuleCache(_logger);

                    // Enqueue refresh if enabled
                    if (playlistDto.Enabled)
                    {
                        try
                        {
                            EnqueueRefreshOperation(playlistDto.Id, playlistDto.Name, playlistDto.Type, playlistDto, playlistDto.UserId);
                        }
                        catch (Exception enqueueEx)
                        {
                            _logger.LogWarning(enqueueEx, "Failed to enqueue refresh for updated playlist '{Name}', but update succeeded", playlistDto.Name);
                        }
                    }

                    _logger.LogInformation("User {UserId} updated smart playlist '{Name}'", userId, playlistDto.Name);
                    return Ok(playlistDto);
                }

                // Try collection
                var collectionStore = GetCollectionStore();
                var existingCollection = await collectionStore.GetByIdAsync(guidId);
                
                if (existingCollection != null)
                {
                    // Verify user owns this collection
                    if (existingCollection.UserId != null && Guid.TryParse(existingCollection.UserId, out var cUserId) && cUserId != userId)
                    {
                        return Forbid();
                    }

                    // Convert to SmartCollectionDto if needed
                    var collectionDto = list as SmartCollectionDto;
                    if (collectionDto == null && list.Type == Core.Enums.SmartListType.Collection)
                    {
                        collectionDto = new SmartCollectionDto
                        {
                            Id = list.Id,
                            Name = list.Name,
                            Type = Core.Enums.SmartListType.Collection,
                            UserId = existingCollection.UserId,
                            MediaTypes = list.MediaTypes,
                            ExpressionSets = list.ExpressionSets,
                            Order = list.Order,
                            Enabled = list.Enabled,
                            MaxItems = list.MaxItems,
                            MaxPlayTimeMinutes = list.MaxPlayTimeMinutes,
                            DateCreated = existingCollection.DateCreated,
                            FileName = existingCollection.FileName,
                            AutoRefresh = list.AutoRefresh,
                            Schedules = list.Schedules,
                            VisibilitySchedules = list.VisibilitySchedules
                        };
                    }

                    if (collectionDto != null)
                    {
                        // Preserve the original filename and user ownership
                        collectionDto.FileName = existingCollection.FileName;
                        collectionDto.UserId = existingCollection.UserId;

                        // Save updated collection
                        await collectionStore.SaveAsync(collectionDto);
                        
                        // Update cache
                        Services.Shared.AutoRefreshService.Instance?.UpdateCollectionInCache(collectionDto);

                        // Clear rule cache
                        SmartList.ClearRuleCache(_logger);

                        // Enqueue refresh if enabled
                        if (collectionDto.Enabled)
                        {
                            try
                            {
                                EnqueueRefreshOperation(collectionDto.Id, collectionDto.Name, collectionDto.Type, collectionDto, collectionDto.UserId);
                            }
                            catch (Exception enqueueEx)
                            {
                                _logger.LogWarning(enqueueEx, "Failed to enqueue refresh for updated collection '{Name}', but update succeeded", collectionDto.Name);
                            }
                        }

                        _logger.LogInformation("User {UserId} updated smart collection '{Name}'", userId, collectionDto.Name);
                        return Ok(collectionDto);
                    }

                    return BadRequest(new { message = "Invalid collection data" });
                }

                return NotFound(new { message = "Smart list not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating smart list {ListId}", id);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Error updating smart list" });
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
            // Check if user page is enabled
            var accessCheck = CheckUserPageAccess();
            if (accessCheck != null) return accessCheck;

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

        /// <summary>
        /// Trigger a refresh of a specific smart list (playlist or collection) for the current user.
        /// </summary>
        /// <param name="id">The list ID.</param>
        /// <returns>Success message.</returns>
        [HttpPost("{id}/refresh")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> RefreshSmartList([FromRoute, Required] string id)
        {
            // Check if user page is enabled
            var accessCheck = CheckUserPageAccess();
            if (accessCheck != null) return accessCheck;

            try
            {
                var userId = GetCurrentUserId();

                return await ExecuteListAction(
                    id,
                    playlist =>
                    {
                        try
                        {
                            EnqueueRefreshOperation(playlist.Id, playlist.Name, playlist.Type, playlist, playlist.UserId);
                            _logger.LogInformation("User {UserId} enqueued refresh for playlist '{Name}'", userId, playlist.Name);
                            return Task.FromResult<ActionResult>(Ok(new { message = $"Playlist '{playlist.Name}' refresh has been queued" }));
                        }
                        catch (Exception enqueueEx)
                        {
                            _logger.LogError(enqueueEx, "Failed to enqueue refresh for playlist '{Name}'", playlist.Name);
                            return Task.FromResult<ActionResult>(StatusCode(StatusCodes.Status500InternalServerError, new { message = "Failed to queue playlist refresh" }));
                        }
                    },
                    collection =>
                    {
                        try
                        {
                            EnqueueRefreshOperation(collection.Id, collection.Name, collection.Type, collection, collection.UserId);
                            _logger.LogInformation("User {UserId} enqueued refresh for collection '{Name}'", userId, collection.Name);
                            return Task.FromResult<ActionResult>(Ok(new { message = $"Collection '{collection.Name}' refresh has been queued" }));
                        }
                        catch (Exception enqueueEx)
                        {
                            _logger.LogError(enqueueEx, "Failed to enqueue refresh for collection '{Name}'", collection.Name);
                            return Task.FromResult<ActionResult>(StatusCode(StatusCodes.Status500InternalServerError, new { message = "Failed to queue collection refresh" }));
                        }
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing smart list {ListId}", id);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Error refreshing smart list" });
            }
        }

        /// <summary>
        /// Helper method to find a smart list (playlist or collection) and execute an action on it.
        /// This reduces code duplication across Enable, Disable, and Refresh operations.
        /// </summary>
        /// <param name="id">The list ID.</param>
        /// <param name="playlistAction">Action to perform on a playlist.</param>
        /// <param name="collectionAction">Action to perform on a collection.</param>
        /// <returns>ActionResult from the executed action.</returns>
        private async Task<ActionResult> ExecuteListAction(
            string id,
            Func<SmartPlaylistDto, Task<ActionResult>> playlistAction,
            Func<SmartCollectionDto, Task<ActionResult>> collectionAction)
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

                return await playlistAction(playlist);
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

                return await collectionAction(collection);
            }

            return NotFound(new { message = "Smart list not found" });
        }

        /// <summary>
        /// Helper method to enqueue a refresh operation for a list.
        /// </summary>
        private void EnqueueRefreshOperation(string? listId, string listName, Core.Enums.SmartListType listType, SmartListDto? listData, string? userId)
        {
#pragma warning disable CS8601 // Possible null reference assignment
            var queueItem = new RefreshQueueItem
            {
                ListId = listId,
                ListName = listName ?? "Unknown",
                ListType = listType,
                OperationType = RefreshOperationType.Refresh,
                ListData = listData,
                UserId = userId,
                TriggerType = RefreshTriggerType.Manual,
                QueuedAt = DateTime.UtcNow
            };
#pragma warning restore CS8601

            _refreshQueueService.EnqueueOperation(queueItem);
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
