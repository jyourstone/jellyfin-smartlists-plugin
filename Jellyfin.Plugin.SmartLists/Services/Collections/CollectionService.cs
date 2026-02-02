using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists;
using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Constants;
using Jellyfin.Plugin.SmartLists.Core.Enums;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Services.Abstractions;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace Jellyfin.Plugin.SmartLists.Services.Collections
{
    /// <summary>
    /// Service for handling individual smart collection operations.
    /// Implements ISmartListService for collections.
    /// </summary>
    public class CollectionService : ISmartListService<SmartCollectionDto>
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ICollectionManager _collectionManager;
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ILogger<CollectionService> _logger;
        private readonly IProviderManager _providerManager;
        private readonly SmartListImageService? _imageService;

        public CollectionService(
            ILibraryManager libraryManager,
            ICollectionManager collectionManager,
            IUserManager userManager,
            IUserDataManager userDataManager,
            ILogger<CollectionService> logger,
            IProviderManager providerManager,
            SmartListImageService? imageService = null)
        {
            _libraryManager = libraryManager;
            _collectionManager = collectionManager;
            _userManager = userManager;
            _userDataManager = userDataManager;
            _logger = logger;
            _providerManager = providerManager;
            _imageService = imageService;
        }

        /// <summary>
        /// Gets all user media for a collection, filtered by media types.
        /// Uses the owner user context to query media items.
        /// </summary>
        public IEnumerable<BaseItem> GetAllUserMediaForPlaylist(User user, List<string> mediaTypes, SmartCollectionDto? dto = null)
        {
            // Validate media types before processing
            if (dto != null)
            {
                _logger?.LogDebug("GetAllUserMediaForPlaylist validation for '{CollectionName}': MediaTypes={MediaTypes}", dto.Name, mediaTypes != null ? string.Join(",", mediaTypes) : "null");

                if (mediaTypes == null || mediaTypes.Count == 0)
                {
                    _logger?.LogError("Smart collection '{CollectionName}' has no media types specified. At least one media type must be selected.", dto.Name);
                    throw new InvalidOperationException("No media types specified. At least one media type must be selected.");
                }
            }

            // Use GetAllMedia which queries media in the owner user's context
            return GetAllMedia(mediaTypes, dto, user);
        }

        /// <summary>
        /// Processes a collection refresh with pre-cached media for efficient batch processing.
        /// Implements ISmartListService interface (generic method name for both playlists and collections).
        /// </summary>
        public async Task<(bool Success, string Message, string JellyfinPlaylistId)> ProcessPlaylistRefreshWithCachedMediaAsync(
            SmartCollectionDto dto,
            User user,
            BaseItem[] allUserMedia,
            RefreshQueueService.RefreshCache refreshCache,
            Func<SmartCollectionDto, Task>? saveCallback = null,
            Action<int, int>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(dto);
            ArgumentNullException.ThrowIfNull(user);
            ArgumentNullException.ThrowIfNull(allUserMedia);
            ArgumentNullException.ThrowIfNull(refreshCache);

            var (success, message, collectionId) = await ProcessCollectionRefreshAsync(dto, user, allUserMedia, refreshCache, progressCallback, cancellationToken);

            // Update LastRefreshed timestamp for successful refreshes (any trigger)
            // Note: For new collections, LastRefreshed was already set in ProcessCollectionRefreshAsync,
            // but we update it here to ensure it reflects the exact completion time of the refresh operation.
            if (success)
            {
                dto.LastRefreshed = DateTime.UtcNow;
                _logger.LogDebug("Updated LastRefreshed timestamp for cached collection: {CollectionName}", dto.Name);
                
                // Call save callback if provided
                if (saveCallback != null)
                {
                    await saveCallback(dto);
                }
            }

            return (success, message, collectionId);
        }

        public async Task<(bool Success, string Message, string Id)> RefreshAsync(SmartCollectionDto dto, Action<int, int>? progressCallback = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(dto);

            var stopwatch = Stopwatch.StartNew();
            try
            {
                _logger.LogDebug("Refreshing single smart collection: {CollectionName}", dto.Name);
                _logger.LogDebug("CollectionService.RefreshAsync called with: Name={Name}, Enabled={Enabled}, ExpressionSets={ExpressionSetCount}, MediaTypes={MediaTypes}",
                    dto.Name, dto.Enabled, dto.ExpressionSets?.Count ?? 0,
                    dto.MediaTypes != null ? string.Join(",", dto.MediaTypes) : "None");

                // Validate media types before processing
                _logger.LogDebug("Validating media types for collection '{CollectionName}': {MediaTypes}", dto.Name, dto.MediaTypes != null ? string.Join(",", dto.MediaTypes) : "null");

                // Note: Series media type is supported in Collections (unlike Playlists where it causes playback issues)
                
                if (dto.MediaTypes == null || dto.MediaTypes.Count == 0)
                {
                    _logger.LogError("Smart collection '{CollectionName}' has no media types specified. At least one media type must be selected. Skipping collection refresh.", dto.Name);
                    return (false, "No media types specified. At least one media type must be selected.", string.Empty);
                }

                // Check if collection is enabled
                if (!dto.Enabled)
                {
                    _logger.LogDebug("Smart collection '{CollectionName}' is disabled. Skipping refresh.", dto.Name);
                    return (true, "Collection is disabled", string.Empty);
                }

                // Collections use an owner user for rule context (IsPlayed, IsFavorite, etc.)
                // The collection is server-wide (visible to all), but rules evaluate in the owner's context
                // and the query must be executed in the owner's context to get the correct media items
                if (!Guid.TryParse(dto.UserId, out var ownerUserId) || ownerUserId == Guid.Empty)
                {
                    _logger.LogError("Collection owner user ID is invalid or empty: {User}", dto.UserId);
                    return (false, $"Collection owner user is required. Please set a valid owner.", string.Empty);
                }
                
                var ownerUser = _userManager.GetUserById(ownerUserId);
                
                if (ownerUser == null)
                {
                    _logger.LogError("Collection owner user {User} not found - cannot filter collection items", dto.UserId);
                    return (false, $"Collection owner user not found. Please set a valid owner.", string.Empty);
                }

                // Get all media items using the owner user's context
                var allMedia = GetAllMedia(dto.MediaTypes, dto, ownerUser).ToArray();
                _logger.LogDebug("Found {MediaCount} total media items for collection using owner user {OwnerUsername}", allMedia.Length, ownerUser.Username);

                // Create a temporary RefreshCache for this refresh (fallback path when queue service unavailable)
                var refreshCache = new RefreshQueueService.RefreshCache();

                // Process collection refresh with the media
                return await ProcessCollectionRefreshAsync(dto, ownerUser, allMedia, refreshCache, progressCallback, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing collection refresh for '{CollectionName}': {ErrorMessage}", dto.Name, ex.Message);
                return (false, $"Error processing collection '{dto.Name}': {ex.Message}", string.Empty);
            }
            finally
            {
                stopwatch.Stop();
                _logger.LogDebug("Collection refresh completed in {ElapsedMs}ms: {CollectionName}", stopwatch.ElapsedMilliseconds, dto.Name);
            }
        }

        /// <summary>
        /// Core method to process a collection refresh with provided media items.
        /// This is the shared logic used by both RefreshAsync and ProcessCollectionRefreshWithCachedMediaAsync.
        /// </summary>
        private async Task<(bool Success, string Message, string Id)> ProcessCollectionRefreshAsync(
            SmartCollectionDto dto,
            User ownerUser,
            BaseItem[] allMedia,
            RefreshQueueService.RefreshCache refreshCache,
            Action<int, int>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
                var smartCollection = new Core.SmartList(dto)
                {
                    UserManager = _userManager // Set UserManager for Jellyfin 10.11+ user resolution
                };

                // Query for collections if IncludeCollectionOnly is enabled
                var allCollections = QueryIncludeOnlyItems(dto, "Collections", BaseItemKind.BoxSet, ownerUser, "collection");

                // Query for playlists if IncludePlaylistOnly is enabled
                var allPlaylists = QueryIncludeOnlyItems(dto, "Playlists", BaseItemKind.Playlist, ownerUser, "playlist");

                // Log the collection rules
                _logger.LogDebug("Processing collection {CollectionName} with {RuleSetCount} rule sets (Owner: {OwnerUser})", 
                    dto.Name, dto.ExpressionSets?.Count ?? 0, ownerUser.Username);
                
                // Report initial total items count
                progressCallback?.Invoke(0, allMedia.Length);
                
                // Use owner's user data manager for user-specific filtering (IsPlayed, IsFavorite, etc.)
                var newItems = smartCollection.FilterPlaylistItems(allMedia, _libraryManager, ownerUser, refreshCache, _userDataManager, _logger, progressCallback).ToArray();
                _logger.LogDebug("Collection {CollectionName} filtered to {FilteredCount} items from {TotalCount} total items",
                    dto.Name, newItems.Length, allMedia.Length);

                // Create a lookup dictionary for O(1) access while preserving order from newItems
                // Include both media items and collections (if IncludeCollectionOnly is enabled) and playlists (if IncludePlaylistOnly is enabled)
                var mediaLookup = allMedia.ToDictionary(m => m.Id, m => m);
                AddIncludeOnlyItemsToLookup(mediaLookup, allCollections);
                AddIncludeOnlyItemsToLookup(mediaLookup, allPlaylists);
                var newLinkedChildren = newItems
                    .Where(itemId => mediaLookup.ContainsKey(itemId))
                    .Select(itemId => new LinkedChild { ItemId = itemId, Path = mediaLookup[itemId].Path })
                    .ToArray();

                // Calculate collection statistics from the same filtered list used for the actual collection
                dto.ItemCount = newLinkedChildren.Length;
                dto.TotalRuntimeMinutes = RuntimeCalculator.CalculateTotalRuntimeMinutes(
                    newLinkedChildren.Where(lc => lc.ItemId.HasValue).Select(lc => lc.ItemId!.Value).ToArray(),
                    mediaLookup,
                    _logger);
                _logger.LogDebug("Calculated collection stats: {ItemCount} items, {TotalRuntime} minutes total runtime",
                    dto.ItemCount, dto.TotalRuntimeMinutes);

                // Try to find existing collection by Jellyfin collection ID
                BaseItem? existingCollectionItem = null;

                _logger.LogDebug("Looking for collection: JellyfinCollectionId={JellyfinCollectionId}",
                    dto.JellyfinCollectionId);

                // First try to find by Jellyfin collection ID (most reliable)
                if (!string.IsNullOrEmpty(dto.JellyfinCollectionId) && Guid.TryParse(dto.JellyfinCollectionId, out var jellyfinCollectionId))
                {
                    var itemById = _libraryManager.GetItemById(jellyfinCollectionId);
                    if (itemById != null && itemById.GetBaseItemKind() == BaseItemKind.BoxSet)
                    {
                        existingCollectionItem = itemById;
                        _logger.LogDebug("Found existing collection by Jellyfin collection ID: {JellyfinCollectionId} - {CollectionName}",
                            dto.JellyfinCollectionId, itemById.Name);
                    }
                    else
                    {
                        _logger.LogDebug("No collection found by Jellyfin collection ID: {JellyfinCollectionId}", dto.JellyfinCollectionId);
                    }
                }

                var collectionName = dto.Name;

                if (existingCollectionItem != null && existingCollectionItem.GetBaseItemKind() == BaseItemKind.BoxSet)
                {
                    var existingCollection = existingCollectionItem;
                    _logger.LogDebug("Processing existing collection: {CollectionName} (ID: {CollectionId})", existingCollection.Name, existingCollection.Id);

                    // Check if the collection name needs to be updated
                    // Apply prefix/suffix formatting to ensure consistency
                    var currentName = existingCollection.Name;
                    var expectedName = NameFormatter.FormatPlaylistName(collectionName);
                    var nameChanged = currentName != expectedName;

                    if (nameChanged)
                    {
                        _logger.LogDebug("Collection name changing from '{OldName}' to '{NewName}'", currentName, expectedName);
                        existingCollection.Name = expectedName;
                    }

                    // Update the collection if any changes are needed
                    if (nameChanged)
                    {
                        await existingCollection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken);
                        _logger.LogDebug("Updated existing collection: {CollectionName}", existingCollection.Name);
                    }

                    // Update the collection items
                    await UpdateCollectionItemsAsync(existingCollection, newLinkedChildren, dto, cancellationToken);

                    _logger.LogDebug("Successfully updated existing collection: {CollectionName} with {ItemCount} items",
                        existingCollection.Name, newLinkedChildren.Length);

                    // Update LastRefreshed timestamp for successful refresh
                    dto.LastRefreshed = DateTime.UtcNow;
                    _logger.LogDebug("Updated LastRefreshed timestamp for collection: {CollectionName}", dto.Name);

                    return (true, $"Updated collection '{existingCollection.Name}' with {newLinkedChildren.Length} items", existingCollection.Id.ToString("N"));
                }
                else
                {
                    // Create new collection
                    _logger.LogDebug("Creating new collection: {CollectionName}", collectionName);

                    var newCollectionId = await CreateNewCollectionAsync(collectionName, newLinkedChildren, dto, cancellationToken);

                    // Check if collection creation actually succeeded
                    if (string.IsNullOrEmpty(newCollectionId))
                    {
                        _logger.LogError("Failed to create collection '{CollectionName}' - no valid collection ID returned", collectionName);
                        return (false, $"Failed to create collection '{collectionName}' - the collection could not be retrieved after creation", string.Empty);
                    }

                    // Update the DTO with the new Jellyfin collection ID
                    dto.JellyfinCollectionId = newCollectionId;

                    // Update LastRefreshed timestamp for successful refresh
                    dto.LastRefreshed = DateTime.UtcNow;
                    _logger.LogDebug("Updated LastRefreshed timestamp for collection: {CollectionName}", dto.Name);

                    _logger.LogDebug("Successfully created new collection: {CollectionName} with {ItemCount} items",
                        collectionName, newLinkedChildren.Length);

                    return (true, $"Created collection '{collectionName}' with {newLinkedChildren.Length} items", newCollectionId);
                }
        }

        /// <summary>
        /// Deletes a Jellyfin collection associated with the smart collection.
        /// </summary>
        /// <param name="dto">The smart collection DTO.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public Task DeleteAsync(SmartCollectionDto dto, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(dto);

            try
            {
                BaseItem? existingCollection = null;

                // Try to find by Jellyfin collection ID only
                if (!string.IsNullOrEmpty(dto.JellyfinCollectionId) && Guid.TryParse(dto.JellyfinCollectionId, out var jellyfinCollectionId))
                {
                    var itemById = _libraryManager.GetItemById(jellyfinCollectionId);
                    if (itemById != null && itemById.GetBaseItemKind() == BaseItemKind.BoxSet)
                    {
                        existingCollection = itemById;
                        _logger.LogDebug("Found collection by Jellyfin collection ID for deletion: {JellyfinCollectionId} - {CollectionName}",
                            dto.JellyfinCollectionId, existingCollection.Name);
                    }
                    else
                    {
                        _logger.LogWarning("No Jellyfin collection found by ID '{JellyfinCollectionId}' for deletion. Collection may have been manually deleted.", dto.JellyfinCollectionId);
                    }
                }
                else
                {
                    _logger.LogWarning("No Jellyfin collection ID available for collection '{CollectionName}'. Cannot delete Jellyfin collection.", dto.Name);
                }

                if (existingCollection != null)
                {
                    _logger.LogInformation("Deleting Jellyfin collection '{CollectionName}' (ID: {CollectionId})",
                        existingCollection.Name, existingCollection.Id);
                    // DeleteFileLocation = true to properly delete the BoxSet entity and its metadata
                    _libraryManager.DeleteItem(existingCollection, new DeleteOptions { DeleteFileLocation = true }, true);
                }

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting smart collection {CollectionName}", dto.Name);
                throw;
            }
        }

        public async Task RemoveSmartSuffixAsync(SmartCollectionDto dto, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(dto);

            try
            {
                BaseItem? existingCollection = null;

                // Try to find by Jellyfin collection ID only (no name fallback for suffix removal)
                if (!string.IsNullOrEmpty(dto.JellyfinCollectionId) && Guid.TryParse(dto.JellyfinCollectionId, out var jellyfinCollectionId))
                {
                    var itemById = _libraryManager.GetItemById(jellyfinCollectionId);
                    if (itemById != null && itemById.GetBaseItemKind() == BaseItemKind.BoxSet)
                    {
                        existingCollection = itemById;
                        _logger.LogDebug("Found collection by Jellyfin collection ID for suffix removal: {JellyfinCollectionId} - {CollectionName}",
                            dto.JellyfinCollectionId, existingCollection.Name);
                    }
                    else
                    {
                        _logger.LogWarning("No Jellyfin collection found by ID '{JellyfinCollectionId}' for suffix removal. Collection may have been manually deleted.", dto.JellyfinCollectionId);
                    }
                }
                else
                {
                    _logger.LogWarning("No Jellyfin collection ID available for collection '{CollectionName}'.", dto.Name);
                }

                if (existingCollection != null)
                {
                    var oldName = existingCollection.Name;
                    _logger.LogInformation("Removing smart collection suffix from '{CollectionName}' (ID: {CollectionId})",
                        oldName, existingCollection.Id);

                    // Get the current smart collection name format to see what needs to be removed
                    var currentSmartName = NameFormatter.FormatPlaylistName(dto.Name);

                    // Check if the collection name matches the current smart format
                    if (oldName == currentSmartName)
                    {
                        // Remove the smart collection naming and keep just the base name
                        existingCollection.Name = dto.Name;

                        // Save the changes
                        await existingCollection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

                        _logger.LogDebug("Successfully renamed collection from '{OldName}' to '{NewName}'",
                            oldName, dto.Name);
                    }
                    else
                    {
                        // Try to remove prefix and suffix even if they don't match current settings
                        // This handles cases where the user changed their prefix/suffix settings
                        var config = Plugin.Instance?.Configuration;
                        if (config != null)
                        {
                            var prefix = config.PlaylistNamePrefix ?? "";
                            var suffix = config.PlaylistNameSuffix ?? "[Smart]";

                            var baseName = dto.Name;
                            var expectedName = NameFormatter.FormatPlaylistNameWithSettings(baseName, prefix, suffix);

                            // If the collection name matches this pattern, remove the prefix and suffix
                            if (oldName == expectedName)
                            {
                                existingCollection.Name = baseName;

                                // Save the changes
                                await existingCollection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

                                _logger.LogDebug("Successfully renamed collection from '{OldName}' to '{NewName}' (removed prefix/suffix)",
                                    oldName, baseName);
                            }
                            else
                            {
                                _logger.LogWarning("Collection name '{OldName}' doesn't match expected smart format '{ExpectedName}'. Skipping rename.",
                                    oldName, expectedName);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Collection name '{OldName}' doesn't match expected smart format '{ExpectedName}'. Skipping rename.",
                                oldName, currentSmartName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing smart suffix from collection {CollectionName}", dto.Name);
                throw;
            }
        }

        public async Task DisableAsync(SmartCollectionDto dto, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(dto);

            try
            {
                _logger.LogDebug("Disabling smart collection: {CollectionName}", dto.Name);

                await DeleteAsync(dto, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Successfully disabled smart collection: {CollectionName}", dto.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disabling smart collection {CollectionName}", dto.Name);
                throw;
            }
        }

        private async Task UpdateCollectionItemsAsync(BaseItem collection, LinkedChild[] linkedChildren, SmartCollectionDto dto, CancellationToken cancellationToken)
        {
            // Verify this is a BoxSet using BaseItemKind
            if (collection.GetBaseItemKind() != BaseItemKind.BoxSet)
            {
                _logger.LogError("Expected BoxSet but got {Type} (BaseItemKind: {Kind})", collection.GetType().Name, collection.GetBaseItemKind());
                return;
            }

            _logger.LogDebug("Updating collection {CollectionName} items to {ItemCount}",
                collection.Name, linkedChildren.Length);

            // Update the collection items using reflection to access LinkedChildren property
            var linkedChildrenProperty = collection.GetType().GetProperty("LinkedChildren");
            if (linkedChildrenProperty != null && linkedChildrenProperty.CanWrite)
            {
                linkedChildrenProperty.SetValue(collection, linkedChildren);
            }
            else
            {
                _logger.LogError("Cannot set LinkedChildren property on collection {CollectionName}", collection.Name);
                return;
            }

            // Save the changes
            await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

            // Handle custom images and auto-generation
            // Logic: Custom images uploaded via SmartLists take precedence over auto-generation
            // Auto-generation only happens for Primary/Thumb if no custom versions exist
            var hasCustomPrimary = dto.CustomImages?.ContainsKey("Primary") == true;
            var hasCustomThumb = dto.CustomImages?.ContainsKey("Thumb") == true;

            // Always call ApplyCustomImagesToCollectionAsync - it handles both:
            // 1. Applying custom images if any exist
            // 2. Cleaning up orphaned images when custom images are deleted
            await ApplyCustomImagesToCollectionAsync(collection, dto, cancellationToken).ConfigureAwait(false);

            // Only trigger auto-generation if no custom Primary AND no custom Thumb uploaded
            // (RefreshCollectionMetadataAsync already checks for manually uploaded images in Jellyfin)
            if (!hasCustomPrimary && !hasCustomThumb)
            {
                // Pass linkedChildren to ensure we use the freshly-set items rather than potentially stale cache
                await RefreshCollectionMetadataAsync(collection, linkedChildren, cancellationToken).ConfigureAwait(false);
            }

            // Always set the name after metadata refresh to ensure it's correct
            // This prevents metadata providers from overwriting our intended name
            var expectedName = NameFormatter.FormatPlaylistName(dto.Name);
            var collectionAfterRefresh = _libraryManager.GetItemById(collection.Id);
            if (collectionAfterRefresh != null && collectionAfterRefresh.GetBaseItemKind() == BaseItemKind.BoxSet)
            {
                if (collectionAfterRefresh.Name != expectedName)
                {
                    _logger.LogDebug("Setting collection name to '{ExpectedName}' after metadata refresh (was '{CurrentName}')",
                        expectedName, collectionAfterRefresh.Name);
                }
                collectionAfterRefresh.Name = expectedName;
                await collectionAfterRefresh.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

                // Auto-generate collection images (SetPhotoForCollection handles per-type manual image checks)
                // Pass linkedChildren to ensure we use the freshly-set items rather than potentially stale cache
                await SetPhotoForCollection(collectionAfterRefresh, linkedChildren, cancellationToken).ConfigureAwait(false);

                // Set DisplayOrder to "Default" to respect the plugin's custom sort order
                SetCollectionDisplayOrder(collectionAfterRefresh);
                await collectionAfterRefresh.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<string> CreateNewCollectionAsync(string collectionName, LinkedChild[] linkedChildren, SmartCollectionDto dto, CancellationToken cancellationToken)
        {
            // Apply prefix/suffix to collection name using the same configuration as playlists
            var formattedName = NameFormatter.FormatPlaylistName(collectionName);
            
            _logger.LogDebug("Creating new smart collection {CollectionName} (formatted as {FormattedName}) with {ItemCount} items",
                collectionName, formattedName, linkedChildren.Length);

            // Create collection using ICollectionManager
            // Note: ICollectionManager.CreateCollectionAsync signature may vary - using reflection if needed
            var itemIds = linkedChildren.Where(lc => lc.ItemId.HasValue).Select(lc => lc.ItemId!.Value).ToList();
            
            // Try to use ICollectionManager.CreateCollectionAsync
            // If the API signature is different, we'll use reflection or create BoxSet directly
            Guid collectionId;
            
            try
            {
                // Try to find CreateCollectionAsync with various signatures
                var collectionManagerType = _collectionManager.GetType();
                _logger.LogDebug("Searching for CreateCollectionAsync methods on {Type}", collectionManagerType.Name);
                
                var allMethods = collectionManagerType.GetMethods()
                    .Where(m => m.Name.Contains("Create", StringComparison.OrdinalIgnoreCase))
                    .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})")
                    .ToArray();
                _logger.LogDebug("Available Create methods: {Methods}", string.Join("; ", allMethods));
                
                // Try different method signatures
                System.Reflection.MethodInfo? createMethod = null;
                
                // Find CollectionCreationOptions type
                Type? optionsType = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    optionsType = assembly.GetTypes()
                        .FirstOrDefault(t => t.Name == "CollectionCreationOptions" && t.Namespace?.Contains("MediaBrowser") == true);
                    if (optionsType != null)
                    {
                        _logger.LogDebug("Found CollectionCreationOptions type in assembly {Assembly}", assembly.GetName().Name);
                        break;
                    }
                }
                
                // Signature 1: CreateCollectionAsync(CollectionCreationOptions options)
                if (optionsType != null)
                {
                    createMethod = collectionManagerType.GetMethod("CreateCollectionAsync", new[] { optionsType });
                    if (createMethod != null)
                    {
                        _logger.LogDebug("Found CreateCollectionAsync(CollectionCreationOptions) method");
                    }
                }
                
                if (createMethod == null)
                {
                    // Signature 2: CreateCollectionAsync(string name, Guid[] itemIds)
                    createMethod = collectionManagerType.GetMethod("CreateCollectionAsync", new[] { typeof(string), typeof(Guid[]) });
                }
                
                if (createMethod == null)
                {
                    // Signature 3: CreateCollectionAsync(string name, IEnumerable<Guid> itemIds)
                    createMethod = collectionManagerType.GetMethod("CreateCollectionAsync", new[] { typeof(string), typeof(IEnumerable<Guid>) });
                }
                
                if (createMethod != null)
                {
                    _logger.LogDebug("Found CreateCollectionAsync method: {Method}", createMethod);
                    
                    var parameters = createMethod.GetParameters();
                    object? taskResult = null;
                    
                    if (parameters.Length == 1 && parameters[0].ParameterType.Name == "CollectionCreationOptions")
                    {
                        // Use CollectionCreationOptions
                        var optType = parameters[0].ParameterType;
                        var options = Activator.CreateInstance(optType);
                        if (options == null)
                        {
                            throw new InvalidOperationException("Failed to create CollectionCreationOptions instance");
                        }
                        
                        _logger.LogDebug("Created CollectionCreationOptions instance, setting properties");
                        
                        // Set Name property
                        var nameProperty = optType.GetProperty("Name");
                        if (nameProperty != null)
                        {
                            nameProperty.SetValue(options, formattedName);
                            _logger.LogDebug("Set Name to: {Name}", formattedName);
                        }
                        
                        // Note: We'll add items using AddToCollectionAsync after creation instead of setting ItemIdList
                        _logger.LogDebug("Collection will be created empty, items will be added via AddToCollectionAsync");
                        
                        _logger.LogDebug("Invoking CreateCollectionAsync with CollectionCreationOptions");
                        taskResult = createMethod.Invoke(_collectionManager, new object[] { options });
                    }
                    else
                    {
                        // Use direct parameters
                        _logger.LogDebug("Invoking CreateCollectionAsync with direct parameters");
                        taskResult = createMethod.Invoke(_collectionManager, new object[] { formattedName, itemIds.ToArray() });
                    }
                    
                    if (taskResult != null)
                    {
                        // Await the task and extract the result
                        _logger.LogDebug("Awaiting task result");
                        await ((Task)taskResult).ConfigureAwait(false);
                        var resultProperty = taskResult.GetType().GetProperty("Result");
                        var boxSetResult = resultProperty?.GetValue(taskResult);
                        
                        _logger.LogDebug("Task completed, result type: {Type}", boxSetResult?.GetType().Name ?? "null");
                        
                        if (boxSetResult is BaseItem baseItem)
                        {
                            collectionId = baseItem.Id;
                            _logger.LogDebug("Collection created via ICollectionManager with ID: {CollectionId}", collectionId);
                            
                            // Add items to the collection using AddToCollectionAsync
                            if (itemIds.Count > 0)
                            {
                                _logger.LogDebug("Adding {Count} items to collection {CollectionId} using AddToCollectionAsync", itemIds.Count, collectionId);
                                var addMethod = _collectionManager.GetType().GetMethod("AddToCollectionAsync");
                                if (addMethod != null)
                                {
                                    var addTask = addMethod.Invoke(_collectionManager, new object[] { collectionId, itemIds.ToArray() });
                                    if (addTask != null)
                                    {
                                        await ((Task)addTask).ConfigureAwait(false);
                                        _logger.LogDebug("Successfully added items to collection via AddToCollectionAsync");
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning("AddToCollectionAsync method not found on ICollectionManager");
                                }
                            }
                        }
                        else
                        {
                            throw new InvalidOperationException($"CreateCollectionAsync did not return a BaseItem, got: {boxSetResult?.GetType().Name ?? "null"}");
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("CreateCollectionAsync returned null");
                    }
                }
                else
                {
                    // Fallback: Create BoxSet using reflection
                    _logger.LogDebug("ICollectionManager.CreateCollectionAsync not found, creating BoxSet via reflection");
                    var boxSetType = typeof(BaseItem).Assembly.GetType("MediaBrowser.Controller.Entities.BoxSet") 
                        ?? AppDomain.CurrentDomain.GetAssemblies()
                            .SelectMany(a => a.GetTypes())
                            .FirstOrDefault(t => t.Name == "BoxSet" && t.IsSubclassOf(typeof(BaseItem)));
                    
                    if (boxSetType != null)
                    {
                        var boxSet = Activator.CreateInstance(boxSetType);
                        if (boxSet != null)
                        {
                            var baseItem = (BaseItem)boxSet;
                            
                            // Set ID first - must be set before persisting
                            var newCollectionId = Guid.NewGuid();
                            boxSetType.GetProperty("Id")?.SetValue(boxSet, newCollectionId);
                            _logger.LogDebug("Generated new collection ID: {CollectionId}", newCollectionId);
                            
                            boxSetType.GetProperty("Name")?.SetValue(boxSet, formattedName);
                            boxSetType.GetProperty("LinkedChildren")?.SetValue(boxSet, linkedChildren);
                            
                            // Add to library manager - use CreateItemAsync if available, otherwise try UpdateToRepositoryAsync
                            var createItemMethod = _libraryManager.GetType().GetMethod("CreateItemAsync", new[] { typeof(BaseItem), typeof(CancellationToken) });
                            if (createItemMethod != null)
                            {
                                await ((Task)createItemMethod.Invoke(_libraryManager, new object[] { baseItem, cancellationToken })!).ConfigureAwait(false);
                            }
                            else
                            {
                                // Fallback: try UpdateToRepositoryAsync
                                await baseItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                            }
                            collectionId = baseItem.Id;
                            _logger.LogDebug("BoxSet created with ID: {CollectionId}", collectionId);
                        }
                        else
                        {
                            throw new InvalidOperationException("Failed to create BoxSet instance via reflection");
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("BoxSet type not found - cannot create collection");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create collection via ICollectionManager, trying reflection-based BoxSet creation");
                // Fallback: Create BoxSet using reflection
                var boxSetType = typeof(BaseItem).Assembly.GetType("MediaBrowser.Controller.Entities.BoxSet") 
                    ?? AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => a.GetTypes())
                        .FirstOrDefault(t => t.Name == "BoxSet" && t.IsSubclassOf(typeof(BaseItem)));
                
                if (boxSetType != null)
                {
                    var boxSet = Activator.CreateInstance(boxSetType);
                    if (boxSet != null)
                    {
                        var baseItem = (BaseItem)boxSet;
                        
                        // Set ID first - must be set before persisting
                        var newCollectionId = Guid.NewGuid();
                        boxSetType.GetProperty("Id")?.SetValue(boxSet, newCollectionId);
                        _logger.LogDebug("Generated new collection ID: {CollectionId}", newCollectionId);
                        
                        boxSetType.GetProperty("Name")?.SetValue(boxSet, formattedName);
                        boxSetType.GetProperty("LinkedChildren")?.SetValue(boxSet, linkedChildren);
                        
                        // Add to library manager - use CreateItemAsync if available, otherwise try UpdateToRepositoryAsync
                        var createItemMethod = _libraryManager.GetType().GetMethod("CreateItemAsync", new[] { typeof(BaseItem), typeof(CancellationToken) });
                        if (createItemMethod != null)
                        {
                            await ((Task)createItemMethod.Invoke(_libraryManager, new object[] { baseItem, cancellationToken })!).ConfigureAwait(false);
                        }
                        else
                        {
                            // Fallback: try UpdateToRepositoryAsync
                            await baseItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                        }
                        collectionId = baseItem.Id;
                        _logger.LogDebug("BoxSet created with ID: {CollectionId}", collectionId);
                    }
                    else
                    {
                        throw new InvalidOperationException("Failed to create BoxSet instance via reflection");
                    }
                }
                else
                {
                    throw new InvalidOperationException("BoxSet type not found - cannot create collection");
                }
            }

            _logger.LogDebug("Collection creation result: ID = {CollectionId}", collectionId);

            var retrievedItem = _libraryManager.GetItemById(collectionId);
            if (retrievedItem != null && retrievedItem.GetBaseItemKind() == BaseItemKind.BoxSet)
            {
                _logger.LogDebug("Retrieved new collection: Name = {Name}", retrievedItem.Name);

                // Handle custom images and auto-generation
                // Logic: Custom images uploaded via SmartLists take precedence over auto-generation
                // Auto-generation only happens for Primary/Thumb if no custom versions exist
                var hasCustomPrimary = dto.CustomImages?.ContainsKey("Primary") == true;
                var hasCustomThumb = dto.CustomImages?.ContainsKey("Thumb") == true;

                // Always call ApplyCustomImagesToCollectionAsync - it handles both:
                // 1. Applying custom images if any exist
                // 2. Cleaning up orphaned images when custom images are deleted
                await ApplyCustomImagesToCollectionAsync(retrievedItem, dto, cancellationToken).ConfigureAwait(false);

                // Only trigger auto-generation if no custom Primary AND no custom Thumb uploaded
                // (RefreshCollectionMetadataAsync already checks for manually uploaded images in Jellyfin)
                if (!hasCustomPrimary && !hasCustomThumb)
                {
                    // Pass linkedChildren to avoid stale cache issues - the collection object may not have
                    // updated LinkedChildren yet after AddToCollectionAsync, especially for large libraries
                    await RefreshCollectionMetadataAsync(retrievedItem, linkedChildren, cancellationToken).ConfigureAwait(false);
                }

                // Always set the name after metadata refresh to ensure it's correct
                // This prevents metadata providers from overwriting our intended name
                retrievedItem = _libraryManager.GetItemById(collectionId);
                if (retrievedItem != null && retrievedItem.GetBaseItemKind() == BaseItemKind.BoxSet)
                {
                    if (retrievedItem.Name != formattedName)
                    {
                        _logger.LogDebug("Setting collection name to '{FormattedName}' after metadata refresh (was '{CurrentName}')",
                            formattedName, retrievedItem.Name);
                    }
                    retrievedItem.Name = formattedName;
                    await retrievedItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

                    // Auto-generate collection images (SetPhotoForCollection handles per-type manual image checks)
                    // Pass linkedChildren to avoid stale cache issues - the collection object may not have
                    // updated LinkedChildren yet after AddToCollectionAsync, especially for large libraries
                    await SetPhotoForCollection(retrievedItem, linkedChildren, cancellationToken).ConfigureAwait(false);

                    // Set DisplayOrder to "Default" to respect the plugin's custom sort order
                    SetCollectionDisplayOrder(retrievedItem);
                    await retrievedItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                }

                return collectionId.ToString("N");
            }
            else
            {
                _logger.LogWarning("Failed to retrieve newly created collection with ID {CollectionId}", collectionId);
                return string.Empty;
            }
        }

        /// <summary>
        /// Applies custom images from the smart list configuration to the Jellyfin collection.
        /// Also removes orphaned images when CustomImages are deleted.
        /// Custom images take precedence over auto-generation - auto-generation only applies
        /// when no custom Primary/Thumb images are uploaded.
        /// </summary>
        /// <param name="collection">The Jellyfin collection item.</param>
        /// <param name="dto">The smart collection DTO.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        private async Task ApplyCustomImagesToCollectionAsync(BaseItem collection, SmartCollectionDto dto, CancellationToken cancellationToken)
        {
            if (_imageService == null || string.IsNullOrEmpty(dto.Id))
            {
                return;
            }

            try
            {
                var itemPath = collection.ContainingFolderPath;
                if (string.IsNullOrEmpty(itemPath) || !Directory.Exists(itemPath))
                {
                    _logger.LogWarning("Cannot apply custom images: collection path is invalid: {Path}", itemPath);
                    return;
                }

                var imageInfos = collection.ImageInfos?.ToList() ?? [];
                var appliedImages = new List<(string Path, ImageType Type)>();
                var customImageTypes = new HashSet<ImageType>();

                // Check if this smart list has ever had custom images uploaded through our system
                // by checking if an image folder exists for this list (even if empty after deletions)
                var hasOrHadSmartListImages = _imageService.HasImageFolder(dto.Id);

                // Apply custom images if any exist
                if (dto.CustomImages != null && dto.CustomImages.Count > 0)
                {
                    foreach (var (imageTypeName, fileName) in dto.CustomImages)
                    {
                        // Parse the image type first to check if we should skip it
                        if (!Enum.TryParse<ImageType>(imageTypeName, ignoreCase: true, out var imageType))
                        {
                            _logger.LogWarning("Invalid image type: {ImageType}", imageTypeName);
                            continue;
                        }

                        // Get the source image path from the image service
                        var sourcePath = _imageService.GetImagePath(dto.Id, imageTypeName);
                        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                        {
                            _logger.LogDebug("Custom image not found: {ImageType} for collection {CollectionName}", imageTypeName, dto.Name);
                            continue;
                        }

                        customImageTypes.Add(imageType);

                        // Determine destination filename
                        var extension = Path.GetExtension(sourcePath);
                        var destFileName = GetImageFileName(imageType, extension);
                        var destPath = Path.Combine(itemPath, destFileName);

                        // Copy the image to the collection folder
                        File.Copy(sourcePath, destPath, overwrite: true);
                        _logger.LogDebug("Copied custom {ImageType} image to collection: {DestPath}", imageTypeName, destPath);

                        // Remove existing image of the same type
                        var existingImage = imageInfos.FirstOrDefault(i => i.Type == imageType);
                        if (existingImage != null)
                        {
                            imageInfos.Remove(existingImage);
                        }

                        // Add the new image info
                        var imageInfo = new ItemImageInfo
                        {
                            Path = destPath,
                            Type = imageType,
                            DateModified = DateTime.UtcNow
                        };
                        imageInfos.Add(imageInfo);
                        appliedImages.Add((destPath, imageType));
                    }
                }

                // Clean up orphaned images if this smart list has or had custom images
                var removedAny = false;
                if (hasOrHadSmartListImages)
                {
                    removedAny = RemoveOrphanedCustomImages(collection, itemPath, imageInfos, customImageTypes);
                }

                if (appliedImages.Count > 0 || removedAny)
                {
                    collection.ImageInfos = imageInfos.ToArray();
                    await collection.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug("Applied {AppliedCount} custom image(s) to collection '{CollectionName}', removed orphaned: {RemovedAny}",
                        appliedImages.Count, dto.Name, removedAny);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply custom images to collection '{CollectionName}'", dto.Name);
            }
        }

        /// <summary>
        /// Cleans up auto-generated collages when custom Primary/Thumb images are uploaded via SmartLists.
        /// IMPORTANT: This method does NOT delete images like folder.png, backdrop.png, etc. because
        /// we cannot distinguish between images uploaded via SmartLists vs. images uploaded via Jellyfin UI.
        /// We only clean up files we KNOW we created (smartlist-collage.jpg, smartlist-thumb-collage.jpg).
        /// </summary>
        /// <param name="collection">The collection item.</param>
        /// <param name="itemPath">Path to the collection folder.</param>
        /// <param name="imageInfos">List of image infos to update.</param>
        /// <param name="customImageTypes">Set of image types currently in CustomImages (uploaded via SmartLists).</param>
        private bool RemoveOrphanedCustomImages(BaseItem collection, string itemPath, List<ItemImageInfo> imageInfos, HashSet<ImageType> customImageTypes)
        {
            bool removedAny = false;

            // If user has custom Primary via SmartLists, clean up auto-generated collage
            if (customImageTypes.Contains(ImageType.Primary))
            {
                var collagePath = Path.Combine(itemPath, "smartlist-collage.jpg");
                if (File.Exists(collagePath))
                {
                    try
                    {
                        File.Delete(collagePath);
                        _logger.LogDebug("Removed auto-generated Primary collage (custom image uploaded): {FilePath}", collagePath);
                        removedAny = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete auto-generated collage file: {FilePath}", collagePath);
                    }
                }
            }

            // If user has custom Thumb via SmartLists, clean up auto-generated thumb collage
            if (customImageTypes.Contains(ImageType.Thumb))
            {
                var thumbCollagePath = Path.Combine(itemPath, "smartlist-thumb-collage.jpg");
                if (File.Exists(thumbCollagePath))
                {
                    try
                    {
                        File.Delete(thumbCollagePath);
                        _logger.LogDebug("Removed auto-generated Thumb collage (custom image uploaded): {FilePath}", thumbCollagePath);
                        removedAny = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete auto-generated thumb collage file: {FilePath}", thumbCollagePath);
                    }
                }
            }

            return removedAny;
        }

        /// <summary>
        /// Gets the standard Jellyfin filename for an image type.
        /// Delegates to the shared helper in SmartListImageService.
        /// </summary>
        private static string GetImageFileName(ImageType imageType, string extension)
            => SmartListImageService.GetJellyfinImageFileName(imageType, extension);

        private IEnumerable<BaseItem> GetAllMedia(List<string> mediaTypes, SmartCollectionDto? dto = null, User? ownerUser = null)
        {
            // Collections are server-wide (visible to all users), but the media query and rules
            // are evaluated in the context of the owner user to respect library access permissions
            // and user-specific data (IsPlayed, IsFavorite, etc.)
            
            var baseItemKinds = MediaTypeConverter.GetBaseItemKindsFromMediaTypes(mediaTypes, dto, _logger);
            
            // Owner user is required for proper permissions and user-specific data
            if (ownerUser == null)
            {
                _logger.LogError("Owner user is required for GetAllMedia but was not provided");
                return [];
            }
            
            // Query all items the owner user has access to
            var query = new InternalItemsQuery(ownerUser)
            {
                IncludeItemTypes = baseItemKinds,
                Recursive = true,
                IsVirtualItem = false
            };
            
            return _libraryManager.GetItemsResult(query).Items;
        }

        /// <summary>
        /// Refreshes collection metadata including cover image generation.
        /// </summary>
        /// <param name="collection">The collection to refresh.</param>
        /// <param name="knownLinkedChildren">Optional pre-fetched LinkedChildren to use instead of querying from the collection.
        /// This is used when items were just added and the collection object may have stale data.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "File path comes from Jellyfin's internal ItemImageInfo.Path property, which is validated by Jellyfin")]
        private async Task RefreshCollectionMetadataAsync(BaseItem collection, LinkedChild[]? knownLinkedChildren, CancellationToken cancellationToken)
        {
            // Verify this is a BoxSet using BaseItemKind
            if (collection.GetBaseItemKind() != BaseItemKind.BoxSet)
            {
                _logger.LogWarning("Expected BoxSet but got {Type} (BaseItemKind: {Kind}) for collection {Name}",
                    collection.GetType().Name, collection.GetBaseItemKind(), collection.Name);
                return;
            }

            // BoxSet properties are available on BaseItem
            var boxSet = collection;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var directoryService = new Services.Shared.BasicDirectoryService();

                // Check if collection is empty - use pre-fetched LinkedChildren if provided to avoid stale cache issues
                LinkedChild[]? linkedChildren;
                if (knownLinkedChildren != null)
                {
                    linkedChildren = knownLinkedChildren;
                    _logger.LogDebug("Using {Count} pre-fetched LinkedChildren for collection {CollectionName} metadata refresh",
                        linkedChildren.Length, collection.Name);
                }
                else
                {
                    var linkedChildrenProperty = collection.GetType().GetProperty("LinkedChildren");
                    linkedChildren = linkedChildrenProperty?.GetValue(collection) as LinkedChild[];
                }
                
                if (linkedChildren == null || linkedChildren.Length == 0)
                {
                    // Check which image types have manually uploaded images (per-type check)
                    var (hasManualPrimary, hasManualThumb) = await GetManuallyUploadedImageFlagsAsync(collection, cancellationToken).ConfigureAwait(false);

                    if (hasManualPrimary && hasManualThumb)
                    {
                        _logger.LogDebug("Collection {CollectionName} is empty but has manually uploaded Primary and Thumb images, preserving them", collection.Name);
                        stopwatch.Stop();
                        return;
                    }

                    _logger.LogDebug("Collection {CollectionName} is empty - clearing auto-generated cover images (preserving manual: Primary={HasManualPrimary}, Thumb={HasManualThumb})",
                        collection.Name, hasManualPrimary, hasManualThumb);

                    // Clear only non-manually-uploaded images
                    await ClearAutoGeneratedImagesAsync(collection, hasManualPrimary, hasManualThumb, cancellationToken).ConfigureAwait(false);

                    stopwatch.Stop();
                    _logger.LogDebug("Cover image clearing completed for empty collection {CollectionName} in {ElapsedTime}ms", collection.Name, stopwatch.ElapsedMilliseconds);
                    return;
                }

                // Check if collection has a manually uploaded image BEFORE refreshing metadata
                // If it does, don't replace images (ReplaceAllImages would overwrite user's image)
                var hasManualImage = await HasManuallyUploadedImageAsync(collection, cancellationToken).ConfigureAwait(false);

                if (hasManualImage)
                {
                    _logger.LogDebug("Collection {CollectionName} has a manually uploaded image, skipping metadata image refresh", collection.Name);
                    stopwatch.Stop();
                    return;
                }

                _logger.LogDebug("Triggering metadata refresh for collection {CollectionName} to generate cover image", collection.Name);

                var refreshOptions = new MetadataRefreshOptions(directoryService)
                {
                    MetadataRefreshMode = MetadataRefreshMode.Default,
                    ImageRefreshMode = MetadataRefreshMode.Default,
                    ReplaceAllMetadata = false, // Don't replace metadata - prevents online providers from changing the collection name
                    ReplaceAllImages = true // Safe to replace since we checked for manual images above
                };

                await _providerManager.RefreshSingleItem(collection, refreshOptions, cancellationToken).ConfigureAwait(false);

                stopwatch.Stop();
                _logger.LogDebug("Cover image generation completed for collection {CollectionName} in {ElapsedTime}ms", collection.Name, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "Failed to refresh metadata for collection {CollectionName} after {ElapsedTime}ms. Cover image may not be generated.", collection.Name, stopwatch.ElapsedMilliseconds);
            }
        }

        /// <summary>
        /// Sets a photo/image for a collection from items within the collection.
        /// Creates a single image for 1 item, or a 4-image collage for 2+ items.
        /// Skips if the collection has a manually uploaded image.
        /// </summary>
        /// <param name="collection">The collection to set the image for</param>
        /// <param name="knownLinkedChildren">Optional pre-fetched LinkedChildren to use instead of querying from the collection.
        /// This is used when items were just added and the collection object may have stale data.</param>
        /// <param name="cancellationToken">Cancellation token</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "File path comes from Jellyfin's internal ItemImageInfo.Path property, which is validated by Jellyfin")]
        private async Task SetPhotoForCollection(BaseItem collection, LinkedChild[]? knownLinkedChildren, CancellationToken cancellationToken)
        {
            try
            {
                // Check which image types have manually uploaded images (per-type check)
                var (hasManualPrimary, hasManualThumb) = await GetManuallyUploadedImageFlagsAsync(collection, cancellationToken).ConfigureAwait(false);

                if (hasManualPrimary && hasManualThumb)
                {
                    _logger.LogDebug("Collection {CollectionName} has manually uploaded Primary and Thumb images, skipping automatic image generation", collection.Name);
                    return;
                }

                if (hasManualPrimary)
                {
                    _logger.LogDebug("Collection {CollectionName} has a manually uploaded Primary image, will only auto-generate Thumb", collection.Name);
                }

                if (hasManualThumb)
                {
                    _logger.LogDebug("Collection {CollectionName} has a manually uploaded Thumb image, will only auto-generate Primary", collection.Name);
                }

                // Get collection items - use pre-fetched LinkedChildren if provided to avoid stale cache issues
                List<BaseItem> items;
                if (knownLinkedChildren != null && knownLinkedChildren.Length > 0)
                {
                    // Resolve LinkedChildren to BaseItems directly from library manager
                    // Process all items to ensure we find images even if they're not in the first few items
                    // The list has already been limited by MaxItems in FilterPlaylistItems, so this respects user config
                    items = knownLinkedChildren
                        .Where(lc => lc.ItemId.HasValue)
                        .Select(lc => _libraryManager.GetItemById(lc.ItemId!.Value))
                        .Where(item => item != null)
                        .ToList()!;
                    _logger.LogDebug("Resolved {ResolvedCount} pre-fetched items for collection {CollectionName} image generation",
                        items.Count, collection.Name);
                }
                else
                {
                    items = await GetCollectionItemsAsync(collection, cancellationToken).ConfigureAwait(false);
                }

                if (items.Count == 0)
                {
                    // Collection is empty - clear auto-generated images only (preserve manual uploads)
                    _logger.LogDebug("Collection {CollectionName} has no items - clearing auto-generated cover images", collection.Name);
                    await ClearAutoGeneratedImagesAsync(collection, hasManualPrimary, hasManualThumb, cancellationToken).ConfigureAwait(false);
                    return;
                }

                // Get items with images for Primary (prefer Movies/Series, fallback to any item with image)
                var itemsWithPrimaryImages = GetItemsWithImages(items);

                if (itemsWithPrimaryImages.Count == 0)
                {
                    // No items with primary images - clear auto-generated images only
                    _logger.LogDebug("No items with images found in collection {CollectionName}. Items: {Items}. Clearing auto-generated cover images.",
                        collection.Name,
                        string.Join(", ", items.Select(i => i.Name)));
                    await ClearAutoGeneratedImagesAsync(collection, hasManualPrimary, hasManualThumb, cancellationToken).ConfigureAwait(false);
                    return;
                }

                // Create primary image if no manual Primary exists
                if (!hasManualPrimary)
                {
                    await CreateCollectionImageAsync(collection, itemsWithPrimaryImages, cancellationToken).ConfigureAwait(false);
                }

                // Create thumb image if no manual Thumb exists
                if (!hasManualThumb)
                {
                    var itemsWithThumbImages = GetItemsWithThumbImages(items);
                    if (itemsWithThumbImages.Count > 0)
                    {
                        await CreateCollectionThumbAsync(collection, itemsWithThumbImages, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting image for collection {CollectionName}",
                    collection.Name);
            }
        }

        /// <summary>
        /// Clears auto-generated images from the collection, preserving manually uploaded ones.
        /// </summary>
        private async Task ClearAutoGeneratedImagesAsync(BaseItem collection, bool hasManualPrimary, bool hasManualThumb, CancellationToken cancellationToken)
        {
            if (collection.ImageInfos == null)
            {
                return;
            }

            var imageInfos = collection.ImageInfos.ToList();
            var needsUpdate = false;

            // Clear Primary if not manually uploaded
            if (!hasManualPrimary)
            {
                var primaryImage = imageInfos.FirstOrDefault(i => i.Type == ImageType.Primary);
                if (primaryImage != null)
                {
                    imageInfos.Remove(primaryImage);
                    needsUpdate = true;

                    // Delete auto-generated collage file if it exists
                    if (!string.IsNullOrEmpty(primaryImage.Path))
                    {
                        var fileName = System.IO.Path.GetFileName(primaryImage.Path);
                        if (string.Equals(fileName, "smartlist-collage.jpg", StringComparison.OrdinalIgnoreCase))
                        {
                            TryDeleteFile(primaryImage.Path, "auto-generated Primary collage", collection.Name);
                        }
                    }

                    _logger.LogDebug("Cleared auto-generated Primary image for collection {CollectionName}", collection.Name);
                }
            }

            // Clear Thumb if not manually uploaded
            if (!hasManualThumb)
            {
                var thumbImage = imageInfos.FirstOrDefault(i => i.Type == ImageType.Thumb);
                if (thumbImage != null)
                {
                    imageInfos.Remove(thumbImage);
                    needsUpdate = true;

                    // Delete auto-generated thumb collage file if it exists
                    if (!string.IsNullOrEmpty(thumbImage.Path))
                    {
                        var fileName = System.IO.Path.GetFileName(thumbImage.Path);
                        if (string.Equals(fileName, "smartlist-thumb-collage.jpg", StringComparison.OrdinalIgnoreCase))
                        {
                            TryDeleteFile(thumbImage.Path, "auto-generated Thumb collage", collection.Name);
                        }
                    }

                    _logger.LogDebug("Cleared auto-generated Thumb image for collection {CollectionName}", collection.Name);
                }
            }

            if (needsUpdate)
            {
                collection.ImageInfos = imageInfos.ToArray();
                await collection.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Safely attempts to delete a file, logging any errors.
        /// </summary>
        private void TryDeleteFile(string filePath, string description, string collectionName)
        {
            try
            {
                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                    _logger.LogDebug("Deleted {Description} file for collection {CollectionName}", description, collectionName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete {Description} file for collection {CollectionName}", description, collectionName);
            }
        }

        /// <summary>
        /// Checks if the collection has manually uploaded images that should not be overwritten.
        /// Returns flags for each image type (Primary and Thumb) separately.
        /// A manually uploaded image is one that:
        /// 1. Exists in the collection's metadata directory
        /// 2. Is NOT one of the collection items' images
        /// 3. Is NOT our auto-generated collage (smartlist-collage.jpg or smartlist-thumb-collage.jpg)
        ///
        /// Note: Our auto-generated collages will be regenerated to match current items.
        /// </summary>
        /// <returns>A tuple with (HasManualPrimary, HasManualThumb) flags.</returns>
        private async Task<(bool HasManualPrimary, bool HasManualThumb)> GetManuallyUploadedImageFlagsAsync(BaseItem collection, CancellationToken cancellationToken = default)
        {
            if (collection.ImageInfos == null)
            {
                return (false, false);
            }

            var primaryImage = collection.ImageInfos.FirstOrDefault(i => i.Type == ImageType.Primary);
            var thumbImage = collection.ImageInfos.FirstOrDefault(i => i.Type == ImageType.Thumb);

            var collectionPath = collection.Path;
            if (string.IsNullOrEmpty(collectionPath))
            {
                return (false, false);
            }

            var normalizedCollectionPath = System.IO.Path.GetFullPath(collectionPath);

            bool hasManualPrimary = false;
            bool hasManualThumb = false;

            // Check Primary image
            if (primaryImage != null && !string.IsNullOrEmpty(primaryImage.Path))
            {
                hasManualPrimary = await IsManuallyUploadedImageAsync(primaryImage, normalizedCollectionPath, collection, ImageType.Primary, cancellationToken).ConfigureAwait(false);
            }

            // Check Thumb image
            if (thumbImage != null && !string.IsNullOrEmpty(thumbImage.Path))
            {
                hasManualThumb = await IsManuallyUploadedImageAsync(thumbImage, normalizedCollectionPath, collection, ImageType.Thumb, cancellationToken).ConfigureAwait(false);
            }

            return (hasManualPrimary, hasManualThumb);
        }

        /// <summary>
        /// Checks if the collection has ANY manually uploaded image (Primary or Thumb).
        /// Use this when you need a simple boolean check for any manual image.
        /// For more granular control, use GetManuallyUploadedImageFlagsAsync instead.
        /// </summary>
        private async Task<bool> HasManuallyUploadedImageAsync(BaseItem collection, CancellationToken cancellationToken = default)
        {
            var (hasManualPrimary, hasManualThumb) = await GetManuallyUploadedImageFlagsAsync(collection, cancellationToken).ConfigureAwait(false);
            return hasManualPrimary || hasManualThumb;
        }

        /// <summary>
        /// Helper method to check if a specific image is manually uploaded.
        /// An image is considered manually uploaded if:
        /// 1. It exists in the collection's metadata directory
        /// 2. It's NOT our auto-generated collage (smartlist-collage.jpg or smartlist-thumb-collage.jpg)
        /// 3. It doesn't match any collection item's image path
        /// </summary>
        private async Task<bool> IsManuallyUploadedImageAsync(ItemImageInfo imageInfo, string normalizedCollectionPath, BaseItem collection, ImageType imageType, CancellationToken cancellationToken)
        {
            var normalizedImagePath = System.IO.Path.GetFullPath(imageInfo.Path);

            // Check if image is in the collection's metadata directory
            if (!normalizedImagePath.StartsWith(normalizedCollectionPath, StringComparison.OrdinalIgnoreCase))
            {
                // Image is not in collection's directory, so it's from an item (auto-generated or referenced)
                // This is safe to overwrite
                return false;
            }

            // Image is in collection's metadata directory
            // Check if it's our auto-generated collage
            var fileName = System.IO.Path.GetFileName(normalizedImagePath);
            if (string.Equals(fileName, "smartlist-collage.jpg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, "smartlist-thumb-collage.jpg", StringComparison.OrdinalIgnoreCase))
            {
                // This is our auto-generated collage - safe to overwrite (will regenerate to match current items)
                return false;
            }

            // Check if it matches any of the collection items' images
            try
            {
                var items = await GetCollectionItemsAsync(collection, cancellationToken).ConfigureAwait(false);

                // Get the appropriate items list based on image type
                var itemsWithImages = imageType == ImageType.Thumb
                    ? GetItemsWithThumbImages(items)
                    : GetItemsWithImages(items);

                // Check if any item uses this exact image path
                foreach (var item in itemsWithImages)
                {
                    if (item.ImageInfos != null)
                    {
                        var itemImage = item.ImageInfos.FirstOrDefault(i => i.Type == imageType);
                        if (itemImage != null && !string.IsNullOrEmpty(itemImage.Path))
                        {
                            var normalizedItemPath = System.IO.Path.GetFullPath(itemImage.Path);
                            if (normalizedImagePath.Equals(normalizedItemPath, StringComparison.OrdinalIgnoreCase))
                            {
                                // Image matches an item's image, so it's auto-generated/referenced
                                // Safe to overwrite (will regenerate to match current items)
                                return false;
                            }
                        }
                    }
                }

                // Image is in collection's metadata directory but doesn't match any item's image
                // and is not our auto-generated collage - this is likely a manually uploaded image
                _logger.LogDebug("Collection {CollectionName} has {ImageType} image '{ImagePath}' in metadata directory that doesn't match any item's image - treating as manually uploaded",
                    collection.Name, imageType, fileName);
                return true;
            }
            catch (Exception ex)
            {
                // If we can't check, be conservative and preserve the image
                _logger.LogWarning(ex, "Could not verify if collection {CollectionName} has manually uploaded {ImageType} image, preserving existing image", collection.Name, imageType);
                return true;
            }
        }

        /// <summary>
        /// Gets all items in a collection using GetLinkedChildren method.
        /// </summary>
        private Task<List<BaseItem>> GetCollectionItemsAsync(BaseItem collection, CancellationToken cancellationToken)
        {
            List<BaseItem> items = [];

            try
            {
                var getLinkedChildrenMethod = collection.GetType().GetMethod("GetLinkedChildren", Type.EmptyTypes);
                if (getLinkedChildrenMethod != null)
                {
                    var linkedChildren = getLinkedChildrenMethod.Invoke(collection, null);
                    if (linkedChildren is IEnumerable<BaseItem> linkedEnumerable)
                    {
                        items = linkedEnumerable.ToList();
                        _logger.LogDebug("GetLinkedChildren method returned {Count} items for collection {CollectionName}", 
                            items.Count, collection.Name);
                        return Task.FromResult(items);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetLinkedChildren method failed for collection {CollectionName}: {Error}", 
                    collection.Name, ex.Message);
            }

            return Task.FromResult(items);
        }

        /// <summary>
        /// Filters items to those with primary images, preferring Movies and Series.
        /// For Episodes, fetches and returns their parent Series instead.
        /// Returns items in their original order (first items first).
        /// </summary>
        private List<BaseItem> GetItemsWithImages(List<BaseItem> items)
        {
            // First, get Movies and Series with images (preserving order)
            var mediaItemsWithImages = items
                .Where(item => (item is Movie || item is Series) &&
                               item.ImageInfos != null &&
                               item.ImageInfos.Any(i => i.Type == ImageType.Primary))
                .ToList();

            // If we have media items with images, use those
            if (mediaItemsWithImages.Count > 0)
            {
                return mediaItemsWithImages;
            }

            // Check if we have episodes - if so, fetch their parent series
            var episodes = items.OfType<Episode>().ToList();
            if (episodes.Count > 0)
            {
                _logger.LogDebug("Collection contains {EpisodeCount} episodes, fetching parent series for poster generation", episodes.Count);

                var seriesItems = new List<BaseItem>();
                var seenSeriesIds = new HashSet<Guid>();

                foreach (var episode in episodes)
                {
                    if (episode.SeriesId != Guid.Empty && !seenSeriesIds.Contains(episode.SeriesId))
                    {
                        var parentSeries = _libraryManager.GetItemById(episode.SeriesId);
                        if (parentSeries is Series series &&
                            series.ImageInfos != null &&
                            series.ImageInfos.Any(i => i.Type == ImageType.Primary))
                        {
                            seriesItems.Add(series);
                            seenSeriesIds.Add(episode.SeriesId);
                            _logger.LogDebug("Added parent series '{SeriesName}' for episode '{EpisodeName}'",
                                series.Name, episode.Name);
                        }
                    }
                }

                if (seriesItems.Count > 0)
                {
                    _logger.LogDebug("Using {SeriesCount} unique parent series for collection poster", seriesItems.Count);
                    return seriesItems;
                }
            }

            // Check if we have audio items (music tracks)
            var audioItems = items.OfType<Audio>().ToList();
            if (audioItems.Count > 0)
            {
                _logger.LogDebug("Collection contains {AudioCount} audio items, checking for images", audioItems.Count);

                var imageItems = new List<BaseItem>();
                var seenAlbumIds = new HashSet<Guid>();

                foreach (var audio in audioItems)
                {
                    // First, check if the audio item itself has a Primary image
                    if (audio.ImageInfos != null && audio.ImageInfos.Any(i => i.Type == ImageType.Primary))
                    {
                        imageItems.Add(audio);
                        _logger.LogDebug("Using audio item's own image for '{AudioName}'", audio.Name);
                    }
                    else
                    {
                        // Fall back to parent album's image
                        var albumId = audio.ParentId;
                        if (albumId != Guid.Empty && !seenAlbumIds.Contains(albumId))
                        {
                            var parentAlbum = _libraryManager.GetItemById(albumId);
                            if (parentAlbum is MusicAlbum album &&
                                album.ImageInfos != null &&
                                album.ImageInfos.Any(i => i.Type == ImageType.Primary))
                            {
                                imageItems.Add(album);
                                seenAlbumIds.Add(albumId);
                                _logger.LogDebug("Using parent album '{AlbumName}' image for audio '{AudioName}'",
                                    album.Name, audio.Name);
                            }
                        }
                    }

                    // Stop once we have enough items for the collage (4 max)
                    if (imageItems.Count >= 4)
                    {
                        break;
                    }
                }

                if (imageItems.Count > 0)
                {
                    _logger.LogDebug("Using {ImageCount} items with images for collection poster", imageItems.Count);
                    return imageItems;
                }
            }

            // Fallback: use any items with images (preserving order)
            return items
                .Where(item => item.ImageInfos != null &&
                               item.ImageInfos.Any(i => i.Type == ImageType.Primary))
                .ToList();
        }

        /// <summary>
        /// Filters items to those with thumb images, preferring Movies and Series.
        /// For Episodes, fetches and returns their parent Series instead.
        /// Returns items in their original order (first items first).
        /// Returns empty list if no thumb images are available.
        /// </summary>
        private List<BaseItem> GetItemsWithThumbImages(List<BaseItem> items)
        {
            // First, try to get Movies and Series with thumb images (preserving order)
            var mediaItemsWithThumbImages = items
                .Where(item => (item is Movie || item is Series) &&
                               item.ImageInfos != null &&
                               item.ImageInfos.Any(i => i.Type == ImageType.Thumb))
                .ToList();

            // If we have media items with thumb images, use those
            if (mediaItemsWithThumbImages.Count > 0)
            {
                return mediaItemsWithThumbImages;
            }

            // Check if we have episodes - if so, fetch their parent series with thumb images
            var episodes = items.OfType<Episode>().ToList();
            if (episodes.Count > 0)
            {
                _logger.LogDebug("Collection contains {EpisodeCount} episodes, fetching parent series for thumb generation", episodes.Count);

                var seriesItems = new List<BaseItem>();
                var seenSeriesIds = new HashSet<Guid>();

                foreach (var episode in episodes)
                {
                    if (episode.SeriesId != Guid.Empty && !seenSeriesIds.Contains(episode.SeriesId))
                    {
                        var parentSeries = _libraryManager.GetItemById(episode.SeriesId);
                        if (parentSeries is Series series &&
                            series.ImageInfos != null &&
                            series.ImageInfos.Any(i => i.Type == ImageType.Thumb))
                        {
                            seriesItems.Add(series);
                            seenSeriesIds.Add(episode.SeriesId);
                            _logger.LogDebug("Added parent series '{SeriesName}' with thumb for episode '{EpisodeName}'",
                                series.Name, episode.Name);
                        }
                    }
                }

                if (seriesItems.Count > 0)
                {
                    _logger.LogDebug("Using {SeriesCount} unique parent series with thumbs for collection thumb", seriesItems.Count);
                    return seriesItems;
                }
            }

            // Check if we have audio items - check for thumb images
            var audioItems = items.OfType<Audio>().ToList();
            if (audioItems.Count > 0)
            {
                _logger.LogDebug("Collection contains {AudioCount} audio items, checking for thumb images", audioItems.Count);

                var imageItems = new List<BaseItem>();
                var seenAlbumIds = new HashSet<Guid>();

                foreach (var audio in audioItems)
                {
                    // First, check if the audio item itself has a Thumb image
                    if (audio.ImageInfos != null && audio.ImageInfos.Any(i => i.Type == ImageType.Thumb))
                    {
                        imageItems.Add(audio);
                        _logger.LogDebug("Using audio item's own thumb for '{AudioName}'", audio.Name);
                    }
                    else
                    {
                        // Fall back to parent album's thumb image
                        var albumId = audio.ParentId;
                        if (albumId != Guid.Empty && !seenAlbumIds.Contains(albumId))
                        {
                            var parentAlbum = _libraryManager.GetItemById(albumId);
                            if (parentAlbum is MusicAlbum album &&
                                album.ImageInfos != null &&
                                album.ImageInfos.Any(i => i.Type == ImageType.Thumb))
                            {
                                imageItems.Add(album);
                                seenAlbumIds.Add(albumId);
                                _logger.LogDebug("Using parent album '{AlbumName}' thumb for audio '{AudioName}'",
                                    album.Name, audio.Name);
                            }
                        }
                    }

                    // Stop once we have enough items for the collage (4 max)
                    if (imageItems.Count >= 4)
                    {
                        break;
                    }
                }

                if (imageItems.Count > 0)
                {
                    _logger.LogDebug("Using {ImageCount} items with thumb images for collection thumb", imageItems.Count);
                    return imageItems;
                }
            }

            // Fallback: try any items with thumb images (preserving order)
            var anyItemsWithThumb = items
                .Where(item => item.ImageInfos != null &&
                               item.ImageInfos.Any(i => i.Type == ImageType.Thumb))
                .ToList();

            if (anyItemsWithThumb.Count > 0)
            {
                return anyItemsWithThumb;
            }

            // No thumb images found - return empty list (don't generate thumb if no thumbs exist)
            _logger.LogDebug("No thumb images found in collection, skipping thumb generation");
            return [];
        }

        /// <summary>
        /// Creates and sets the collection image: single image for 1 item, 4-image collage for 2+ items.
        /// Always uses the first item(s) with images from the collection.
        /// </summary>
        private async Task CreateCollectionImageAsync(BaseItem collection, List<BaseItem> itemsWithImages, CancellationToken cancellationToken)
        {
            if (itemsWithImages.Count == 0)
            {
                return;
            }

            if (itemsWithImages.Count == 1)
            {
                // Single item: use the first (and only) item's image directly
                // First, remove any existing auto-generated collage image file if it exists
                var collectionPath = collection.Path;
                if (!string.IsNullOrEmpty(collectionPath))
                {
                    var oldCollagePath = System.IO.Path.Combine(collectionPath, "smartlist-collage.jpg");
                    if (System.IO.File.Exists(oldCollagePath))
                    {
                        try
                        {
                            System.IO.File.Delete(oldCollagePath);
                            _logger.LogDebug("Deleted old auto-generated collage image file for collection {CollectionName}", collection.Name);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete old collage image file for collection {CollectionName}", collection.Name);
                        }
                    }
                }

                var item = itemsWithImages[0];
                var imageInfo = item.ImageInfos!.First(i => i.Type == ImageType.Primary);

                if (!string.IsNullOrEmpty(imageInfo.Path))
                {
                    collection.SetImage(new ItemImageInfo
                    {
                        Path = imageInfo.Path,
                        Type = ImageType.Primary
                    }, 0);

                    await _libraryManager.UpdateItemAsync(
                        collection,
                        collection.GetParent(),
                        ItemUpdateType.ImageUpdate,
                        cancellationToken);
                    _logger.LogInformation("Successfully set single image for collection {CollectionName} from first item: {ItemName}",
                        collection.Name, item.Name);
                }
            }
            else
            {
                // 2+ items: create 4-image collage using the first items
                await CreateImageCollageAsync(collection, itemsWithImages, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Creates and sets the collection thumb image: single image for 1 item, 4-image collage for 2+ items.
        /// Only uses actual thumb images from the collection items (no fallback to primary images).
        /// </summary>
        private async Task CreateCollectionThumbAsync(BaseItem collection, List<BaseItem> itemsWithThumbImages, CancellationToken cancellationToken)
        {
            if (itemsWithThumbImages.Count == 0)
            {
                return;
            }

            if (itemsWithThumbImages.Count == 1)
            {
                // Single item: use the first (and only) item's thumb image directly
                // First, remove any existing auto-generated thumb collage image file if it exists
                var collectionPath = collection.Path;
                if (!string.IsNullOrEmpty(collectionPath))
                {
                    var oldThumbCollagePath = System.IO.Path.Combine(collectionPath, "smartlist-thumb-collage.jpg");
                    if (System.IO.File.Exists(oldThumbCollagePath))
                    {
                        try
                        {
                            System.IO.File.Delete(oldThumbCollagePath);
                            _logger.LogDebug("Deleted old auto-generated thumb collage image file for collection {CollectionName}", collection.Name);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete old thumb collage image file for collection {CollectionName}", collection.Name);
                        }
                    }
                }

                var item = itemsWithThumbImages[0];
                var thumbImageInfo = item.ImageInfos?.FirstOrDefault(i => i.Type == ImageType.Thumb);

                if (thumbImageInfo != null && !string.IsNullOrEmpty(thumbImageInfo.Path))
                {
                    collection.SetImage(new ItemImageInfo
                    {
                        Path = thumbImageInfo.Path,
                        Type = ImageType.Thumb
                    }, 0);

                    await _libraryManager.UpdateItemAsync(
                        collection,
                        collection.GetParent(),
                        ItemUpdateType.ImageUpdate,
                        cancellationToken);
                    _logger.LogInformation("Successfully set single thumb image for collection {CollectionName} from first item: {ItemName}",
                        collection.Name, item.Name);
                }
            }
            else
            {
                // 2+ items: create 4-image thumb collage using the first items
                await CreateThumbCollageAsync(collection, itemsWithThumbImages, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Creates a 4-image collage from collection items and sets it as the collection's primary image.
        /// Uses the first items with valid images (duplicates if needed to fill 4 slots).
        /// </summary>
        private async Task CreateImageCollageAsync(BaseItem collection, List<BaseItem> itemsWithImages, CancellationToken cancellationToken)
        {
            try
            {
                // Select up to 4 items from the first items with images (duplicate if needed to fill 4 slots)
                var selectedItems = new List<BaseItem>();
                for (int i = 0; i < 4; i++)
                {
                    selectedItems.Add(itemsWithImages[i % itemsWithImages.Count]);
                }

                _logger.LogDebug("Creating 4-image collage for collection {CollectionName} from {ItemCount} items (using first items: {ItemNames})", 
                    collection.Name, itemsWithImages.Count,
                    string.Join(", ", selectedItems.Take(Math.Min(4, itemsWithImages.Count)).Select(i => i.Name)));

                // Get image paths
                var imagePaths = new List<string>();
                foreach (var item in selectedItems)
                {
                    var imageInfo = item.ImageInfos!.First(i => i.Type == ImageType.Primary);
                    if (!string.IsNullOrEmpty(imageInfo.Path) && System.IO.File.Exists(imageInfo.Path))
                    {
                        imagePaths.Add(imageInfo.Path);
                    }
                }

                if (imagePaths.Count == 0)
                {
                    _logger.LogWarning("No valid image paths found for collage creation for collection {CollectionName}", collection.Name);
                    return;
                }

                // Get collection's metadata directory
                var collectionPath = collection.Path;
                if (string.IsNullOrEmpty(collectionPath))
                {
                    _logger.LogWarning("Collection {CollectionName} has no path, cannot save collage image", collection.Name);
                    return;
                }

                // Use a specific filename for our auto-generated collage to avoid conflicts with user-uploaded poster.jpg
                var metadataPath = System.IO.Path.Combine(collectionPath, "smartlist-collage.jpg");
                var metadataDir = System.IO.Path.GetDirectoryName(metadataPath);
                if (metadataDir != null && !System.IO.Directory.Exists(metadataDir))
                {
                    System.IO.Directory.CreateDirectory(metadataDir);
                }

                // Create collage: 2x2 grid
                // Standard Jellyfin poster size is typically 300x450 (2:3 aspect ratio)
                // For 2x2 grid, we'll use 600x900 (each quadrant is 300x450)
                const int collageWidth = 600;
                const int collageHeight = 900;
                const int quadrantWidth = 300;
                const int quadrantHeight = 450;

                using (var collage = new Image<Rgba32>(collageWidth, collageHeight))
                {
                    // Fill background with black
                    collage.Mutate(x => x.BackgroundColor(Color.Black));

                    // Position images in 2x2 grid
                    var positions = new[]
                    {
                        new { X = 0, Y = 0 },           // Top-left
                        new { X = quadrantWidth, Y = 0 }, // Top-right
                        new { X = 0, Y = quadrantHeight }, // Bottom-left
                        new { X = quadrantWidth, Y = quadrantHeight } // Bottom-right
                    };

                    for (int i = 0; i < 4 && i < imagePaths.Count; i++)
                    {
                        try
                        {
                            using (var sourceImage = await Image.LoadAsync(imagePaths[i], cancellationToken).ConfigureAwait(false))
                            {
                                // Resize image to fit quadrant while maintaining aspect ratio
                                var resizeOptions = new ResizeOptions
                                {
                                    Size = new Size(quadrantWidth, quadrantHeight),
                                    Mode = ResizeMode.Crop,
                                    Position = AnchorPositionMode.Center
                                };

                                sourceImage.Mutate(x => x.Resize(resizeOptions));

                                // Draw image at position
                                var point = new Point(positions[i].X, positions[i].Y);
                                collage.Mutate(ctx => ctx.DrawImage(sourceImage, point, 1.0f));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to load image {ImagePath} for collage, skipping", imagePaths[i]);
                        }
                    }

                    // Save collage
                    await collage.SaveAsync(metadataPath, new JpegEncoder { Quality = 90 }, cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug("Created collage image at {ImagePath}", metadataPath);
                }

                // Set the collage as the collection's primary image
                collection.SetImage(new ItemImageInfo
                {
                    Path = metadataPath,
                    Type = ImageType.Primary
                }, 0);

                await _libraryManager.UpdateItemAsync(
                    collection,
                    collection.GetParent(),
                    ItemUpdateType.ImageUpdate,
                    cancellationToken);
                _logger.LogInformation("Successfully set 4-image collage for collection {CollectionName}", collection.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating collage image for collection {CollectionName}", collection.Name);
            }
        }

        /// <summary>
        /// Creates a 4-image thumb collage from collection items and sets it as the collection's thumb image.
        /// Uses the first items with valid thumb images (duplicates if needed to fill 4 slots).
        /// Creates a 16:9 aspect ratio image suitable for landscape views.
        /// </summary>
        private async Task CreateThumbCollageAsync(BaseItem collection, List<BaseItem> itemsWithThumbImages, CancellationToken cancellationToken)
        {
            try
            {
                // Select up to 4 items from the first items with images (duplicate if needed to fill 4 slots)
                var selectedItems = new List<BaseItem>();
                for (int i = 0; i < 4; i++)
                {
                    selectedItems.Add(itemsWithThumbImages[i % itemsWithThumbImages.Count]);
                }

                _logger.LogDebug("Creating 4-image thumb collage for collection {CollectionName} from {ItemCount} items (using first items: {ItemNames})", 
                    collection.Name, itemsWithThumbImages.Count,
                    string.Join(", ", selectedItems.Take(Math.Min(4, itemsWithThumbImages.Count)).Select(i => i.Name)));

                // Get thumb image paths only
                var imagePaths = new List<string>();
                foreach (var item in selectedItems)
                {
                    var thumbImageInfo = item.ImageInfos?.FirstOrDefault(i => i.Type == ImageType.Thumb);
                    if (thumbImageInfo != null && !string.IsNullOrEmpty(thumbImageInfo.Path) && System.IO.File.Exists(thumbImageInfo.Path))
                    {
                        imagePaths.Add(thumbImageInfo.Path);
                    }
                }

                if (imagePaths.Count == 0)
                {
                    _logger.LogWarning("No valid image paths found for thumb collage creation for collection {CollectionName}", collection.Name);
                    return;
                }

                // Get collection's metadata directory
                var collectionPath = collection.Path;
                if (string.IsNullOrEmpty(collectionPath))
                {
                    _logger.LogWarning("Collection {CollectionName} has no path, cannot save thumb collage image", collection.Name);
                    return;
                }

                // Use a specific filename for our auto-generated thumb collage
                var metadataPath = System.IO.Path.Combine(collectionPath, "smartlist-thumb-collage.jpg");
                var metadataDir = System.IO.Path.GetDirectoryName(metadataPath);
                if (metadataDir != null && !System.IO.Directory.Exists(metadataDir))
                {
                    System.IO.Directory.CreateDirectory(metadataDir);
                }

                // Create thumb collage: 2x2 grid in 16:9 aspect ratio
                // Standard 1080p resolution: 1920x1080 (each quadrant is 960x540)
                const int collageWidth = 1920;
                const int collageHeight = 1080;
                const int quadrantWidth = 960;
                const int quadrantHeight = 540;

                using (var collage = new Image<Rgba32>(collageWidth, collageHeight))
                {
                    // Fill background with black
                    collage.Mutate(x => x.BackgroundColor(Color.Black));

                    // Position images in 2x2 grid
                    var positions = new[]
                    {
                        new { X = 0, Y = 0 },           // Top-left
                        new { X = quadrantWidth, Y = 0 }, // Top-right
                        new { X = 0, Y = quadrantHeight }, // Bottom-left
                        new { X = quadrantWidth, Y = quadrantHeight } // Bottom-right
                    };

                    for (int i = 0; i < 4 && i < imagePaths.Count; i++)
                    {
                        try
                        {
                            using (var sourceImage = await Image.LoadAsync(imagePaths[i], cancellationToken).ConfigureAwait(false))
                            {
                                // Resize image to fit quadrant while maintaining aspect ratio
                                // Crop to 16:9 aspect ratio for consistency
                                var resizeOptions = new ResizeOptions
                                {
                                    Size = new Size(quadrantWidth, quadrantHeight),
                                    Mode = ResizeMode.Crop,
                                    Position = AnchorPositionMode.Center
                                };

                                sourceImage.Mutate(x => x.Resize(resizeOptions));

                                // Draw image at position
                                var point = new Point(positions[i].X, positions[i].Y);
                                collage.Mutate(ctx => ctx.DrawImage(sourceImage, point, 1.0f));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to load image {ImagePath} for thumb collage, skipping", imagePaths[i]);
                        }
                    }

                    // Save collage
                    await collage.SaveAsync(metadataPath, new JpegEncoder { Quality = 90 }, cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug("Created thumb collage image at {ImagePath}", metadataPath);
                }

                // Set the collage as the collection's thumb image
                collection.SetImage(new ItemImageInfo
                {
                    Path = metadataPath,
                    Type = ImageType.Thumb
                }, 0);

                await _libraryManager.UpdateItemAsync(
                    collection,
                    collection.GetParent(),
                    ItemUpdateType.ImageUpdate,
                    cancellationToken);
                _logger.LogInformation("Successfully set 4-image thumb collage for collection {CollectionName}", collection.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating thumb collage image for collection {CollectionName}", collection.Name);
            }
        }

        /// <summary>
        /// Sets the DisplayOrder property of a collection to "Default" using reflection.
        /// This ensures that the collection respects the plugin's custom sort order.
        /// "Default" corresponds to "Date Modified" in Jellyfin's internal logic for Collections,
        /// which respects the order of items added to the collection.
        /// </summary>
        private void SetCollectionDisplayOrder(BaseItem collection)
        {
            try
            {
                // Use reflection to set DisplayOrder property to avoid hard dependency on specific Jellyfin versions
                var displayOrderProperty = collection.GetType().GetProperty("DisplayOrder");
                if (displayOrderProperty != null && displayOrderProperty.CanWrite)
                {
                    displayOrderProperty.SetValue(collection, "Default");
                    _logger.LogDebug("Set DisplayOrder to 'Default' for collection {CollectionName}", collection.Name);
                }
                else
                {
                    _logger.LogWarning("Cannot set DisplayOrder property on collection {CollectionName} - property not found or not writable", collection.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set DisplayOrder for collection {CollectionName}", collection.Name);
            }
        }

        /// <summary>
        /// Queries for items when IncludeOnly option is enabled for a field.
        /// This is a shared helper for both Collections and Playlists fields.
        /// </summary>
        /// <param name="dto">The collection DTO containing expression sets</param>
        /// <param name="fieldName">The field name to check (e.g., "Collections", "Playlists")</param>
        /// <param name="itemKind">The item kind to query (e.g., BoxSet, Playlist)</param>
        /// <param name="user">The user context for the query</param>
        /// <param name="itemTypeName">Human-readable item type name for logging (e.g., "collection", "playlist")</param>
        /// <returns>List of items if IncludeOnly is enabled, otherwise empty list</returns>
        private List<BaseItem> QueryIncludeOnlyItems(
            SmartCollectionDto dto,
            string fieldName,
            BaseItemKind itemKind,
            User user,
            string itemTypeName)
        {
            var hasIncludeOnly = dto.ExpressionSets?.Any(set =>
                set.Expressions?.Any(expr =>
                    expr.MemberName == fieldName && fieldName switch
                    {
                        "Collections" => expr.IncludeCollectionOnly == true,
                        "Playlists" => expr.IncludePlaylistOnly == true,
                        _ => false
                    }) == true) == true;

            if (!hasIncludeOnly)
            {
                return [];
            }

            _logger.LogDebug("Include{FieldName}Only is enabled - querying {ItemType}s for lookup", fieldName, itemTypeName);
            var query = new InternalItemsQuery(user)
            {
                IncludeItemTypes = [itemKind],
                Recursive = true,
            };
            var items = _libraryManager.GetItemsResult(query).Items.ToList();
            _logger.LogDebug("Found {ItemCount} {ItemType}s for lookup", items.Count, itemTypeName);
            
            return items;
        }

        /// <summary>
        /// Adds IncludeOnly items to the media lookup dictionary.
        /// This is a shared helper for adding both collections and playlists to the lookup.
        /// </summary>
        /// <param name="mediaLookup">The lookup dictionary to add items to</param>
        /// <param name="items">The items to add</param>
        private static void AddIncludeOnlyItemsToLookup(Dictionary<Guid, BaseItem> mediaLookup, List<BaseItem> items)
        {
            foreach (var item in items)
            {
                mediaLookup[item.Id] = item;
            }
        }

    }
}

