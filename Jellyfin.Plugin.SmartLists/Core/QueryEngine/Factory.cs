using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.Audio;
using Video = MediaBrowser.Controller.Entities.Video;
using Photo = MediaBrowser.Controller.Entities.Photo;
using Book = MediaBrowser.Controller.Entities.Book;

using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartLists.Core.Constants;
using Jellyfin.Plugin.SmartLists.Utilities;
using RefreshQueueServiceRefreshCache = Jellyfin.Plugin.SmartLists.Services.Shared.RefreshQueueService.RefreshCache;
using CategorizedPeople = Jellyfin.Plugin.SmartLists.Services.Shared.RefreshQueueService.CategorizedPeople;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine
{
    /// <summary>
    /// Parameters object for GetMediaType operations to improve readability and maintainability.
    /// Uses ExtractionGroup flags for efficient storage while providing backward-compatible convenience properties.
    /// </summary>
    public class MediaTypeExtractionOptions
    {
        /// <summary>
        /// Flags indicating which extraction groups are required.
        /// </summary>
        public ExtractionGroup RequiredGroups { get; set; } = ExtractionGroup.None;

        // Convenience properties for backward compatibility - get/set that modify RequiredGroups
        public bool ExtractAudioLanguages
        {
            get => RequiredGroups.HasFlag(ExtractionGroup.AudioLanguages);
            set => RequiredGroups = value ? RequiredGroups | ExtractionGroup.AudioLanguages : RequiredGroups & ~ExtractionGroup.AudioLanguages;
        }

        public bool ExtractSubtitleLanguages
        {
            get => RequiredGroups.HasFlag(ExtractionGroup.AudioLanguages); // Same group as AudioLanguages
            set => RequiredGroups = value ? RequiredGroups | ExtractionGroup.AudioLanguages : RequiredGroups & ~ExtractionGroup.AudioLanguages;
        }

        public bool ExtractAudioQuality
        {
            get => RequiredGroups.HasFlag(ExtractionGroup.AudioQuality);
            set => RequiredGroups = value ? RequiredGroups | ExtractionGroup.AudioQuality : RequiredGroups & ~ExtractionGroup.AudioQuality;
        }

        public bool ExtractVideoQuality
        {
            get => RequiredGroups.HasFlag(ExtractionGroup.VideoQuality);
            set => RequiredGroups = value ? RequiredGroups | ExtractionGroup.VideoQuality : RequiredGroups & ~ExtractionGroup.VideoQuality;
        }

        public bool ExtractPeople
        {
            get => RequiredGroups.HasFlag(ExtractionGroup.People);
            set => RequiredGroups = value ? RequiredGroups | ExtractionGroup.People : RequiredGroups & ~ExtractionGroup.People;
        }

        public bool ExtractCollections
        {
            get => RequiredGroups.HasFlag(ExtractionGroup.Collections);
            set => RequiredGroups = value ? RequiredGroups | ExtractionGroup.Collections : RequiredGroups & ~ExtractionGroup.Collections;
        }

        public bool ExtractPlaylists
        {
            get => RequiredGroups.HasFlag(ExtractionGroup.Playlists);
            set => RequiredGroups = value ? RequiredGroups | ExtractionGroup.Playlists : RequiredGroups & ~ExtractionGroup.Playlists;
        }

        public bool ExtractNextUnwatched
        {
            get => RequiredGroups.HasFlag(ExtractionGroup.NextUnwatched);
            set => RequiredGroups = value ? RequiredGroups | ExtractionGroup.NextUnwatched : RequiredGroups & ~ExtractionGroup.NextUnwatched;
        }

        public bool ExtractSeriesName
        {
            get => RequiredGroups.HasFlag(ExtractionGroup.SeriesName);
            set => RequiredGroups = value ? RequiredGroups | ExtractionGroup.SeriesName : RequiredGroups & ~ExtractionGroup.SeriesName;
        }

        public bool ExtractParentSeriesTags
        {
            get => RequiredGroups.HasFlag(ExtractionGroup.ParentSeriesTags);
            set => RequiredGroups = value ? RequiredGroups | ExtractionGroup.ParentSeriesTags : RequiredGroups & ~ExtractionGroup.ParentSeriesTags;
        }

        public bool ExtractParentSeriesStudios
        {
            get => RequiredGroups.HasFlag(ExtractionGroup.ParentSeriesStudios);
            set => RequiredGroups = value ? RequiredGroups | ExtractionGroup.ParentSeriesStudios : RequiredGroups & ~ExtractionGroup.ParentSeriesStudios;
        }

        public bool ExtractParentSeriesGenres
        {
            get => RequiredGroups.HasFlag(ExtractionGroup.ParentSeriesGenres);
            set => RequiredGroups = value ? RequiredGroups | ExtractionGroup.ParentSeriesGenres : RequiredGroups & ~ExtractionGroup.ParentSeriesGenres;
        }

        public bool ExtractLastEpisodeAirDate
        {
            get => RequiredGroups.HasFlag(ExtractionGroup.LastEpisodeAirDate);
            set => RequiredGroups = value ? RequiredGroups | ExtractionGroup.LastEpisodeAirDate : RequiredGroups & ~ExtractionGroup.LastEpisodeAirDate;
        }

        // Non-flag properties
        public bool IncludeUnwatchedSeries { get; set; } = true;
        public List<string> AdditionalUserIds { get; set; } = [];
        public string? OriginListName { get; set; } = null; // Name of the playlist/collection being built (to prevent self-reference)
        public int CollectionRecursionDepth { get; set; } = 1; // How deep to traverse nested collections (0-10, where 0 = direct members only). Note: Playlists don't support nesting, so no recursion depth for playlists.

        /// <summary>
        /// Creates extraction options from FieldRequirements.
        /// </summary>
        public static MediaTypeExtractionOptions FromRequirements(FieldRequirements requirements, string? originListName = null, int collectionDepth = 1)
        {
            return new MediaTypeExtractionOptions
            {
                RequiredGroups = requirements.RequiredGroups,
                IncludeUnwatchedSeries = requirements.IncludeUnwatchedSeries,
                AdditionalUserIds = requirements.AdditionalUserIds,
                OriginListName = originListName,
                CollectionRecursionDepth = collectionDepth,
            };
        }
    }

    internal sealed class OperandFactory
    {
        // Cache reflection method lookups for better performance - using ConcurrentDictionary for thread safety
        private static readonly ConcurrentDictionary<Type, System.Reflection.MethodInfo?> _getMediaStreamsMethodCache = new();
        private static readonly ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> _mediaSourcesPropertyCache = new();
        private static System.Reflection.MethodInfo? _getPeopleMethodCache = null;
        private static readonly object _getPeopleMethodLock = new();

        // Known unsupported types to avoid logging noise
        private static readonly HashSet<string> _knownUnsupportedTypes = new()
        {
            "CollectionFolder", "UserRootFolder", "AggregateFolder", "Folder",
        };

        /// <summary>
        /// Shared helper to extract media streams from a BaseItem using reflection.
        /// Reduces code duplication across AudioLanguages/Resolution/Framerate/VideoQuality extraction methods.
        /// </summary>
        private static List<object> TryGetAllMediaStreams(BaseItem baseItem, ILogger? logger)
        {
            var mediaStreams = new List<object>();

            try
            {
                var baseItemType = baseItem.GetType();

                // Approach 1: Try GetMediaStreams method if it exists (with caching)
                // Note: Use TryGetValue to avoid caching null values, which would throw in GetOrAdd
                System.Reflection.MethodInfo? getMediaStreamsMethod;
                if (!_getMediaStreamsMethodCache.TryGetValue(baseItemType, out getMediaStreamsMethod))
                {
                    getMediaStreamsMethod = baseItemType.GetMethod("GetMediaStreams");
                    if (getMediaStreamsMethod != null)
                    {
                        _getMediaStreamsMethodCache.TryAdd(baseItemType, getMediaStreamsMethod);
                    }
                }

                if (getMediaStreamsMethod != null)
                {
                    try
                    {
                        var result = getMediaStreamsMethod.Invoke(baseItem, null);
                        if (result is IEnumerable<object> streamEnum)
                        {
                            mediaStreams.AddRange(streamEnum);
                        }
                        else if (result != null)
                        {
                            logger?.LogDebug("GetMediaStreams method for item {Name} returned a non-enumerable type: {Type}",
                                baseItem.Name, result.GetType().FullName);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogDebug(ex, "Failed to call GetMediaStreams method for item {Name}", baseItem.Name);
                    }
                }

                // Approach 2: Look for MediaSources property (with caching)
                // Note: Use TryGetValue to avoid caching null values, which would throw in GetOrAdd
                System.Reflection.PropertyInfo? mediaSourcesProperty;
                if (!_mediaSourcesPropertyCache.TryGetValue(baseItemType, out mediaSourcesProperty))
                {
                    mediaSourcesProperty = baseItemType.GetProperty("MediaSources");
                    if (mediaSourcesProperty != null)
                    {
                        _mediaSourcesPropertyCache.TryAdd(baseItemType, mediaSourcesProperty);
                    }
                }

                if (mediaSourcesProperty != null)
                {
                    var mediaSources = mediaSourcesProperty.GetValue(baseItem);
                    if (mediaSources is IEnumerable<object> sourceEnum)
                    {
                        foreach (var source in sourceEnum)
                        {
                            try
                            {
                                var streamsProperty = source.GetType().GetProperty("MediaStreams");
                                if (streamsProperty != null)
                                {
                                    var streams = streamsProperty.GetValue(source);
                                    if (streams is IEnumerable<object> streamList)
                                    {
                                        mediaStreams.AddRange(streamList);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                logger?.LogDebug(ex, "Failed to process MediaSource for item {Name}", baseItem.Name);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to extract media streams for item {Name}", baseItem.Name);
            }

            return mediaStreams;
        }

        // Cache episode property lookups for better performance - using ConcurrentDictionary for thread safety
        private static readonly ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> _parentIndexPropertyCache = new();
        private static readonly ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> _indexPropertyCache = new();
        private static readonly ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> _seriesIdPropertyCache = new();


        /// <summary>
        /// Calculates the playback status for a media item.
        /// </summary>
        /// <param name="userData">User data for the item</param>
        /// <returns>"Played", "InProgress", or "Unplayed"</returns>
        private static string CalculatePlaybackStatus(UserItemData? userData)
        {
            if (userData == null)
            {
                return "Unplayed";
            }

            // Check Jellyfin's Played flag first (authoritative)
            if (userData.Played)
            {
                return "Played";
            }

            // Check if partially watched
            if (userData.PlaybackPositionTicks > 0)
            {
                return "InProgress";
            }

            return "Unplayed";
        }

        /// <summary>
        /// Calculates playback status from a reflected userData object using reflection.
        /// Mirrors the logic of CalculatePlaybackStatus but works with reflected objects.
        /// </summary>
        /// <param name="reflectedUserData">The reflected userData object</param>
        /// <returns>"Played", "InProgress", or "Unplayed"</returns>
        private static string CalculatePlaybackStatusFromReflected(object reflectedUserData)
        {
            if (reflectedUserData == null)
            {
                return "Unplayed";
            }

            var userDataType = reflectedUserData.GetType();

            // Check Played property
            var playedProp = userDataType.GetProperty("Played");
            if (playedProp != null)
            {
                var playedValue = playedProp.GetValue(reflectedUserData);
                if (playedValue is bool isPlayed && isPlayed)
                {
                    return "Played";
                }
            }

            // Check PlaybackPositionTicks property
            var playbackPositionTicksProp = userDataType.GetProperty("PlaybackPositionTicks");
            if (playbackPositionTicksProp != null)
            {
                var ticksValue = playbackPositionTicksProp.GetValue(reflectedUserData);
                var ticks = ExtractLongValue(ticksValue);
                if (ticks.HasValue && ticks.Value > 0)
                {
                    return "InProgress";
                }
            }

            return "Unplayed";
        }

        /// <summary>
        /// Calculates playback status for a user based on item type.
        /// Handles both Series (episode-based) and other item types.
        /// </summary>
        /// <param name="baseItem">The base item</param>
        /// <param name="user">The user</param>
        /// <param name="libraryManager">Library manager to query episodes (for Series)</param>
        /// <param name="userDataManager">User data manager (can be null)</param>
        /// <param name="userData">User data for the item</param>
        /// <param name="cache">Cache for performance</param>
        /// <param name="logger">Logger</param>
        /// <returns>"Played", "InProgress", or "Unplayed"</returns>
        private static string CalculatePlaybackStatusForUser(
            BaseItem baseItem,
            User user,
            ILibraryManager libraryManager,
            IUserDataManager? userDataManager,
            UserItemData? userData,
            RefreshQueueServiceRefreshCache cache,
            ILogger? logger)
        {
            if (baseItem is Series series && userDataManager != null)
            {
                return CalculateSeriesPlaybackStatus(series, user, libraryManager, userDataManager, cache, logger);
            }
            else
            {
                return CalculatePlaybackStatus(userData);
            }
        }

        /// <summary>
        /// Calculates playback status for a Series based on episode watch counts.
        /// </summary>
        /// <param name="series">The series item</param>
        /// <param name="user">The user</param>
        /// <param name="libraryManager">Library manager to query episodes</param>
        /// <param name="userDataManager">User data manager</param>
        /// <param name="cache">Cache for performance</param>
        /// <param name="logger">Logger</param>
        /// <returns>"Played", "InProgress", or "Unplayed"</returns>
        private static string CalculateSeriesPlaybackStatus(
            Series series,
            User user,
            ILibraryManager libraryManager,
            IUserDataManager userDataManager,
            RefreshQueueServiceRefreshCache cache,
            ILogger? logger)
        {
            try
            {
                // Get valid episodes (excluding season 0 specials)
                var validEpisodes = GetValidSeriesEpisodes(series.Id, user, libraryManager, cache, logger);

                // Exclude series with 0 valid episodes (invalid data or only season 0 specials)
                if (validEpisodes.Count == 0)
                {
                    logger?.LogDebug("Series '{SeriesName}' has 0 valid episodes (excluding season 0), excluding from results", series.Name);
                    return "Unplayed"; // Will be filtered out by caller
                }

                // Use LINQ Count with cache retrieval to count watched episodes
                int watchedCount = validEpisodes.Count(e =>
                {
                    // Use UserDataCache to avoid redundant DB lookups
                    // Only cache non-null UserData to avoid polluting the cache
                    var cacheKey = (e.Id, user.Id);
                    if (!cache.UserDataCache.TryGetValue(cacheKey, out var userData))
                    {
                        userData = userDataManager.GetUserData(user, e);
                        // Only add to cache if non-null
                        if (userData != null)
                        {
                            cache.UserDataCache[cacheKey] = userData;
                        }
                    }
                    return userData != null && e.IsPlayed(user, userData);
                });
                int totalCount = validEpisodes.Count;

                // Determine status based on watched count
                if (watchedCount == totalCount)
                {
                    return "Played";
                }
                else if (watchedCount >= 1)
                {
                    return "InProgress";
                }
                else
                {
                    return "Unplayed";
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Error calculating playback status for series '{SeriesName}'", series.Name);
                return "Unplayed";
            }
        }

        /// <summary>
        /// Gets valid episodes for a series, excluding season 0 specials and episodes without valid metadata.
        /// </summary>
        /// <param name="seriesId">The series ID</param>
        /// <param name="user">The user</param>
        /// <param name="libraryManager">Library manager to query episodes</param>
        /// <param name="cache">Cache for performance</param>
        /// <param name="logger">Logger</param>
        /// <returns>List of valid episodes (season 1+, with valid episode numbers)</returns>
        private static List<BaseItem> GetValidSeriesEpisodes(
            Guid seriesId,
            User user,
            ILibraryManager libraryManager,
            RefreshQueueServiceRefreshCache cache,
            ILogger? logger)
        {
            // Get all episodes in the series using cache
            var episodes = GetCachedSeriesEpisodes(seriesId, user, libraryManager, cache, logger, isVirtualItem: false);

            var validEpisodes = new List<BaseItem>();
            foreach (var episode in episodes)
            {
                var episodeType = episode.GetType();
                var parentIndexProperty = _parentIndexPropertyCache.GetOrAdd(episodeType, type => type.GetProperty("ParentIndexNumber"));
                var indexProperty = _indexPropertyCache.GetOrAdd(episodeType, type => type.GetProperty("IndexNumber"));

                if (parentIndexProperty != null && indexProperty != null)
                {
                    var seasonNum = ExtractIntValue(parentIndexProperty.GetValue(episode));
                    var episodeNum = ExtractIntValue(indexProperty.GetValue(episode));

                    // Skip season 0 (specials) and only include episodes with valid season/episode numbers
                    if (seasonNum.HasValue && episodeNum.HasValue && seasonNum.Value > 0)
                    {
                        validEpisodes.Add(episode);
                    }
                }
            }

            return validEpisodes;
        }

        /// <summary>
        /// Calculates the most recent LastPlayedDate for a Series based on episode watch dates.
        /// </summary>
        /// <param name="series">The series item</param>
        /// <param name="user">The user</param>
        /// <param name="libraryManager">Library manager to query episodes</param>
        /// <param name="userDataManager">User data manager</param>
        /// <param name="cache">Cache for performance</param>
        /// <param name="logger">Logger</param>
        /// <returns>The most recent LastPlayedDate among all episodes, or null if no episodes have been played</returns>
        private static DateTime? CalculateSeriesLastPlayedDate(
            Series series,
            User user,
            ILibraryManager libraryManager,
            IUserDataManager userDataManager,
            RefreshQueueServiceRefreshCache cache,
            ILogger? logger)
        {
            try
            {
                // Get valid episodes (excluding season 0 specials)
                var validEpisodes = GetValidSeriesEpisodes(series.Id, user, libraryManager, cache, logger);

                // If no valid episodes, return null
                if (validEpisodes.Count == 0)
                {
                    logger?.LogDebug("Series '{SeriesName}' has 0 valid episodes (excluding season 0), returning null LastPlayedDate", series.Name);
                    return null;
                }

                // Track the maximum (most recent) LastPlayedDate
                DateTime? maxLastPlayedDate = null;

                foreach (var episode in validEpisodes)
                {
                    // Use UserDataCache to avoid redundant DB lookups
                    var cacheKey = (episode.Id, user.Id);
                    if (!cache.UserDataCache.TryGetValue(cacheKey, out var episodeUserData))
                    {
                        episodeUserData = userDataManager.GetUserData(user, episode);
                        // Cache the result if non-null
                        if (episodeUserData != null)
                        {
                            cache.UserDataCache[cacheKey] = episodeUserData;
                        }
                    }

                    if (episodeUserData != null)
                    {
                        // Extract LastPlayedDate using reflection (similar to PopulateUserData)
                        var userDataType = episodeUserData.GetType();
                        var lastPlayedDateProp = userDataType.GetProperty("LastPlayedDate");
                        if (lastPlayedDateProp != null)
                        {
                            var lastPlayedDateValue = lastPlayedDateProp.GetValue(episodeUserData);
                            // PropertyInfo.GetValue automatically unwraps Nullable<T>
                            // If lastPlayedDateValue is non-null, it's already the underlying DateTime
                            if (lastPlayedDateValue is DateTime dateTime && dateTime != DateTime.MinValue)
                            {
                                // Update max if this episode's date is more recent
                                logger?.LogDebug("Episode '{EpisodeName}' LastPlayedDate: {Date}", episode.Name, dateTime);
                                if (!maxLastPlayedDate.HasValue || dateTime > maxLastPlayedDate.Value)
                                {
                                    maxLastPlayedDate = dateTime;
                                }
                            }
                        }
                    }
                }

                logger?.LogDebug("Series '{SeriesName}' calculated LastPlayedDate: {Date} (from {EpisodeCount} episodes)", series.Name, maxLastPlayedDate, validEpisodes.Count);
                return maxLastPlayedDate;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Error calculating LastPlayedDate for series '{SeriesName}'", series.Name);
                return null;
            }
        }

        /// <summary>
        /// Sets fallback values for user-specific data when userData is unavailable or invalid.
        /// </summary>
        /// <param name="operand">The operand to populate</param>
        /// <param name="userId">The user ID (as string)</param>
        /// <param name="playbackStatus">The PlaybackStatus value to use</param>
        private static void SetUserDataFallbacks(Operand operand, string userId, string playbackStatus)
        {
            operand.PlaybackStatusByUser[userId] = playbackStatus;
            operand.PlayCountByUser[userId] = playbackStatus == "Played" ? 1 : 0;
            operand.IsFavoriteByUser[userId] = false;
            operand.LastPlayedDateByUser[userId] = -1; // Never played,
        }

        /// <summary>
        /// Helper method to categorize people by their type/role.
        /// This ensures we only have one place to maintain the categorization logic (DRY principle).
        /// </summary>
        /// <param name="peopleEnumerable">The enumerable of person objects from GetPeople</param>
        /// <param name="logger">Optional logger for debugging</param>
        /// <returns>Categorized people data</returns>
        private static CategorizedPeople CategorizePeople(IEnumerable<object> peopleEnumerable, ILogger? logger = null)
        {
            var categorized = new CategorizedPeople();
            var allPeopleNames = new HashSet<string>(); // Use HashSet to avoid duplicates
            var actorRolesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Case-insensitive deduplication for roles

            foreach (var person in peopleEnumerable)
            {
                if (person == null) continue;

                try
                {
                    // Extract Name property
                    var nameProperty = person.GetType().GetProperty("Name");
                    if (nameProperty == null) continue;

                    var name = nameProperty.GetValue(person) as string;
                    if (string.IsNullOrEmpty(name)) continue;

                    // Add to all people (only if not already present)
                    allPeopleNames.Add(name);

                    // Extract Type property to categorize
                    var typeProperty = person.GetType().GetProperty("Type");
                    if (typeProperty != null)
                    {
                        var typeValue = typeProperty.GetValue(person);
                        if (typeValue != null)
                        {
                            var typeString = typeValue.ToString();

                            // Categorize based on the Type enum value
                            switch (typeString)
                            {
                                case "Actor":
                                    if (!categorized.Actors.Contains(name))
                                    {
                                        categorized.Actors.Add(name);
                                    }
                                    // Extract Role property for actors (character name)
                                    var actorRoleProperty = person.GetType().GetProperty("Role");
                                    if (actorRoleProperty != null)
                                    {
                                        var roleValue = actorRoleProperty.GetValue(person) as string;
                                        if (!string.IsNullOrWhiteSpace(roleValue))
                                        {
                                            var trimmedRole = roleValue.Trim();
                                            actorRolesSet.Add(trimmedRole);
                                        }
                                    }
                                    break;
                                case "Director":
                                    if (!categorized.Directors.Contains(name))
                                    {
                                        categorized.Directors.Add(name);
                                    }
                                    break;
                                case "Composer":
                                    if (!categorized.Composers.Contains(name))
                                    {
                                        categorized.Composers.Add(name);
                                    }
                                    break;
                                case "Writer":
                                    if (!categorized.Writers.Contains(name))
                                    {
                                        categorized.Writers.Add(name);
                                    }
                                    break;
                                case "GuestStar":
                                    if (!categorized.GuestStars.Contains(name))
                                    {
                                        categorized.GuestStars.Add(name);
                                    }
                                    // Extract Role property for guest stars (character name)
                                    var guestRoleProperty = person.GetType().GetProperty("Role");
                                    if (guestRoleProperty != null)
                                    {
                                        var roleValue = guestRoleProperty.GetValue(person) as string;
                                        if (!string.IsNullOrWhiteSpace(roleValue))
                                        {
                                            var trimmedRole = roleValue.Trim();
                                            actorRolesSet.Add(trimmedRole);
                                        }
                                    }
                                    break;
                                case "Producer":
                                    if (!categorized.Producers.Contains(name))
                                    {
                                        categorized.Producers.Add(name);
                                    }
                                    break;
                                case "Conductor":
                                    if (!categorized.Conductors.Contains(name))
                                    {
                                        categorized.Conductors.Add(name);
                                    }
                                    break;
                                case "Lyricist":
                                    if (!categorized.Lyricists.Contains(name))
                                    {
                                        categorized.Lyricists.Add(name);
                                    }
                                    break;
                                case "Arranger":
                                    if (!categorized.Arrangers.Contains(name))
                                    {
                                        categorized.Arrangers.Add(name);
                                    }
                                    break;
                                case "SoundEngineer":
                                    if (!categorized.SoundEngineers.Contains(name))
                                    {
                                        categorized.SoundEngineers.Add(name);
                                    }
                                    break;
                                case "Mixer":
                                    if (!categorized.Mixers.Contains(name))
                                    {
                                        categorized.Mixers.Add(name);
                                    }
                                    break;
                                case "Remixer":
                                    if (!categorized.Remixers.Contains(name))
                                    {
                                        categorized.Remixers.Add(name);
                                    }
                                    break;
                                case "Creator":
                                    if (!categorized.Creators.Contains(name))
                                    {
                                        categorized.Creators.Add(name);
                                    }
                                    break;
                                case "Artist":
                                    if (!categorized.PersonArtists.Contains(name))
                                    {
                                        categorized.PersonArtists.Add(name);
                                    }
                                    break;
                                case "AlbumArtist":
                                    if (!categorized.PersonAlbumArtists.Contains(name))
                                    {
                                        categorized.PersonAlbumArtists.Add(name);
                                    }
                                    break;
                                case "Author":
                                    if (!categorized.Authors.Contains(name))
                                    {
                                        categorized.Authors.Add(name);
                                    }
                                    break;
                                case "Illustrator":
                                    if (!categorized.Illustrators.Contains(name))
                                    {
                                        categorized.Illustrators.Add(name);
                                    }
                                    break;
                                case "Penciler":
                                    if (!categorized.Pencilers.Contains(name))
                                    {
                                        categorized.Pencilers.Add(name);
                                    }
                                    break;
                                case "Inker":
                                    if (!categorized.Inkers.Contains(name))
                                    {
                                        categorized.Inkers.Add(name);
                                    }
                                    break;
                                case "Colorist":
                                    if (!categorized.Colorists.Contains(name))
                                    {
                                        categorized.Colorists.Add(name);
                                    }
                                    break;
                                case "Letterer":
                                    if (!categorized.Letterers.Contains(name))
                                    {
                                        categorized.Letterers.Add(name);
                                    }
                                    break;
                                case "CoverArtist":
                                    if (!categorized.CoverArtists.Contains(name))
                                    {
                                        categorized.CoverArtists.Add(name);
                                    }
                                    break;
                                case "Editor":
                                    if (!categorized.Editors.Contains(name))
                                    {
                                        categorized.Editors.Add(name);
                                    }
                                    break;
                                case "Translator":
                                    if (!categorized.Translators.Contains(name))
                                    {
                                        categorized.Translators.Add(name);
                                    }
                                    break;
                                // Add other types as needed, but they won't be categorized
                                default:
                                    logger?.LogDebug("Encountered uncategorized person type: {Type} for {Name}", typeString, name);
                                    break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Error categorizing person");
                }
            }

            categorized.AllPeople = allPeopleNames.ToList();
            categorized.ActorRoles = actorRolesSet.ToList();
            
            if (logger != null && actorRolesSet.Count > 0)
            {
                logger.LogDebug("SmartLists extracted {RoleCount} unique actor roles from cast", actorRolesSet.Count);
            }
            
            return categorized;
        }

        /// <summary>
        /// Populates user-specific data from userData into the operand.
        /// </summary>
        /// <param name="operand">The operand to populate</param>
        /// <param name="userId">The user ID (as string)</param>
        /// <param name="playbackStatus">The PlaybackStatus value</param>
        /// <param name="userData">The userData object to extract from</param>
        /// <param name="baseItem">The base item (for Series LastPlayedDate calculation)</param>
        /// <param name="user">The user (for Series LastPlayedDate calculation)</param>
        /// <param name="libraryManager">Library manager (for Series LastPlayedDate calculation)</param>
        /// <param name="userDataManager">User data manager (for Series LastPlayedDate calculation)</param>
        /// <param name="cache">Cache for performance (for Series LastPlayedDate calculation)</param>
        /// <param name="logger">Logger (for Series LastPlayedDate calculation)</param>
        private static void PopulateUserData(Operand operand, string userId, string playbackStatus, object userData,
            BaseItem baseItem, User user, ILibraryManager libraryManager, IUserDataManager? userDataManager,
            RefreshQueueServiceRefreshCache cache, ILogger? logger)
        {
            operand.PlaybackStatusByUser[userId] = playbackStatus;

            // Use reflection to safely extract properties from userData
            var userDataType = userData.GetType();

            // Extract PlayCount
            var playCountProp = userDataType.GetProperty("PlayCount");
            if (playCountProp != null)
            {
                var playCountValue = playCountProp.GetValue(userData);
                var playCount = ExtractIntValue(playCountValue);
                operand.PlayCountByUser[userId] = playCount.GetValueOrDefault(0);
            }
            else
            {
                operand.PlayCountByUser[userId] = 0;
            }

            // Extract IsFavorite
            var isFavoriteProp = userDataType.GetProperty("IsFavorite");
            if (isFavoriteProp != null)
            {
                var isFavoriteValue = isFavoriteProp.GetValue(userData);
                // Handle nullable bool - check if it's a bool or bool?
                bool isFavorite = false;
                if (isFavoriteValue != null)
                {
                    if (isFavoriteValue is bool boolValue)
                    {
                        isFavorite = boolValue;
                    }
                    else if (isFavoriteValue.GetType().IsGenericType && 
                             isFavoriteValue.GetType().GetGenericTypeDefinition() == typeof(Nullable<>) &&
                             isFavoriteValue.GetType().GetGenericArguments()[0] == typeof(bool))
                    {
                        // Handle nullable bool
                        var hasValueProp = isFavoriteValue.GetType().GetProperty("HasValue");
                        var valueProp = isFavoriteValue.GetType().GetProperty("Value");
                        if (hasValueProp != null && valueProp != null)
                        {
                            var hasValue = (bool)(hasValueProp.GetValue(isFavoriteValue) ?? false);
                            if (hasValue)
                            {
                                isFavorite = (bool)(valueProp.GetValue(isFavoriteValue) ?? false);
                            }
                        }
                    }
                }
                operand.IsFavoriteByUser[userId] = isFavorite;
            }
            else
            {
                operand.IsFavoriteByUser[userId] = false;
            }

            // Extract LastPlayedDate - handle both nullable and non-nullable DateTime
            // For Series, calculate from episodes; for other types, use direct extraction
            if (baseItem is Series series && userDataManager != null)
            {
                // Calculate LastPlayedDate from episodes for Series
                var seriesLastPlayedDate = CalculateSeriesLastPlayedDate(series, user, libraryManager, userDataManager, cache, logger);
                if (seriesLastPlayedDate.HasValue)
                {
                    var unixTimestamp = SafeToUnixTimeSeconds(seriesLastPlayedDate.Value);
                    logger?.LogDebug("Series '{SeriesName}' LastPlayedDate set to {Date} (Unix: {Unix})", series.Name, seriesLastPlayedDate.Value, unixTimestamp);
                    operand.LastPlayedDateByUser[userId] = unixTimestamp;
                }
                else
                {
                    logger?.LogDebug("Series '{SeriesName}' has no LastPlayedDate (no episodes watched)", series.Name);
                    operand.LastPlayedDateByUser[userId] = -1; // Never played - no episodes watched
                }
            }
            else
            {
                // Direct extraction for non-Series items
                var lastPlayedDateProp = userDataType.GetProperty("LastPlayedDate");
                if (lastPlayedDateProp != null)
                {
                    var lastPlayedDateValue = lastPlayedDateProp.GetValue(userData);
                    // PropertyInfo.GetValue automatically unwraps Nullable<T>
                    // If lastPlayedDateValue is non-null, it's already the underlying DateTime
                    if (lastPlayedDateValue is DateTime dateTime && dateTime != DateTime.MinValue)
                    {
                        operand.LastPlayedDateByUser[userId] = SafeToUnixTimeSeconds(dateTime);
                    }
                    else
                    {
                        operand.LastPlayedDateByUser[userId] = -1; // Never played
                    }
                }
                else
                {
                    operand.LastPlayedDateByUser[userId] = -1; // Never played - property not found
                }
            }
        }

        /// <summary>
        /// Safely extracts an integer value from a property, handling both nullable and non-nullable int properties.
        /// </summary>
        /// <param name="value">The property value to convert</param>
        /// <returns>Nullable int representing the extracted value</returns>
        private static int? ExtractIntValue(object? value)
        {
            if (value is int intValue)
                return intValue;
            if (value == null)
                return null;

            // Try to convert to int if it's some other numeric type
            try
            {
                return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static long? ExtractLongValue(object? value)
        {
            if (value is long longValue)
                return longValue;
            if (value == null)
                return null;

            // Try to convert to long if it's some other numeric type
            try
            {
                return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extracts audio languages from media streams.
        /// </summary>
        private static void ExtractAudioLanguages(Operand operand, BaseItem baseItem, RefreshQueueServiceRefreshCache? cache, ILogger? logger)
        {
            operand.AudioLanguages = [];
            operand.DefaultAudioLanguages = [];
            try
            {
                // Check cache first if available
                IEnumerable<object> mediaStreams;
                if (cache != null && cache.MediaStreamsCache.TryGetValue(baseItem.Id, out var cachedStreams))
                {
                    mediaStreams = cachedStreams;
                }
                else
                {
                    // Use shared helper to extract media streams
                    mediaStreams = TryGetAllMediaStreams(baseItem, logger);
                    // Cache the result if cache is available
                    if (cache != null)
                    {
                        cache.MediaStreamsCache[baseItem.Id] = mediaStreams;
                    }
                }

                // Process found streams
                foreach (var stream in mediaStreams)
                {
                    try
                    {
                        var typeProperty = stream.GetType().GetProperty("Type");
                        var languageProperty = stream.GetType().GetProperty("Language");
                        var isDefaultProperty = stream.GetType().GetProperty("IsDefault");

                        if (typeProperty != null)
                        {
                            var streamType = typeProperty.GetValue(stream);
                            var language = languageProperty?.GetValue(stream) as string;
                            var isDefault = isDefaultProperty?.GetValue(stream) as bool? ?? false;

                            // Check if it's an audio stream
                            if (streamType != null && streamType.ToString() == "Audio")
                            {
                                if (!string.IsNullOrEmpty(language))
                                {
                                    if (!operand.AudioLanguages.Contains(language))
                                    {
                                        operand.AudioLanguages.Add(language);
                                    }

                                    // Track default languages separately
                                    if (isDefault && !operand.DefaultAudioLanguages.Contains(language))
                                    {
                                        operand.DefaultAudioLanguages.Add(language);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogDebug(ex, "Failed to process individual stream for item {Name}", baseItem.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to extract audio languages for item {Name}", baseItem.Name);
            }
        }

        /// <summary>
        /// Extracts subtitle languages from media streams.
        /// </summary>
        private static void ExtractSubtitleLanguages(Operand operand, BaseItem baseItem, RefreshQueueServiceRefreshCache? cache, ILogger? logger)
        {
            operand.SubtitleLanguages = [];
            try
            {
                // Check cache first if available
                IEnumerable<object> mediaStreams;
                if (cache != null && cache.MediaStreamsCache.TryGetValue(baseItem.Id, out var cachedStreams))
                {
                    mediaStreams = cachedStreams;
                }
                else
                {
                    // Use shared helper to extract media streams
                    mediaStreams = TryGetAllMediaStreams(baseItem, logger);
                    // Cache the result if cache is available
                    if (cache != null)
                    {
                        cache.MediaStreamsCache[baseItem.Id] = mediaStreams;
                    }
                }

                // Process found streams
                foreach (var stream in mediaStreams)
                {
                    try
                    {
                        var typeProperty = stream.GetType().GetProperty("Type");
                        var languageProperty = stream.GetType().GetProperty("Language");

                        if (typeProperty != null)
                        {
                            var streamType = typeProperty.GetValue(stream);
                            var language = languageProperty?.GetValue(stream) as string;

                            // Check if it's a subtitle stream
                            if (streamType != null && streamType.ToString() == "Subtitle")
                            {
                                if (!string.IsNullOrEmpty(language))
                                {
                                    if (!operand.SubtitleLanguages.Contains(language))
                                    {
                                        operand.SubtitleLanguages.Add(language);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogDebug(ex, "Failed to process individual stream for item {Name}", baseItem.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to extract subtitle languages for item {Name}", baseItem.Name);
            }
        }

        /// <summary>
        /// Extracts resolution from media streams.
        /// </summary>
        private static void ExtractResolution(Operand operand, BaseItem baseItem, RefreshQueueServiceRefreshCache cache, ILogger? logger)
        {
            operand.Resolution = string.Empty;
            try
            {
                // Check cache first
                IEnumerable<object> mediaStreams;
                if (cache.MediaStreamsCache.TryGetValue(baseItem.Id, out var cachedStreams))
                {
                    mediaStreams = cachedStreams;
                }
                else
                {
                    // Use shared helper to extract media streams
                    mediaStreams = TryGetAllMediaStreams(baseItem, logger);
                    // Cache the result
                    cache.MediaStreamsCache[baseItem.Id] = mediaStreams;
                }

                // Process found streams to find the highest resolution video stream
                int maxHeight = 0;
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
                        logger?.LogDebug(ex, "Failed to process individual stream for item {Name}", baseItem.Name);
                    }
                }

                // Convert height to resolution string
                if (maxHeight > 0)
                {
                    operand.Resolution = maxHeight switch
                    {
                        <= 480 => "480p",
                        <= 720 => "720p",
                        <= 1080 => "1080p",
                        <= 1440 => "1440p",
                        <= 2160 => "4K",
                        <= 4320 => "8K",
                        _ => "8K" // For anything higher, default to 8K,
                    };
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to extract resolution for item {Name}", baseItem.Name);
            }
        }

        /// <summary>
        /// Extracts framerate from media streams.
        /// </summary>
        private static void ExtractFramerate(Operand operand, BaseItem baseItem, ILogger? logger)
        {
            operand.Framerate = null;
            try
            {
                // Use shared helper to extract media streams
                var mediaStreams = TryGetAllMediaStreams(baseItem, logger);

                // Process found streams to find the first video stream with framerate information
                foreach (var stream in mediaStreams)
                {
                    try
                    {
                        var typeProperty = stream.GetType().GetProperty("Type");
                        var framerateProperty = stream.GetType().GetProperty("RealFrameRate") ?? stream.GetType().GetProperty("AverageFrameRate");

                        if (typeProperty != null && framerateProperty != null)
                        {
                            var streamType = typeProperty.GetValue(stream);
                            var framerate = framerateProperty.GetValue(stream);

                            // Check if it's a video stream
                            if (streamType != null && streamType.ToString() == "Video" && framerate != null)
                            {
                                // Try to parse framerate as different numeric types
                                if (framerate is float floatFramerate && floatFramerate > 0)
                                {
                                    operand.Framerate = floatFramerate;
                                    break; // Use the first valid framerate found,
                                }
                                else if (framerate is double doubleFramerate && doubleFramerate > 0)
                                {
                                    operand.Framerate = (float)doubleFramerate;
                                    break;
                                }
                                else if (framerate is int intFramerate && intFramerate > 0)
                                {
                                    operand.Framerate = intFramerate;
                                    break;
                                }
                                else if (double.TryParse(framerate.ToString(), out var parsedFramerate) && parsedFramerate > 0)
                                {
                                    operand.Framerate = (float)parsedFramerate;
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogDebug(ex, "Failed to process individual stream for item {Name}", baseItem.Name);
                    }
                }

                logger?.LogDebug("Extracted framerate for item {Name}: {Framerate}", baseItem.Name, operand.Framerate?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null");
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to extract framerate for item {Name}", baseItem.Name);
            }
        }

        /// <summary>
        /// Extracts audio quality information from media streams (bitrate, sample rate, bit depth, codec, channels).
        /// </summary>
        private static void ExtractAudioQuality(Operand operand, BaseItem baseItem, RefreshQueueServiceRefreshCache cache, ILogger? logger)
        {
            operand.AudioBitrate = 0;
            operand.AudioSampleRate = 0;
            operand.AudioBitDepth = 0;
            operand.AudioCodec = string.Empty;
            operand.AudioProfile = string.Empty;
            operand.AudioChannels = 0;

            try
            {
                // Check cache first
                IEnumerable<object> mediaStreams;
                if (cache.MediaStreamsCache.TryGetValue(baseItem.Id, out var cachedStreams))
                {
                    mediaStreams = cachedStreams;
                }
                else
                {
                    // Use shared helper to extract media streams
                    mediaStreams = TryGetAllMediaStreams(baseItem, logger);
                    // Cache the result
                    cache.MediaStreamsCache[baseItem.Id] = mediaStreams;
                }

                // Process found streams to find the first audio stream with quality information
                foreach (var stream in mediaStreams)
                {
                    try
                    {
                        var typeProperty = stream.GetType().GetProperty("Type");

                        if (typeProperty != null)
                        {
                            var streamType = typeProperty.GetValue(stream);

                            // Check if it's an audio stream
                            if (streamType != null && streamType.ToString() == "Audio")
                            {
                                // Extract bitrate (in bps, convert to kbps)
                                var bitrateProperty = stream.GetType().GetProperty("BitRate");
                                if (bitrateProperty != null)
                                {
                                    var bitrate = bitrateProperty.GetValue(stream);
                                    if (bitrate != null && int.TryParse(bitrate.ToString(), out int bitrateValue) && bitrateValue > 0)
                                    {
                                        operand.AudioBitrate = bitrateValue / 1000; // Convert to kbps,
                                    }
                                }

                                // Extract sample rate (in Hz)
                                var sampleRateProperty = stream.GetType().GetProperty("SampleRate");
                                if (sampleRateProperty != null)
                                {
                                    var sampleRate = sampleRateProperty.GetValue(stream);
                                    if (sampleRate != null && int.TryParse(sampleRate.ToString(), out int sampleRateValue) && sampleRateValue > 0)
                                    {
                                        operand.AudioSampleRate = sampleRateValue;
                                    }
                                }

                                // Extract bit depth (in bits)
                                var bitDepthProperty = stream.GetType().GetProperty("BitDepth");
                                if (bitDepthProperty != null)
                                {
                                    var bitDepth = bitDepthProperty.GetValue(stream);
                                    if (bitDepth != null && int.TryParse(bitDepth.ToString(), out int bitDepthValue) && bitDepthValue > 0)
                                    {
                                        operand.AudioBitDepth = bitDepthValue;
                                    }
                                }

                                // Extract codec
                                var codecProperty = stream.GetType().GetProperty("Codec");
                                if (codecProperty != null)
                                {
                                    var codec = codecProperty.GetValue(stream) as string;
                                    if (!string.IsNullOrEmpty(codec))
                                    {
                                        operand.AudioCodec = codec.ToUpperInvariant(); // Normalize to uppercase,
                                    }
                                }

                                // Extract profile
                                var profileProperty = stream.GetType().GetProperty("Profile");
                                if (profileProperty != null)
                                {
                                    var profile = profileProperty.GetValue(stream) as string;
                                    if (!string.IsNullOrEmpty(profile))
                                    {
                                        operand.AudioProfile = profile;
                                    }
                                }

                                // Extract channels
                                var channelsProperty = stream.GetType().GetProperty("Channels");
                                if (channelsProperty != null)
                                {
                                    var channels = channelsProperty.GetValue(stream);
                                    if (channels != null && int.TryParse(channels.ToString(), out int channelsValue) && channelsValue > 0)
                                    {
                                        operand.AudioChannels = channelsValue;
                                    }
                                }

                                // If we found at least one audio property, we're done
                                // (use the first audio stream found)
                                if (operand.AudioBitrate > 0 || operand.AudioSampleRate > 0 ||
                                    operand.AudioBitDepth > 0 || !string.IsNullOrEmpty(operand.AudioCodec) ||
                                    !string.IsNullOrEmpty(operand.AudioProfile) || operand.AudioChannels > 0)
                                {
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogDebug(ex, "Failed to process individual stream for item {Name}", baseItem.Name);
                    }
                }

                logger?.LogDebug(
                    "Extracted audio quality for item {Name}: Bitrate={Bitrate}kbps, SampleRate={SampleRate}Hz, BitDepth={BitDepth}bit, Codec={Codec}, Profile={Profile}, Channels={Channels}",
                    baseItem.Name, operand.AudioBitrate, operand.AudioSampleRate, operand.AudioBitDepth, operand.AudioCodec, operand.AudioProfile, operand.AudioChannels
                );
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to extract audio quality for item {Name}", baseItem.Name);
            }
        }

        /// <summary>
        /// Extracts video quality information from media streams (codec, profile, range, range type).
        /// </summary>
        private static void ExtractVideoQuality(Operand operand, BaseItem baseItem, RefreshQueueServiceRefreshCache cache, ILogger? logger)
        {
            operand.VideoCodec = string.Empty;
            operand.VideoProfile = string.Empty;
            operand.VideoRange = string.Empty;
            operand.VideoRangeType = string.Empty;

            try
            {
                // Check cache first
                IEnumerable<object> mediaStreams;
                if (cache.MediaStreamsCache.TryGetValue(baseItem.Id, out var cachedStreams))
                {
                    mediaStreams = cachedStreams;
                }
                else
                {
                    // Use shared helper to extract media streams
                    mediaStreams = TryGetAllMediaStreams(baseItem, logger);
                    // Cache the result
                    cache.MediaStreamsCache[baseItem.Id] = mediaStreams;
                }

                // Process found streams to find the first video stream with quality information
                foreach (var stream in mediaStreams)
                {
                    try
                    {
                        var typeProperty = stream.GetType().GetProperty("Type");

                        if (typeProperty != null)
                        {
                            var streamType = typeProperty.GetValue(stream);

                            // Check if it's a video stream
                            if (streamType != null && streamType.ToString() == "Video")
                            {
                                // Extract codec
                                var codecProperty = stream.GetType().GetProperty("Codec");
                                if (codecProperty != null)
                                {
                                    var codec = codecProperty.GetValue(stream) as string;
                                    if (!string.IsNullOrEmpty(codec))
                                    {
                                        operand.VideoCodec = codec.ToUpperInvariant(); // Normalize to uppercase,
                                    }
                                }

                                // Extract profile
                                var profileProperty = stream.GetType().GetProperty("Profile");
                                if (profileProperty != null)
                                {
                                    var profile = profileProperty.GetValue(stream) as string;
                                    if (!string.IsNullOrEmpty(profile))
                                    {
                                        operand.VideoProfile = profile;
                                    }
                                }

                                // Extract video range (HDR/SDR)
                                var videoRangeProperty = stream.GetType().GetProperty("VideoRange");
                                if (videoRangeProperty != null)
                                {
                                    var videoRange = videoRangeProperty.GetValue(stream);
                                    if (videoRange != null)
                                    {
                                        operand.VideoRange = videoRange.ToString() ?? "";
                                    }
                                }

                                // Extract video range type (HDR10, DOVIWithHDR10, etc.)
                                var videoRangeTypeProperty = stream.GetType().GetProperty("VideoRangeType");
                                if (videoRangeTypeProperty != null)
                                {
                                    var videoRangeType = videoRangeTypeProperty.GetValue(stream);
                                    if (videoRangeType != null)
                                    {
                                        operand.VideoRangeType = videoRangeType.ToString() ?? "";
                                    }
                                }

                                // If we found at least one video property, we're done
                                // (use the first video stream found)
                                if (!string.IsNullOrEmpty(operand.VideoCodec) || !string.IsNullOrEmpty(operand.VideoProfile) ||
                                    !string.IsNullOrEmpty(operand.VideoRange) || !string.IsNullOrEmpty(operand.VideoRangeType))
                                {
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogDebug(ex, "Failed to process individual stream for item {Name}", baseItem.Name);
                    }
                }

                logger?.LogDebug(
                    "Extracted video quality for item {Name}: Codec={Codec}, Profile={Profile}, Range={Range}, RangeType={RangeType}",
                    baseItem.Name, operand.VideoCodec, operand.VideoProfile, operand.VideoRange, operand.VideoRangeType
                );
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to extract video quality for item {Name}", baseItem.Name);
            }
        }

        /// <summary>
        /// Helper method to safely extract SeriesId as Guid from episode items.
        /// Handles Guid, Guid?, and string representations.
        /// </summary>
        private static bool TryGetEpisodeSeriesGuid(BaseItem baseItem, out Guid seriesGuid)
        {
            seriesGuid = Guid.Empty;
            if (baseItem is not Episode) return false;

            var episodeType = baseItem.GetType();
            var seriesIdProperty = _seriesIdPropertyCache.GetOrAdd(episodeType, t => t.GetProperty("SeriesId"));
            if (seriesIdProperty == null) return false;

            var seriesId = seriesIdProperty.GetValue(baseItem);
            if (seriesId is Guid g) { seriesGuid = g; return true; }
            if (seriesId != null && seriesId.GetType() == typeof(Guid?))
            {
                var nullableGuid = (Guid?)seriesId;
                if (nullableGuid.HasValue) { seriesGuid = nullableGuid.Value; return true; }
            }
            if (seriesId is string s && Guid.TryParse(s, out var parsed)) { seriesGuid = parsed; return true; }

            return false;
        }

        /// <summary>
        /// Extracts the series name for episodes with per-refresh caching.
        /// </summary>
        private static void ExtractSeriesName(Operand operand, BaseItem baseItem, ILibraryManager libraryManager, RefreshQueueServiceRefreshCache cache, ILogger? logger)
        {
            operand.SeriesName = string.Empty;
            try
            {
                // Use helper to extract SeriesId safely
                if (TryGetEpisodeSeriesGuid(baseItem, out var seriesGuid))
                {
                    // Check cache first to avoid repeated library lookups
                    if (cache.SeriesNameById.TryGetValue(seriesGuid, out var cachedName))
                    {
                        operand.SeriesName = cachedName;
                        logger?.LogDebug("Using cached series name '{SeriesName}' for episode '{EpisodeName}'",
                            operand.SeriesName, baseItem.Name);
                    }
                    else
                    {
                        try
                        {
                            // Get the parent series from the library manager
                            var parentSeries = libraryManager.GetItemById(seriesGuid);
                            var seriesName = parentSeries?.Name ?? "";
                            var seriesSortName = parentSeries?.SortName ?? ""; // Don't fallback to Name, let sorting logic handle it

                            // Cache the result for future episodes from the same series
                            cache.SeriesNameById[seriesGuid] = seriesName;
                            cache.SeriesSortNameById[seriesGuid] = seriesSortName;
                            operand.SeriesName = seriesName;

                            logger?.LogDebug("Extracted and cached series name '{SeriesName}' for episode '{EpisodeName}'",
                                operand.SeriesName, baseItem.Name);
                        }
                        catch (Exception ex)
                        {
                            logger?.LogDebug(ex, "Failed to get parent series for episode '{EpisodeName}' with SeriesId {SeriesId}",
                                baseItem.Name, seriesGuid);

                            // Cache empty string to avoid repeated failures
                            cache.SeriesNameById[seriesGuid] = string.Empty;
                            cache.SeriesSortNameById[seriesGuid] = string.Empty;
                        }
                    }
                }
                else
                {
                    // Either not an episode, no SeriesId property, or unsupported SeriesId value
                    if (baseItem is Episode)
                    {
                        logger?.LogDebug("Could not extract valid SeriesId from episode '{EpisodeName}'", baseItem.Name);
                    }
                    else
                    {
                        logger?.LogDebug("Item '{ItemName}' is not an episode, series name remains empty", baseItem.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to extract series name for item '{ItemName}'", baseItem.Name);
            }
        }

        /// <summary>
        /// Extracts the last episode air date for a Series by finding the most recent episode's air date.
        /// This is only populated for Series items (not Episodes - they use ReleaseDate directly).
        /// </summary>
        private static void ExtractLastEpisodeAirDate(Operand operand, BaseItem baseItem, ILibraryManager libraryManager, RefreshQueueServiceRefreshCache cache, ILogger? logger)
        {
            operand.LastEpisodeAirDate = 0;
            try
            {
                // Only process Series - this field is specifically for "when did the most recent episode air"
                if (baseItem is not Series series)
                {
                    return;
                }

                // Check cache first
                if (cache.LastEpisodeAirDateById.TryGetValue(series.Id, out var cachedDate))
                {
                    operand.LastEpisodeAirDate = cachedDate;
                    logger?.LogDebug("Using cached last episode air date for series '{SeriesName}': {Date}",
                        series.Name, cachedDate > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)cachedDate).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "N/A");
                    return;
                }

                // Query all episodes for this series using AncestorIds for hierarchical lookup
                var episodes = libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    AncestorIds = new[] { series.Id },
                    IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Episode },
                    Recursive = true,
                    IsVirtualItem = false
                });

                DateTime? mostRecentAirDate = null;

                foreach (var episode in episodes)
                {
                    // Use PremiereDate as the episode air date
                    if (episode.PremiereDate.HasValue)
                    {
                        if (!mostRecentAirDate.HasValue || episode.PremiereDate.Value > mostRecentAirDate.Value)
                        {
                            mostRecentAirDate = episode.PremiereDate.Value;
                        }
                    }
                }

                if (mostRecentAirDate.HasValue)
                {
                    operand.LastEpisodeAirDate = SafeToUnixTimeSeconds(mostRecentAirDate.Value);
                    logger?.LogDebug("Extracted last episode air date for series '{SeriesName}': {Date}",
                        series.Name, mostRecentAirDate.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    logger?.LogDebug("No episode air dates found for series '{SeriesName}'", series.Name);
                }

                // Cache the result
                cache.LastEpisodeAirDateById[series.Id] = operand.LastEpisodeAirDate;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to extract last episode air date for item '{ItemName}'", baseItem.Name);
            }
        }

        /// <summary>
        /// Extracts the parent series tags for episodes with per-refresh caching.
        /// This is an expensive operation as it requires a database lookup, so caching is critical for performance.
        /// </summary>
        private static void ExtractParentSeriesTags(Operand operand, BaseItem baseItem, ILibraryManager libraryManager, RefreshQueueServiceRefreshCache cache, ILogger? logger)
        {
            operand.ParentSeriesTags = [];
            try
            {
                // Only process episodes - other item types don't have parent series
                if (baseItem is not Episode)
                {
                    logger?.LogDebug("Item '{ItemName}' is not an episode, parent series tags remain empty", baseItem.Name);
                    return;
                }

                // Use helper to extract SeriesId safely
                if (TryGetEpisodeSeriesGuid(baseItem, out var seriesGuid))
                {
                    // Check cache first to avoid repeated library lookups (expensive!)
                    if (cache.SeriesTagsById.TryGetValue(seriesGuid, out var cachedTags))
                    {
                        operand.ParentSeriesTags = cachedTags;
                        logger?.LogDebug("Using cached parent series tags for episode '{EpisodeName}' (series ID: {SeriesId}): [{Tags}]",
                            baseItem.Name, seriesGuid, string.Join(", ", cachedTags));
                    }
                    else
                    {
                        try
                        {
                            // Get the parent series from the library manager (expensive operation!)
                            var parentSeries = libraryManager.GetItemById(seriesGuid);
                            var seriesTags = parentSeries?.Tags?.ToList() ?? [];

                            // Cache the result for future episodes from the same series
                            cache.SeriesTagsById[seriesGuid] = seriesTags;
                            operand.ParentSeriesTags = seriesTags;

                            logger?.LogDebug("Extracted and cached parent series tags for episode '{EpisodeName}' (series: '{SeriesName}'): [{Tags}]",
                                baseItem.Name, parentSeries?.Name ?? "Unknown", string.Join(", ", seriesTags));
                        }
                        catch (Exception ex)
                        {
                            logger?.LogDebug(ex, "Failed to get parent series tags for episode '{EpisodeName}' with SeriesId {SeriesId}",
                                baseItem.Name, seriesGuid);

                            // Cache empty list to avoid repeated failures
                            cache.SeriesTagsById[seriesGuid] = [];
                        }
                    }
                }
                else
                {
                    logger?.LogDebug("Could not extract valid SeriesId from episode '{EpisodeName}'", baseItem.Name);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to extract parent series tags for item '{ItemName}'", baseItem.Name);
            }
        }

        /// <summary>
        /// Extracts the parent series studios for episodes with per-refresh caching.
        /// This is an expensive operation as it requires a database lookup, so caching is critical for performance.
        /// </summary>
        private static void ExtractParentSeriesStudios(Operand operand, BaseItem baseItem, ILibraryManager libraryManager, RefreshQueueServiceRefreshCache cache, ILogger? logger)
        {
            operand.ParentSeriesStudios = [];
            try
            {
                // Only process episodes - other item types don't have parent series
                if (baseItem is not Episode)
                {
                    logger?.LogDebug("Item '{ItemName}' is not an episode, parent series studios remain empty", baseItem.Name);
                    return;
                }

                // Use helper to extract SeriesId safely
                if (TryGetEpisodeSeriesGuid(baseItem, out var seriesGuid))
                {
                    // Check cache first to avoid repeated library lookups (expensive!)
                    if (cache.SeriesStudiosById.TryGetValue(seriesGuid, out var cachedStudios))
                    {
                        operand.ParentSeriesStudios = cachedStudios;
                        logger?.LogDebug("Using cached parent series studios for episode '{EpisodeName}' (series ID: {SeriesId}): [{Studios}]",
                            baseItem.Name, seriesGuid, string.Join(", ", cachedStudios));
                    }
                    else
                    {
                        try
                        {
                            // Get the parent series from the library manager (expensive operation!)
                            var parentSeries = libraryManager.GetItemById(seriesGuid);
                            var seriesStudios = parentSeries?.Studios?.ToList() ?? [];

                            // Cache the result for future episodes from the same series
                            cache.SeriesStudiosById[seriesGuid] = seriesStudios;
                            operand.ParentSeriesStudios = seriesStudios;

                            logger?.LogDebug("Extracted and cached parent series studios for episode '{EpisodeName}' (series: '{SeriesName}'): [{Studios}]",
                                baseItem.Name, parentSeries?.Name ?? "Unknown", string.Join(", ", seriesStudios));
                        }
                        catch (Exception ex)
                        {
                            logger?.LogDebug(ex, "Failed to get parent series studios for episode '{EpisodeName}' with SeriesId {SeriesId}",
                                baseItem.Name, seriesGuid);

                            // Cache empty list to avoid repeated failures
                            cache.SeriesStudiosById[seriesGuid] = [];
                        }
                    }
                }
                else
                {
                    logger?.LogDebug("Could not extract valid SeriesId from episode '{EpisodeName}'", baseItem.Name);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to extract parent series studios for item '{ItemName}'", baseItem.Name);
            }
        }

        /// <summary>
        /// Extracts the parent series genres for episodes with per-refresh caching.
        /// This is an expensive operation as it requires a database lookup, so caching is critical for performance.
        /// </summary>
        private static void ExtractParentSeriesGenres(Operand operand, BaseItem baseItem, ILibraryManager libraryManager, RefreshQueueServiceRefreshCache cache, ILogger? logger)
        {
            operand.ParentSeriesGenres = [];
            try
            {
                // Only process episodes - other item types don't have parent series
                if (baseItem is not Episode)
                {
                    logger?.LogDebug("Item '{ItemName}' is not an episode, parent series genres remain empty", baseItem.Name);
                    return;
                }

                // Use helper to extract SeriesId safely
                if (TryGetEpisodeSeriesGuid(baseItem, out var seriesGuid))
                {
                    // Check cache first to avoid repeated library lookups (expensive!)
                    if (cache.SeriesGenresById.TryGetValue(seriesGuid, out var cachedGenres))
                    {
                        operand.ParentSeriesGenres = cachedGenres;
                        logger?.LogDebug("Using cached parent series genres for episode '{EpisodeName}' (series ID: {SeriesId}): [{Genres}]",
                            baseItem.Name, seriesGuid, string.Join(", ", cachedGenres));
                    }
                    else
                    {
                        try
                        {
                            // Get the parent series from the library manager (expensive operation!)
                            var parentSeries = libraryManager.GetItemById(seriesGuid);
                            var seriesGenres = parentSeries?.Genres?.ToList() ?? [];

                            // Cache the result for future episodes from the same series
                            cache.SeriesGenresById[seriesGuid] = seriesGenres;
                            operand.ParentSeriesGenres = seriesGenres;

                            logger?.LogDebug("Extracted and cached parent series genres for episode '{EpisodeName}' (series: '{SeriesName}'): [{Genres}]",
                                baseItem.Name, parentSeries?.Name ?? "Unknown", string.Join(", ", seriesGenres));
                        }
                        catch (Exception ex)
                        {
                            logger?.LogDebug(ex, "Failed to get parent series genres for episode '{EpisodeName}' with SeriesId {SeriesId}",
                                baseItem.Name, seriesGuid);

                            // Cache empty list to avoid repeated failures
                            cache.SeriesGenresById[seriesGuid] = [];
                        }
                    }
                }
                else
                {
                    logger?.LogDebug("Could not extract valid SeriesId from episode '{EpisodeName}'", baseItem.Name);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to extract parent series genres for item '{ItemName}'", baseItem.Name);
            }
        }

        /// <summary>
        /// Extracts people (actors, directors, producers, etc.) associated with the item.
        /// </summary>
        private static void ExtractPeople(Operand operand, BaseItem baseItem, ILibraryManager libraryManager, RefreshQueueServiceRefreshCache cache, ILogger? logger)
        {
            // Initialize all people fields
            operand.People = [];
            operand.Actors = [];
            operand.ActorRoles = [];
            operand.Directors = [];
            operand.Composers = [];
            operand.Writers = [];
            operand.GuestStars = [];
            operand.Producers = [];
            operand.Conductors = [];
            operand.Lyricists = [];
            operand.Arrangers = [];
            operand.SoundEngineers = [];
            operand.Mixers = [];
            operand.Remixers = [];
            operand.Creators = [];
            operand.PersonArtists = [];
            operand.PersonAlbumArtists = [];
            operand.Authors = [];
            operand.Illustrators = [];
            operand.Pencilers = [];
            operand.Inkers = [];
            operand.Colorists = [];
            operand.Letterers = [];
            operand.CoverArtists = [];
            operand.Editors = [];
            operand.Translators = [];

            // Check cache first if available
            if (cache != null && cache.ItemPeople.TryGetValue(baseItem.Id, out var cachedPeople))
            {
                operand.People = new List<string>(cachedPeople.AllPeople);
                operand.Actors = new List<string>(cachedPeople.Actors);
                operand.ActorRoles = new List<string>(cachedPeople.ActorRoles);
                operand.Directors = new List<string>(cachedPeople.Directors);
                operand.Composers = new List<string>(cachedPeople.Composers);
                operand.Writers = new List<string>(cachedPeople.Writers);
                operand.GuestStars = new List<string>(cachedPeople.GuestStars);
                operand.Producers = new List<string>(cachedPeople.Producers);
                operand.Conductors = new List<string>(cachedPeople.Conductors);
                operand.Lyricists = new List<string>(cachedPeople.Lyricists);
                operand.Arrangers = new List<string>(cachedPeople.Arrangers);
                operand.SoundEngineers = new List<string>(cachedPeople.SoundEngineers);
                operand.Mixers = new List<string>(cachedPeople.Mixers);
                operand.Remixers = new List<string>(cachedPeople.Remixers);
                operand.Creators = new List<string>(cachedPeople.Creators);
                operand.PersonArtists = new List<string>(cachedPeople.PersonArtists);
                operand.PersonAlbumArtists = new List<string>(cachedPeople.PersonAlbumArtists);
                operand.Authors = new List<string>(cachedPeople.Authors);
                operand.Illustrators = new List<string>(cachedPeople.Illustrators);
                operand.Pencilers = new List<string>(cachedPeople.Pencilers);
                operand.Inkers = new List<string>(cachedPeople.Inkers);
                operand.Colorists = new List<string>(cachedPeople.Colorists);
                operand.Letterers = new List<string>(cachedPeople.Letterers);
                operand.CoverArtists = new List<string>(cachedPeople.CoverArtists);
                operand.Editors = new List<string>(cachedPeople.Editors);
                operand.Translators = new List<string>(cachedPeople.Translators);
                return;
            }

            // Cache miss or no cache - perform query
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Cache the GetPeople method lookup for better performance
                var getPeopleMethod = _getPeopleMethodCache;
                if (getPeopleMethod == null)
                {
                    lock (_getPeopleMethodLock)
                    {
                        if (_getPeopleMethodCache == null)
                        {
                            _getPeopleMethodCache = libraryManager.GetType().GetMethod("GetPeople", [typeof(InternalPeopleQuery)]);
                        }
                        getPeopleMethod = _getPeopleMethodCache;
                    }
                }

                if (getPeopleMethod != null)
                {
                    // Use InternalPeopleQuery to get people associated with this item
                    var peopleQuery = new InternalPeopleQuery
                    {
                        ItemId = baseItem.Id,
                    };

                    var result = getPeopleMethod.Invoke(libraryManager, [peopleQuery]);

                    if (result is IEnumerable<object> peopleEnum)
                    {
                        // Use the helper method to categorize people (DRY principle)
                        var categorized = CategorizePeople(peopleEnum, logger);

                        operand.People = categorized.AllPeople;
                        operand.Actors = categorized.Actors;
                        operand.ActorRoles = categorized.ActorRoles;
                        operand.Directors = categorized.Directors;
                        operand.Composers = categorized.Composers;
                        operand.Writers = categorized.Writers;
                        operand.GuestStars = categorized.GuestStars;
                        operand.Producers = categorized.Producers;
                        operand.Conductors = categorized.Conductors;
                        operand.Lyricists = categorized.Lyricists;
                        operand.Arrangers = categorized.Arrangers;
                        operand.SoundEngineers = categorized.SoundEngineers;
                        operand.Mixers = categorized.Mixers;
                        operand.Remixers = categorized.Remixers;
                        operand.Creators = categorized.Creators;
                        operand.PersonArtists = categorized.PersonArtists;
                        operand.PersonAlbumArtists = categorized.PersonAlbumArtists;
                        operand.Authors = categorized.Authors;
                        operand.Illustrators = categorized.Illustrators;
                        operand.Pencilers = categorized.Pencilers;
                        operand.Inkers = categorized.Inkers;
                        operand.Colorists = categorized.Colorists;
                        operand.Letterers = categorized.Letterers;
                        operand.CoverArtists = categorized.CoverArtists;
                        operand.Editors = categorized.Editors;
                        operand.Translators = categorized.Translators;
                    }

                    stopwatch.Stop();
                    logger?.LogDebug("People query for item {ItemId} completed in {Ms}ms ({PeopleCount} people, {ActorCount} actors, {DirectorCount} directors, {RoleCount} roles)",
                        baseItem.Id, stopwatch.ElapsedMilliseconds, operand.People.Count, operand.Actors.Count, operand.Directors.Count, operand.ActorRoles.Count);

                    // Store in cache for future use
                    if (cache != null)
                    {
                        cache.ItemPeople[baseItem.Id] = new CategorizedPeople
                        {
                            AllPeople = new List<string>(operand.People),
                            Actors = new List<string>(operand.Actors),
                            ActorRoles = new List<string>(operand.ActorRoles),
                            Directors = new List<string>(operand.Directors),
                            Composers = new List<string>(operand.Composers),
                            Writers = new List<string>(operand.Writers),
                            GuestStars = new List<string>(operand.GuestStars),
                            Producers = new List<string>(operand.Producers),
                            Conductors = new List<string>(operand.Conductors),
                            Lyricists = new List<string>(operand.Lyricists),
                            Arrangers = new List<string>(operand.Arrangers),
                            SoundEngineers = new List<string>(operand.SoundEngineers),
                            Mixers = new List<string>(operand.Mixers),
                            Remixers = new List<string>(operand.Remixers),
                            Creators = new List<string>(operand.Creators),
                            PersonArtists = new List<string>(operand.PersonArtists),
                            PersonAlbumArtists = new List<string>(operand.PersonAlbumArtists),
                            Authors = new List<string>(operand.Authors),
                            Illustrators = new List<string>(operand.Illustrators),
                            Pencilers = new List<string>(operand.Pencilers),
                            Inkers = new List<string>(operand.Inkers),
                            Colorists = new List<string>(operand.Colorists),
                            Letterers = new List<string>(operand.Letterers),
                            CoverArtists = new List<string>(operand.CoverArtists),
                            Editors = new List<string>(operand.Editors),
                            Translators = new List<string>(operand.Translators),
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                logger?.LogWarning(ex, "Failed to extract people for item {Name} after {Ms}ms", baseItem.Name, stopwatch.ElapsedMilliseconds);
            }
        }

        /// <summary>
        /// Preloads people data for all items in parallel to improve performance.
        /// </summary>
        public static void PreloadPeopleCache(ILibraryManager libraryManager, IEnumerable<BaseItem> items, RefreshQueueServiceRefreshCache cache, ILogger? logger)
        {
            if (cache == null || items == null)
            {
                return;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var itemList = items.ToList();
            
            // Skip if cache already has entries for these items
            var itemsToProcess = itemList.Where(item => !cache.ItemPeople.ContainsKey(item.Id)).ToList();
            if (itemsToProcess.Count == 0)
            {
                logger?.LogDebug("People cache already contains all items, skipping preload");
                return;
            }

            logger?.LogDebug("Preloading People cache for {Count} items sequentially", itemsToProcess.Count);

            // Dictionary for collecting results
            var tempCache = new Dictionary<Guid, CategorizedPeople>();
            var processedCount = 0;

            // Process items sequentially
            foreach (var item in itemsToProcess)
            {
                try
                {
                    // Cache the GetPeople method lookup
                    var getPeopleMethod = _getPeopleMethodCache;
                    if (getPeopleMethod == null)
                    {
                        lock (_getPeopleMethodLock)
                        {
                            if (_getPeopleMethodCache == null)
                            {
                                _getPeopleMethodCache = libraryManager.GetType().GetMethod("GetPeople", [typeof(InternalPeopleQuery)]);
                            }
                            getPeopleMethod = _getPeopleMethodCache;
                        }
                    }

                    if (getPeopleMethod != null)
                    {
                        var peopleQuery = new InternalPeopleQuery
                        {
                            ItemId = item.Id,
                        };

                        var result = getPeopleMethod.Invoke(libraryManager, [peopleQuery]);

                        if (result is IEnumerable<object> peopleEnum)
                        {
                            // Use the helper method to categorize people (DRY principle)
                            var categorized = CategorizePeople(peopleEnum, logger);
                            tempCache[item.Id] = categorized;
                        }

                        processedCount++;

                        // Log progress every 100 items
                        if (processedCount % 100 == 0)
                        {
                            logger?.LogDebug("People cache progress: {Processed}/{Total} items",
                                processedCount, itemsToProcess.Count);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to preload people for item {ItemId}", item.Id);
                }
            }

            // Transfer from dictionary to cache
            foreach (var kvp in tempCache)
            {
                cache.ItemPeople[kvp.Key] = kvp.Value;
            }

            stopwatch.Stop();

            logger?.LogDebug("People cache initialization completed in {TotalMs}ms for {Count} items",
                stopwatch.ElapsedMilliseconds, itemsToProcess.Count);
        }

        /// <summary>
        /// Extracts artists and album artists for music items.
        /// </summary>
        private static void ExtractArtists(Operand operand, BaseItem baseItem, ILogger? logger)
        {
            operand.Artists = [];
            operand.AlbumArtists = [];

            try
            {
                // Try to extract Artist property
                var artistProperty = baseItem.GetType().GetProperty("Artist");
                if (artistProperty != null)
                {
                    var artistValue = artistProperty.GetValue(baseItem) as string;
                    if (!string.IsNullOrEmpty(artistValue))
                    {
                        operand.Artists.Add(artistValue);
                    }
                }

                // Try to extract Artists property (collection)
                var artistsProperty = baseItem.GetType().GetProperty("Artists");
                if (artistsProperty != null)
                {
                    var artistsValue = artistsProperty.GetValue(baseItem);
                    if (artistsValue is IEnumerable<string> artistsCollection)
                    {
                        foreach (var artist in artistsCollection)
                        {
                            if (!string.IsNullOrEmpty(artist) && !operand.Artists.Contains(artist))
                            {
                                operand.Artists.Add(artist);
                            }
                        }
                    }
                }

                // Try to extract AlbumArtist property
                var albumArtistProperty = baseItem.GetType().GetProperty("AlbumArtist");
                if (albumArtistProperty != null)
                {
                    var albumArtistValue = albumArtistProperty.GetValue(baseItem) as string;
                    if (!string.IsNullOrEmpty(albumArtistValue))
                    {
                        operand.AlbumArtists.Add(albumArtistValue);
                    }
                }

                // Try to extract AlbumArtists property (collection)
                var albumArtistsProperty = baseItem.GetType().GetProperty("AlbumArtists");
                if (albumArtistsProperty != null)
                {
                    var albumArtistsValue = albumArtistsProperty.GetValue(baseItem);
                    if (albumArtistsValue is IEnumerable<string> albumArtistsCollection)
                    {
                        foreach (var albumArtist in albumArtistsCollection)
                        {
                            if (!string.IsNullOrEmpty(albumArtist) && !operand.AlbumArtists.Contains(albumArtist))
                            {
                                operand.AlbumArtists.Add(albumArtist);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to extract artists for item {Name}", baseItem.Name);
            }
        }

        // Clean API using options object - no more boolean flag proliferation!
        public static Operand GetMediaType(ILibraryManager libraryManager, BaseItem baseItem, User user,
            IUserDataManager? userDataManager, IUserManager userManager, ILogger? logger, MediaTypeExtractionOptions options,
            RefreshQueueServiceRefreshCache cache)
        {

            // Extract options for easier access
            var extractAudioLanguages = options.ExtractAudioLanguages;
            var extractSubtitleLanguages = options.ExtractSubtitleLanguages;
            var extractAudioQuality = options.ExtractAudioQuality;
            var extractVideoQuality = options.ExtractVideoQuality;
            var extractPeople = options.ExtractPeople;
            var extractNextUnwatched = options.ExtractNextUnwatched;
            var extractSeriesName = options.ExtractSeriesName;
            var extractParentSeriesTags = options.ExtractParentSeriesTags;
            var extractParentSeriesStudios = options.ExtractParentSeriesStudios;
            var extractParentSeriesGenres = options.ExtractParentSeriesGenres;
            var extractLastEpisodeAirDate = options.ExtractLastEpisodeAirDate;
            var includeUnwatchedSeries = options.IncludeUnwatchedSeries;
            var additionalUserIds = options.AdditionalUserIds;

            // Get user data first for Jellyfin 10.11 compatibility - check cache first
            MediaBrowser.Controller.Entities.UserItemData? userData = null;
            var userDataCacheKey = (baseItem.Id, user.Id);
            if (cache.UserDataCache.TryGetValue(userDataCacheKey, out var cachedUserData))
            {
                userData = cachedUserData;
            }
            else if (userDataManager != null)
            {
                userData = userDataManager.GetUserData(user, baseItem);
                // Cache the result
                if (userData != null)
                {
                    cache.UserDataCache[userDataCacheKey] = userData;
                }
            }

            // Calculate playback status based on item type
            string playbackStatus = CalculatePlaybackStatusForUser(
                baseItem,
                user,
                libraryManager,
                userDataManager,
                userData,
                cache,
                logger);

            var operand = new Operand(baseItem.Name)
            {
                Genres = baseItem.Genres is not null ? [.. baseItem.Genres] : [],
                Studios = baseItem.Studios is not null ? [.. baseItem.Studios] : [],
                CommunityRating = baseItem.CommunityRating.GetValueOrDefault(),
                CriticRating = baseItem.CriticRating.GetValueOrDefault(),
                MediaType = baseItem.MediaType.ToString(),
                ItemType = GetItemTypeName(baseItem, logger),
                Album = baseItem.Album,
                ProductionYear = baseItem.ProductionYear.GetValueOrDefault(),
                Tags = baseItem.Tags is not null ? [.. baseItem.Tags] : [],
                RuntimeMinutes = baseItem.RunTimeTicks.HasValue ? TimeSpan.FromTicks(baseItem.RunTimeTicks.Value).TotalMinutes : 0.0,
            };

            // Extract series name for episodes - only when needed for performance
            if (extractSeriesName)
            {
                ExtractSeriesName(operand, baseItem, libraryManager, cache, logger);
            }
            else
            {
                operand.SeriesName = string.Empty; // Ensure consistent default
                logger?.LogDebug("SeriesName extraction skipped for item {Name} - not needed by rules", baseItem.Name);
            }

            // Try to access user data properly
            try
            {
                if (userDataManager != null && userData != null)
                {
                    // Populate user-specific data for playlist user
                    // Normalize to "N" format (no dashes) to match UserPlaylists format
                    var normalizedUserId = user.Id.ToString("N");
                    PopulateUserData(operand, normalizedUserId, playbackStatus, userData!, baseItem, user, libraryManager, userDataManager, cache, logger);
                }
                else if (userDataManager != null)
                {
                    // Fallback when userData is null - treat as never played for playlist user
                    // Normalize to "N" format (no dashes) to match UserPlaylists format
                    SetUserDataFallbacks(operand, user.Id.ToString("N"), playbackStatus);
                }
                else
                {
                    // Fallback approach - try reflection and populate dictionaries for playlist user
                    var userDataProperty = baseItem.GetType().GetProperty("UserData");
                    if (userDataProperty != null)
                    {
                        var reflectedUserData = userDataProperty.GetValue(baseItem);
                        if (reflectedUserData != null)
                        {
                            // Recalculate playback status from reflected data to ensure consistency
                            // The initial playbackStatus was calculated with null userData, so it's "Unplayed"
                            // but reflectedUserData might have actual playback information
                            var recalculatedPlaybackStatus = CalculatePlaybackStatusFromReflected(reflectedUserData);
                            
                            // Use our helper method to populate user data consistently
                            // Normalize to "N" format (no dashes) to match UserPlaylists format
                            PopulateUserData(operand, user.Id.ToString("N"), recalculatedPlaybackStatus, reflectedUserData, baseItem, user, libraryManager, userDataManager, cache, logger);
                        }
                        else
                        {
                            // UserData is null - set fallback values for playlist user
                            // Normalize to "N" format (no dashes) to match UserPlaylists format
                            SetUserDataFallbacks(operand, user.Id.ToString("N"), playbackStatus);
                        }
                    }
                    else
                    {
                        // UserData property not found - set fallback values for playlist user
                        // Normalize to "N" format (no dashes) to match UserPlaylists format
                        SetUserDataFallbacks(operand, user.Id.ToString("N"), playbackStatus);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Error accessing user data for item {Name}", baseItem.Name);
                // Keep the fallback values we set above
            }

            // Extract user-specific data for additional users
            if (additionalUserIds != null && additionalUserIds.Count > 0 && userDataManager != null)
            {
                foreach (var userId in additionalUserIds)
                {
                    try
                    {
                        if (Guid.TryParse(userId, out var userGuid))
                        {
                            // Normalize the userId for dictionary keys
                            var normalizedUserId = userGuid.ToString("N");
                            
                            // Try to get user by ID
                            try
                            {
                                var targetUser = GetUserById(userManager, userGuid);
                                if (targetUser != null)
                                {
                                    // Get user data first for Jellyfin 10.11 compatibility - check cache first
                                    MediaBrowser.Controller.Entities.UserItemData? targetUserData = null;
                                    var targetUserDataCacheKey = (baseItem.Id, userGuid);
                                    if (cache.UserDataCache.TryGetValue(targetUserDataCacheKey, out var cachedTargetUserData))
                                    {
                                        targetUserData = cachedTargetUserData;
                                    }
                                    else
                                    {
                                        targetUserData = userDataManager.GetUserData(targetUser, baseItem);
                                        // Cache the result
                                        if (targetUserData != null)
                                        {
                                            cache.UserDataCache[targetUserDataCacheKey] = targetUserData;
                                        }
                                    }
                                    // Calculate playback status for additional user
                                    string userPlaybackStatus = CalculatePlaybackStatusForUser(
                                        baseItem,
                                        targetUser,
                                        libraryManager,
                                        userDataManager,
                                        targetUserData,
                                        cache,
                                        logger);

                                    if (targetUserData != null)
                                    {
                                        PopulateUserData(operand, normalizedUserId, userPlaybackStatus, targetUserData, baseItem, targetUser, libraryManager, userDataManager, cache, logger);
                                    }
                                    else
                                    {
                                        // Fallback values when targetUserData is null
                                        SetUserDataFallbacks(operand, normalizedUserId, userPlaybackStatus);
                                    }
                                }
                                else
                                {
                                    // User exists in system but GetUserById returned null - this is a legitimate "user not found" case
                                    logger?.LogWarning("User with ID {UserId} not found for user-specific data extraction. This playlist rule references a user that no longer exists.", userId);
                                    throw new InvalidOperationException($"User with ID {userId} not found. This playlist rule references a user that no longer exists.");
                                }
                            }
                            catch (InvalidOperationException ex) when (ex.Message.Contains("reflection") || ex.Message.Contains("internal structure"))
                            {
                                // This is a reflection failure, not a missing user - provide a more helpful error
                                logger?.LogError(ex, "Failed to access user manager via reflection for user {UserId}. This may be due to a Jellyfin version compatibility issue.", userId);
                                throw new InvalidOperationException($"Unable to access user information due to internal system changes. This plugin may need to be updated for this version of Jellyfin. Original error: {ex.Message}", ex);
                            }
                        }
                        else
                        {
                            logger?.LogWarning("Invalid user ID format: {UserId}", userId);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Re-throw InvalidOperationException to allow SmartPlaylist.cs to handle it properly
                        // This stops playlist processing when a referenced user no longer exists or when reflection fails
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Error extracting user data for user {UserId} on item {Name}", userId, baseItem.Name);
                    }
                }
            }

            operand.OfficialRating = baseItem.OfficialRating ?? "";

            // Extract CustomRating property
            operand.CustomRating = baseItem.CustomRating ?? "";

            // Extract ProductionLocations property
            operand.ProductionLocations = baseItem.ProductionLocations?.ToList() ?? [];

            // Extract Overview property using reflection
            try
            {
                var overviewProperty = baseItem.GetType().GetProperty("Overview");
                if (overviewProperty != null)
                {
                    var overviewValue = overviewProperty.GetValue(baseItem) as string;
                    operand.Overview = overviewValue ?? "";
                }
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Failed to extract Overview for item {Name}", baseItem.Name);
                operand.Overview = string.Empty;
            }

            operand.DateCreated = SafeToUnixTimeSeconds(baseItem.DateCreated);
            operand.DateLastRefreshed = SafeToUnixTimeSeconds(baseItem.DateLastRefreshed);
            operand.DateLastSaved = SafeToUnixTimeSeconds(baseItem.DateLastSaved);
            operand.DateModified = SafeToUnixTimeSeconds(baseItem.DateModified);

            // Extract ReleaseDate from PremiereDate property
            operand.ReleaseDate = DateUtils.GetReleaseDateUnixTimestamp(baseItem);

            operand.FolderPath = baseItem.ContainingFolderPath;

            // Fix null reference exception for Path
            operand.FileName = !string.IsNullOrEmpty(baseItem.Path) ?
                System.IO.Path.GetFileName(baseItem.Path) ?? "" : "";

            // Extract audio languages from media streams - only when needed for performance
            if (extractAudioLanguages || extractSubtitleLanguages)
            {
                if (extractAudioLanguages)
                {
                    ExtractAudioLanguages(operand, baseItem, cache, logger);
                }
                else
                {
                    operand.AudioLanguages = [];
                }
                
                // Extract subtitle languages when needed (same media stream data source)
                if (extractSubtitleLanguages)
                {
                    ExtractSubtitleLanguages(operand, baseItem, cache, logger);
                }
                else
                {
                    operand.SubtitleLanguages = [];
                }
            }
            else
            {
                operand.AudioLanguages = [];
                operand.SubtitleLanguages = [];
            }

            // Extract audio quality from media streams - only when needed for performance
            if (extractAudioQuality)
            {
                ExtractAudioQuality(operand, baseItem, cache, logger);
            }
            else
            {
                operand.AudioBitrate = 0;
                operand.AudioSampleRate = 0;
                operand.AudioBitDepth = 0;
                operand.AudioCodec = string.Empty;
                operand.AudioProfile = string.Empty;
                operand.AudioChannels = 0;
            }

            // Extract video quality from media streams - only when needed for performance
            if (extractVideoQuality)
            {
                // Extract resolution/framerate/video quality only for items that can have video streams
                if (MediaTypes.VideoStreamCapableSet.Contains(operand.ItemType))
                {
                    ExtractResolution(operand, baseItem, cache, logger);
                    ExtractFramerate(operand, baseItem, logger);
                    ExtractVideoQuality(operand, baseItem, cache, logger);
                }
                else
                {
                    // Clear video quality fields for non-video items
                    operand.Resolution = string.Empty;
                    operand.Framerate = null;
                    operand.VideoCodec = string.Empty;
                    operand.VideoProfile = string.Empty;
                    operand.VideoRange = string.Empty;
                    operand.VideoRangeType = string.Empty;
                    logger?.LogDebug("Video quality extraction skipped for non-video item {Name}", baseItem.Name);
                }
            }
            else
            {
                operand.Resolution = string.Empty;
                operand.Framerate = null;
                operand.VideoCodec = string.Empty;
                operand.VideoProfile = string.Empty;
                operand.VideoRange = string.Empty;
                operand.VideoRangeType = string.Empty;
            }

            // Extract people - only when needed for performance
            if (extractPeople)
            {
                ExtractPeople(operand, baseItem, libraryManager, cache, logger);
            }
            else
            {
                operand.People = [];
                operand.Actors = [];
                operand.ActorRoles = [];
                operand.Directors = [];
                operand.Composers = [];
                operand.Writers = [];
                operand.GuestStars = [];
                operand.Producers = [];
                operand.Conductors = [];
                operand.Lyricists = [];
                operand.Arrangers = [];
                operand.SoundEngineers = [];
                operand.Mixers = [];
                operand.Remixers = [];
                operand.Creators = [];
                operand.PersonArtists = [];
                operand.PersonAlbumArtists = [];
                operand.Authors = [];
                operand.Illustrators = [];
                operand.Pencilers = [];
                operand.Inkers = [];
                operand.Colorists = [];
                operand.Letterers = [];
                operand.CoverArtists = [];
                operand.Editors = [];
                operand.Translators = [];
                logger?.LogDebug("People extraction skipped for item {Name} - not needed by rules", baseItem.Name);
            }

            // Extract collections - only when needed for performance
            if (options.ExtractCollections)
            {
                operand.Collections = ExtractCollections(baseItem, user, libraryManager, cache, logger, options.CollectionRecursionDepth);
            }
            else
            {
                operand.Collections = [];
            }

            // Extract playlists - only when needed for performance
            // Note: Playlists don't support nesting (can't contain other playlists), so no recursion depth needed
            if (options.ExtractPlaylists)
            {
                operand.Playlists = ExtractPlaylists(baseItem, user, libraryManager, cache, logger, options.OriginListName);
            }
            else
            {
                operand.Playlists = [];
            }

            // Extract library name - cheap operation using Jellyfin's GetCollectionFolders API
            operand.LibraryName = ExtractLibraryName(baseItem, libraryManager, logger);

            // Extract parent series tags for episodes - only when needed for performance
            // This is an expensive operation (database lookup), so we use caching
            if (extractParentSeriesTags)
            {
                ExtractParentSeriesTags(operand, baseItem, libraryManager, cache, logger);
            }
            else
            {
                operand.ParentSeriesTags = [];
            }

            // Extract parent series studios for episodes - only when needed for performance
            // This is an expensive operation (database lookup), so we use caching
            if (extractParentSeriesStudios)
            {
                ExtractParentSeriesStudios(operand, baseItem, libraryManager, cache, logger);
            }
            else
            {
                operand.ParentSeriesStudios = [];
            }

            // Extract parent series genres for episodes - only when needed for performance
            // This is an expensive operation (database lookup), so we use caching
            if (extractParentSeriesGenres)
            {
                ExtractParentSeriesGenres(operand, baseItem, libraryManager, cache, logger);
            }
            else
            {
                operand.ParentSeriesGenres = [];
            }

            // Extract artists and album artists only for music-related items (cheap operations when applicable)
            if (MediaTypes.MusicRelatedSet.Contains(operand.ItemType))
            {
                ExtractArtists(operand, baseItem, logger);
            }
            else
            {
                operand.Artists = [];
                operand.AlbumArtists = [];
            }

            // Extract NextUnwatched status for each user - only when needed for performance
            operand.NextUnwatchedByUser = [];
            if (extractNextUnwatched)
            {
                try
                {
                    // Only process episodes - other item types cannot be "next unwatched"
                    // Use proper type checking instead of string comparison
                    if (baseItem is Episode)
                    {
                        var episodeType = baseItem.GetType();

                        // Use cached property lookups for better performance with thread-safe access
                        var parentIndexProperty = _parentIndexPropertyCache.GetOrAdd(episodeType, type => type.GetProperty("ParentIndexNumber"));
                        var indexProperty = _indexPropertyCache.GetOrAdd(episodeType, type => type.GetProperty("IndexNumber"));

                        if (parentIndexProperty != null && indexProperty != null)
                        {
                            // Safe extraction of season and episode numbers - handle both nullable and non-nullable int properties
                            var seasonNumber = ExtractIntValue(parentIndexProperty.GetValue(baseItem));
                            var episodeNumber = ExtractIntValue(indexProperty.GetValue(baseItem));

                            // Use helper to safely extract SeriesId and validate all required properties
                            if (TryGetEpisodeSeriesGuid(baseItem, out var seriesGuid) && seasonNumber.HasValue && episodeNumber.HasValue && userDataManager != null)
                            {
                                // Get all episodes in this series - use cache to avoid redundant database queries
                                var allEpisodes = GetCachedSeriesEpisodes(seriesGuid, user, libraryManager, cache, logger);

                                // First, calculate NextUnwatched for the main user (playlist user)
                                var mainUserNextUnwatched = IsNextUnwatchedEpisodeCached(allEpisodes, baseItem, user, seasonNumber.Value, episodeNumber.Value, includeUnwatchedSeries, seriesGuid, cache, userDataManager, logger);
                                // Normalize to "N" format (no dashes) to match UserPlaylists format
                                operand.NextUnwatchedByUser[user.Id.ToString("N")] = mainUserNextUnwatched;

                                // Then check for additional users
                                if (additionalUserIds != null)
                                {
                                    foreach (var userId in additionalUserIds)
                                    {
                                        if (Guid.TryParse(userId, out var userGuid))
                                        {
                                            // Normalize before using as keys
                                            var normalizedUserId = userGuid.ToString("N");
                                            var targetUser = GetUserById(userManager, userGuid);
                                            if (targetUser != null)
                                            {
                                                var episodesForUser = GetCachedSeriesEpisodes(seriesGuid, targetUser, libraryManager, cache, logger);
                                                var isNextUnwatched = IsNextUnwatchedEpisodeCached(episodesForUser, baseItem, targetUser, seasonNumber.Value, episodeNumber.Value, includeUnwatchedSeries, seriesGuid, cache, userDataManager, logger);
                                                operand.NextUnwatchedByUser[normalizedUserId] = isNextUnwatched;
                                            }
                                        }
                                        else
                                        {
                                            logger?.LogWarning("Invalid user ID format: {UserId}", userId);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to extract NextUnwatched status for item {Name}", baseItem.Name);
                }
            }

            // Extract LastEpisodeAirDate only for Series items when needed (expensive operation requiring episode queries)
            if (extractLastEpisodeAirDate && baseItem is Series)
            {
                ExtractLastEpisodeAirDate(operand, baseItem, libraryManager, cache, logger);
            }

            return operand;
        }



        /// <summary>
        /// Gets the item type name using efficient type checking instead of reflection
        /// </summary>
        /// <param name="item">The BaseItem to get the type name for</param>
        /// <returns>The item type name</returns>
        private static string GetItemTypeName(BaseItem item, ILogger? logger = null)
        {
            // First try direct type matching for performance
            var directMatch = item switch
            {
                Episode => MediaTypes.Episode,
                Series => MediaTypes.Series,
                Movie => MediaTypes.Movie,
                Audio => MediaTypes.Audio,
                MusicVideo => MediaTypes.MusicVideo,
                Video => MediaTypes.Video,
                Photo => MediaTypes.Photo,
                Book => MediaTypes.Book,
                _ => null,
            };

            if (directMatch != null)
            {
                return directMatch;
            }

            // Fallback to BaseItemKind mapping for types that don't have direct C# classes
            if (MediaTypes.BaseItemKindToMediaType.TryGetValue(item.GetBaseItemKind(), out var mappedType))
            {
                return mappedType;
            }

            // Log truly unknown types (not in our supported mapping)
            var typeName = item.GetType().Name;
            var baseItemKind = item.GetBaseItemKind().ToString();

            // Only log if it's not a known unsupported type to reduce noise
            if (!_knownUnsupportedTypes.Contains(typeName))
            {
                logger?.LogDebug("Unsupported item type encountered: {ItemType} (BaseItemKind: {BaseItemKind}) for item: {ItemName}",
                    typeName, baseItemKind, item.Name);
            }

            return typeName;
        }

        /// <summary>
        /// Gets all episodes for a series, using cache to avoid redundant database queries.
        /// </summary>
        /// <param name="seriesId">The series ID to get episodes for</param>
        /// <param name="user">User for the query context</param>
        /// <param name="libraryManager">Library manager for database queries</param>
        /// <param name="cache">Per-refresh cache to store results</param>
        /// <param name="logger">Logger for debugging</param>
        /// <param name="isVirtualItem">Optional filter for virtual items. If null, uses GetItemsResult. If specified, uses GetItemList with this value.</param>
        /// <returns>Array of all episodes in the series</returns>
        private static BaseItem[] GetCachedSeriesEpisodes(Guid seriesId, User user, ILibraryManager libraryManager, RefreshQueueServiceRefreshCache cache, ILogger? logger, bool? isVirtualItem = null)
        {
            var key = (seriesId, user.Id);
            if (cache.SeriesEpisodes.TryGetValue(key, out var cachedEpisodes))
            {
                // Get series name for better logging
                var seriesName = cache.SeriesNameById.TryGetValue(seriesId, out var name) ? name : "Unknown";
                logger?.LogDebug("[GetCachedSeriesEpisodes] Using cached episodes for series '{SeriesName}' ({SeriesId}), user {UserId}: {EpisodeCount} episodes",
                    seriesName, seriesId, user.Id, cachedEpisodes.Length);
                return cachedEpisodes;
            }

            logger?.LogDebug("[GetCachedSeriesEpisodes] Fetching episodes for series {SeriesId}, user {UserId} from database (cache miss)", seriesId, user.Id);

            BaseItem[] episodes;
            
            if (isVirtualItem.HasValue)
            {
                // Use GetItemList when IsVirtualItem filter is specified (preserves IsVirtualItem semantics)
                episodes = libraryManager.GetItemList(new InternalItemsQuery
                {
                    ParentId = seriesId,
                    IncludeItemTypes = [BaseItemKind.Episode],
                    Recursive = true,
                    IsVirtualItem = isVirtualItem.Value,
                    User = user
                }).ToArray();
            }
            else
            {
                // Use GetItemsResult when IsVirtualItem is not specified (original behavior for NextUnwatched)
                var episodeQuery = new InternalItemsQuery(user)
                {
                    IncludeItemTypes = [BaseItemKind.Episode],
                    ParentId = seriesId,
                    Recursive = true,
                };
                episodes = libraryManager.GetItemsResult(episodeQuery).Items.ToArray();
            }

            // Get series name for better logging
            var series = libraryManager.GetItemById(seriesId);
            var seriesNameForLog = series?.Name ?? "Unknown";
            logger?.LogDebug("[GetCachedSeriesEpisodes] Fetched {EpisodeCount} episodes for series '{SeriesName}' ({SeriesId}), user {UserId}",
                episodes.Length, seriesNameForLog, seriesId, user.Id);

            cache.SeriesEpisodes[key] = episodes;
            return episodes;
        }

        /// <summary>
        /// Determines if the current episode is the next unwatched episode for a user.
        /// Note: NextUnwatched is cached per refresh (per series/user/flag) to avoid recomputation,
        /// using live IsPlayed() data at calculation time.
        /// </summary>
        /// <param name="allEpisodes">All episodes in the series</param>
        /// <param name="currentEpisode">The episode to check</param>
        /// <param name="user">The user to check watch status for</param>
        /// <param name="currentSeason">Current episode's season number</param>
        /// <param name="currentEpisodeNumber">Current episode's episode number</param>
        /// <param name="includeUnwatchedSeries">Whether to include completely unwatched series</param>
        /// <param name="seriesId">The series ID for cache key generation</param>
        /// <param name="cache">Per-refresh cache to store calculation results</param>
        /// <param name="userDataManager">User data manager for retrieving user data</param>
        /// <param name="logger">Logger for debugging</param>
        /// <returns>True if this episode is the next unwatched episode for the user</returns>
        private static bool IsNextUnwatchedEpisodeCached(BaseItem[] allEpisodes, BaseItem currentEpisode, User user,
            int currentSeason, int currentEpisodeNumber, bool includeUnwatchedSeries, Guid seriesId, RefreshQueueServiceRefreshCache cache, IUserDataManager userDataManager, ILogger? logger)
        {
            // Use per-refresh cache to avoid O(E²) recomputation for large series
            // Cache is scoped to single refresh, so no staleness issues across refreshes
            var cacheKey = (seriesId, user.Id, includeUnwatchedSeries);
            if (!cache.NextUnwatched.TryGetValue(cacheKey, out var result))
            {
                logger?.LogDebug("[NextUnwatched] Calculating next unwatched episode for series {SeriesId}, user {UserId}", seriesId, user.Id);
                result = CalculateNextUnwatchedEpisodeInfo(allEpisodes, user, includeUnwatchedSeries, userDataManager, logger);
                cache.NextUnwatched[cacheKey] = result;
            }
            else
            {
                logger?.LogDebug("[NextUnwatched] Using cached result for series {SeriesId}, user {UserId}: S{Season}:E{Episode}",
                    seriesId, user.Id, result.Season, result.Episode);
            }

            // Check if the current episode matches the calculated next unwatched episode
            var isMatch = result.NextEpisodeId.HasValue &&
                   result.NextEpisodeId.Value == currentEpisode.Id &&
                   result.Season == currentSeason &&
                   result.Episode == currentEpisodeNumber;

            logger?.LogDebug("[NextUnwatched] Checking episode '{EpisodeName}' S{CurrentSeason}:E{CurrentEpisode} (ID: {CurrentId}) against calculated next unwatched S{CalcSeason}:E{CalcEpisode} (ID: {CalcId}) - Match: {IsMatch}",
                currentEpisode.Name, currentSeason, currentEpisodeNumber, currentEpisode.Id,
                result.Season, result.Episode, result.NextEpisodeId, isMatch);

            return isMatch;
        }

        /// <summary>
        /// Calculates the next unwatched episode info for a series and user (returns episode details).
        /// </summary>
        private static (Guid? NextEpisodeId, int Season, int Episode) CalculateNextUnwatchedEpisodeInfo(BaseItem[] allEpisodes, User user,
            bool includeUnwatchedSeries, IUserDataManager? userDataManager, ILogger? logger)
        {
            try
            {
                // Use the original logic to find the next unwatched episode
                var episodeList = allEpisodes.ToList();
                logger?.LogDebug("[NextUnwatched] Processing {TotalEpisodes} episodes for user {UserId}, includeUnwatchedSeries={IncludeUnwatched}",
                    episodeList.Count, user.Id, includeUnwatchedSeries);

                // Create a list of episode info with season/episode numbers (excluding season 0 specials)
                var episodeInfos = new List<(BaseItem Episode, int Season, int EpisodeNum, bool IsWatched)>();
                var skippedEpisodes = 0;
                var season0Episodes = 0;

                foreach (var episode in episodeList)
                {
                    var episodeType = episode.GetType();

                    // Use cached property lookups for better performance with thread-safe access
                    var parentIndexProperty = _parentIndexPropertyCache.GetOrAdd(episodeType, type => type.GetProperty("ParentIndexNumber"));
                    var indexProperty = _indexPropertyCache.GetOrAdd(episodeType, type => type.GetProperty("IndexNumber"));

                    if (parentIndexProperty != null && indexProperty != null)
                    {
                        // Safe extraction of season and episode numbers - handle both nullable and non-nullable int properties
                        var seasonNum = ExtractIntValue(parentIndexProperty.GetValue(episode));
                        var episodeNum = ExtractIntValue(indexProperty.GetValue(episode));

                        // Skip season 0 (specials) and only include episodes with valid season/episode numbers
                        if (seasonNum.HasValue && episodeNum.HasValue && seasonNum.Value > 0)
                        {
                            // Call IsPlayed() fresh each time to ensure real-time accuracy
                            // Get user data for Jellyfin 10.11 compatibility
                            var episodeUserData = userDataManager?.GetUserData(user, episode);
                            var isWatched = episodeUserData != null ? episode.IsPlayed(user, episodeUserData) : false;
                            episodeInfos.Add((episode, seasonNum.Value, episodeNum.Value, isWatched));
                            logger?.LogDebug("[NextUnwatched] Episode '{EpisodeName}' S{Season}:E{Episode} - Watched: {IsWatched}",
                                episode.Name, seasonNum.Value, episodeNum.Value, isWatched);
                        }
                        else
                        {
                            if (seasonNum.HasValue && seasonNum.Value == 0)
                            {
                                season0Episodes++;
                                logger?.LogDebug("[NextUnwatched] Skipping Season 0 special: '{EpisodeName}'", episode.Name);
                            }
                            else
                            {
                                skippedEpisodes++;
                                logger?.LogDebug("[NextUnwatched] Skipping episode '{EpisodeName}' - Missing metadata (Season: {Season}, Episode: {Episode})",
                                    episode.Name, seasonNum, episodeNum);
                            }
                        }
                    }
                    else
                    {
                        skippedEpisodes++;
                        logger?.LogDebug("[NextUnwatched] Skipping episode '{EpisodeName}' - Unable to access ParentIndexNumber/IndexNumber properties", episode.Name);
                    }
                }

                logger?.LogDebug("[NextUnwatched] Episode summary: {ValidEpisodes} valid episodes, {Season0} specials skipped, {Skipped} episodes skipped due to missing metadata",
                    episodeInfos.Count, season0Episodes, skippedEpisodes);

                // Sort episodes by season, then episode number
                var sortedEpisodes = episodeInfos.OrderBy(e => e.Season).ThenBy(e => e.EpisodeNum).ToList();

                // Find the first unwatched episode
                var (Episode, Season, EpisodeNum, IsWatched) = sortedEpisodes.FirstOrDefault(e => !e.IsWatched);

                if (Episode != null)
                {
                    logger?.LogDebug("[NextUnwatched] First unwatched episode found: '{EpisodeName}' S{Season}:E{Episode}",
                        Episode.Name, Season, EpisodeNum);

                    // If includeUnwatchedSeries is false, check if this is a completely unwatched series
                    if (!includeUnwatchedSeries)
                    {
                        // If ALL episodes are unwatched, this is a completely unwatched series - exclude it
                        if (sortedEpisodes.All(e => !e.IsWatched))
                        {
                            logger?.LogDebug("[NextUnwatched] Series is completely unwatched and includeUnwatchedSeries=false - excluding all episodes");
                            return (null, 0, 0); // No next unwatched episode,
                        }
                    }

                    logger?.LogDebug("[NextUnwatched] Calculated next unwatched: S{Season}:E{Episode} (ID: {EpisodeId})",
                        Season, EpisodeNum, Episode.Id);
                    return (Episode.Id, Season, EpisodeNum);
                }

                // If all episodes are watched, no episode is "next unwatched"
                logger?.LogDebug("[NextUnwatched] All episodes are watched - no next unwatched episode");
                return (null, 0, 0);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[NextUnwatched] Failed to calculate next unwatched episode info");
                return (null, 0, 0);
            }
        }

        /// <summary>
        /// Extracts the collections that a media item belongs to, with caching for performance.
        /// </summary>
        /// <param name="baseItem">The media item to check</param>
        /// <param name="user">The user context for collection access</param>
        /// <param name="libraryManager">Library manager to query collections</param>
        /// <param name="cache">Per-refresh cache to avoid repeated queries</param>
        /// <param name="logger">Logger for debugging</param>
        /// <returns>List of collection names this item belongs to</returns>
        private static List<string> ExtractCollections(BaseItem baseItem, User user, ILibraryManager libraryManager, RefreshQueueServiceRefreshCache cache, ILogger? logger, int recursionDepth = 0)
        {
            // Ensure recursion depth is within valid range (0 = no recursion, 1-10 = levels to traverse)
            recursionDepth = Math.Max(0, Math.Min(10, recursionDepth));

            // Check if we already have the result cached for this item (with matching depth)
            // We use a combined key to cache results per depth level
            var cacheKey = (baseItem.Id, recursionDepth);
            if (cache.ItemCollectionsWithDepth.TryGetValue(cacheKey, out var cachedCollections))
            {
                return cachedCollections;
            }

            // Fallback to legacy cache for depth=1 (backward compatibility)
            if (recursionDepth == 1 && cache.ItemCollections.TryGetValue(baseItem.Id, out var legacyCachedCollections))
            {
                return legacyCachedCollections;
            }

            var collections = new List<string>();

            try
            {
                // Load all collections once and cache them
                if (cache.AllCollections == null)
                {
                    logger?.LogDebug("Loading all collections for user {UserId} (cache miss)", user.Id);
                    var collectionQuery = new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = [BaseItemKind.BoxSet],
                        Recursive = true,
                    };

                    cache.AllCollections = [.. libraryManager.GetItemsResult(collectionQuery).Items];
                    logger?.LogDebug("Cached {CollectionCount} collections for user {UserId}", cache.AllCollections.Length, user.Id);

                    // Debug: Log collection names (only if debug level logging)
                    if (cache.AllCollections.Length <= 10) // Only log if reasonable number
                    {
                        foreach (var col in cache.AllCollections)
                        {
                            logger?.LogDebug("Found collection: '{CollectionName}' (ID: {CollectionId})", col.Name, col.Id);
                        }
                    }
                }

                // Build the collection direct children cache if it's empty (one-time operation)
                if (cache.CollectionDirectChildren.Count == 0 && cache.AllCollections.Length > 0)
                {
                    logger?.LogDebug("Building collection direct children cache for {CollectionCount} collections", cache.AllCollections.Length);

                    foreach (var collection in cache.AllCollections)
                    {
                        try
                        {
                            var directChildren = GetContainerDirectChildren(collection, user, libraryManager, logger, "Collection");
                            cache.CollectionDirectChildren[collection.Id] = directChildren;
                        }
                        catch (Exception ex)
                        {
                            logger?.LogDebug(ex, "Error getting direct children for collection '{CollectionName}'", collection.Name);
                            cache.CollectionDirectChildren[collection.Id] = [];
                        }
                    }

                    logger?.LogDebug("Collection direct children cache built with {CacheCount} collections", cache.CollectionDirectChildren.Count);
                }

                // Build the recursive membership cache for the requested depth
                var membershipCacheKey = recursionDepth;
                if (!cache.CollectionMembershipCacheByDepth.TryGetValue(membershipCacheKey, out var membershipCacheAtDepth))
                {
                    membershipCacheAtDepth = new Dictionary<Guid, HashSet<Guid>>();
                    cache.CollectionMembershipCacheByDepth[membershipCacheKey] = membershipCacheAtDepth;

                    logger?.LogDebug("Building collection membership cache for depth {Depth}", recursionDepth);

                    foreach (var collection in cache.AllCollections)
                    {
                        try
                        {
                            var allMembers = GetContainerMembersRecursive(
                                collection.Id,
                                0,  // Start at depth 0 (root level)
                                recursionDepth,
                                [],
                                cache.CollectionDirectChildren,
                                BaseItemKind.BoxSet,
                                logger);

                            membershipCacheAtDepth[collection.Id] = allMembers;

                            if (recursionDepth > 1)
                            {
                                logger?.LogDebug("Collection '{CollectionName}' has {MemberCount} members at depth {Depth}",
                                    collection.Name, allMembers.Count, recursionDepth);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger?.LogDebug(ex, "Error building recursive membership for collection '{CollectionName}'", collection.Name);
                            membershipCacheAtDepth[collection.Id] = [];
                        }
                    }

                    logger?.LogDebug("Collection membership cache built for depth {Depth} with {CacheCount} collections", recursionDepth, membershipCacheAtDepth.Count);
                }

                // Use the membership cache for O(1) membership checks
                foreach (var collection in cache.AllCollections)
                {
                    if (membershipCacheAtDepth.TryGetValue(collection.Id, out var membershipSet) &&
                        membershipSet.Contains(baseItem.Id))
                    {
                        collections.Add(collection.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to extract collections for item {Name}", baseItem.Name);
            }

            // Cache the result
            cache.ItemCollectionsWithDepth[cacheKey] = collections;
            if (recursionDepth == 1)
            {
                cache.ItemCollections[baseItem.Id] = collections; // Backward compatibility
            }
            return collections;
        }

        /// <summary>
        /// Extracts the library name that contains this item using Jellyfin's GetCollectionFolders API.
        /// Libraries in Jellyfin are represented as CollectionFolder items.
        /// This is a cheap path-based lookup, no caching needed.
        /// </summary>
        /// <param name="baseItem">The item to extract library name from</param>
        /// <param name="libraryManager">Library manager for library lookups</param>
        /// <param name="logger">Logger for debugging</param>
        /// <returns>The library name this item belongs to, or empty string if not found</returns>
        private static string ExtractLibraryName(BaseItem baseItem, ILibraryManager libraryManager, ILogger? logger)
        {
            try
            {
                var collectionFolders = libraryManager.GetCollectionFolders(baseItem);

                if (collectionFolders != null && collectionFolders.Count > 0)
                {
                    return collectionFolders[0].Name;
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to extract library name for item '{ItemName}'", baseItem.Name);
            }

            return string.Empty;
        }

        /// <summary>
        /// Tries to get children using common reflection methods (GetChildren and GetLinkedChildren).
        /// </summary>
        private static BaseItem[]? TryGetChildrenViaReflection(BaseItem container, User user, ILogger? logger, string containerType)
        {
            // Approach 1: Try GetChildren method using reflection
            try
            {
                var getChildrenMethod = container.GetType().GetMethod("GetChildren", [typeof(User), typeof(bool)]);
                if (getChildrenMethod != null)
                {
                    var result = getChildrenMethod.Invoke(container, [user, true]);
                    if (result is IEnumerable<BaseItem> childrenEnumerable)
                    {
                        BaseItem[] children = [.. childrenEnumerable];
                        logger?.LogDebug("{ContainerType} '{ContainerName}' GetChildren() returned {ItemCount} items", containerType, container.Name, children.Length);
                        return children;
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "GetChildren method failed for {ContainerType} '{ContainerName}'", containerType, container.Name);
            }

            // Approach 2: Try GetLinkedChildren method using reflection
            try
            {
                var getLinkedChildrenMethod = container.GetType().GetMethod("GetLinkedChildren", Type.EmptyTypes);
                if (getLinkedChildrenMethod != null)
                {
                    var linkedChildren = getLinkedChildrenMethod.Invoke(container, null);
                    if (linkedChildren is IEnumerable<BaseItem> linkedEnumerable)
                    {
                        BaseItem[] children = [.. linkedEnumerable];
                        logger?.LogDebug("{ContainerType} '{ContainerName}' GetLinkedChildren() returned {ItemCount} items", containerType, container.Name, children.Length);
                        return children;
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "GetLinkedChildren method failed for {ContainerType} '{ContainerName}'", containerType, container.Name);
            }

            return null;
        }

        /// <summary>
        /// Gets direct children of a container (collection or playlist) using reflection.
        /// </summary>
        private static BaseItem[] GetContainerDirectChildren(BaseItem container, User user, ILibraryManager libraryManager, ILogger? logger, string containerType)
        {
            // Try common reflection methods first
            var children = TryGetChildrenViaReflection(container, user, logger, containerType);
            if (children != null && children.Length > 0)
            {
                return children;
            }

            // Fallback to ParentId query (direct children only, not recursive)
            var query = new InternalItemsQuery(user)
            {
                ParentId = container.Id,
                Recursive = false,
            };

            children = [.. libraryManager.GetItemsResult(query).Items];
            logger?.LogDebug("{ContainerType} '{ContainerName}' ParentId query returned {ItemCount} items", containerType, container.Name, children.Length);

            return children ?? [];
        }

        /// <summary>
        /// Recursively gets all member IDs from a container, traversing nested containers up to maxDepth.
        /// </summary>
        private static HashSet<Guid> GetContainerMembersRecursive(
            Guid containerId,
            int currentDepth,
            int maxDepth,
            HashSet<Guid> visitedIds,
            ConcurrentDictionary<Guid, BaseItem[]> directChildrenCache,
            BaseItemKind containerKind,
            ILogger? logger)
        {
            var result = new HashSet<Guid>();

            // Circular reference protection
            if (visitedIds.Contains(containerId))
            {
                logger?.LogDebug("Circular reference detected for container {ContainerId}, skipping", containerId);
                return result;
            }

            // Get direct children from cache
            if (!directChildrenCache.TryGetValue(containerId, out var directChildren) || directChildren.Length == 0)
            {
                return result;
            }

            // Track this container as visited for circular reference protection
            var newVisitedIds = new HashSet<Guid>(visitedIds) { containerId };

            foreach (var child in directChildren)
            {
                // Always add the child's ID (it's a member of this container)
                result.Add(child.Id);

                // If this child is also a container of the same type and we haven't reached max depth,
                // recursively get its members too
                // Depth semantics: 0 = direct children only, 1+ = traverse N additional levels
                if (maxDepth > 0 && currentDepth < maxDepth && child.GetBaseItemKind() == containerKind)
                {
                    var nestedMembers = GetContainerMembersRecursive(
                        child.Id,
                        currentDepth + 1,
                        maxDepth,
                        newVisitedIds,
                        directChildrenCache,
                        containerKind,
                        logger);

                    foreach (var nestedMember in nestedMembers)
                    {
                        result.Add(nestedMember);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Extracts the playlists that a media item belongs to, with caching for performance.
        /// Note: Playlists in Jellyfin only contain media items (not other playlists), so no recursion is needed.
        /// </summary>
        /// <param name="baseItem">The media item to check</param>
        /// <param name="user">The user context for playlist access</param>
        /// <param name="libraryManager">Library manager to query playlists</param>
        /// <param name="cache">Per-refresh cache to avoid repeated queries</param>
        /// <param name="logger">Logger for debugging</param>
        /// <param name="originListName">Name of the playlist/collection being built (to prevent self-reference)</param>
        /// <returns>List of playlist names this item belongs to</returns>
        private static List<string> ExtractPlaylists(BaseItem baseItem, User user, ILibraryManager libraryManager, RefreshQueueServiceRefreshCache cache, ILogger? logger, string? originListName)
        {
            // Check if we already have the result cached for this item
            if (cache.ItemPlaylists.TryGetValue(baseItem.Id, out var cachedPlaylists))
            {
                return cachedPlaylists;
            }

            var playlists = new List<string>();

            try
            {
                // Load all playlists once and cache them
                if (cache.AllPlaylists == null)
                {
                    logger?.LogDebug("Loading all playlists for user {UserId} (cache miss)", user.Id);
                    var playlistQuery = new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = [BaseItemKind.Playlist],
                        Recursive = true,
                    };

                    var allPlaylists = libraryManager.GetItemsResult(playlistQuery).Items;

                    // Filter playlists to only include those the user owns or that are public
                    var accessiblePlaylists = new List<BaseItem>();
                    foreach (var playlist in allPlaylists)
                    {
                        // Check if user owns the playlist
                        bool isOwner = playlist.GetType().GetProperty("OwnerUserId")?.GetValue(playlist) is Guid ownerId
                            && ownerId == user.Id;

                        // Check if playlist is public
                        bool isPublic = false;
                        var openAccessProperty = playlist.GetType().GetProperty("OpenAccess");
                        if (openAccessProperty != null)
                        {
                            isPublic = (bool)(openAccessProperty.GetValue(playlist) ?? false);
                        }
                        else
                        {
                            // Fallback to Shares check using reflection
                            var sharesProperty = playlist.GetType().GetProperty("Shares");
                            if (sharesProperty != null)
                            {
                                var sharesValue = sharesProperty.GetValue(playlist);
                                if (sharesValue is System.Collections.IEnumerable shares)
                                {
                                    isPublic = shares.Cast<object>().Any();
                                }
                            }
                        }

                        if (isOwner || isPublic)
                        {
                            accessiblePlaylists.Add(playlist);
                            logger?.LogDebug("Playlist '{PlaylistName}' accessible: Owner={IsOwner}, Public={IsPublic}",
                                playlist.Name, isOwner, isPublic);
                        }
                        else
                        {
                            logger?.LogDebug("Playlist '{PlaylistName}' filtered out: not owned by user and not public",
                                playlist.Name);
                        }
                    }

                    cache.AllPlaylists = [.. accessiblePlaylists];
                    logger?.LogDebug("Cached {PlaylistCount} accessible playlists for user {UserId} (filtered from {TotalCount})",
                        cache.AllPlaylists.Length, user.Id, allPlaylists.Count);

                    // Debug: Log playlist names (only if debug level logging)
                    if (cache.AllPlaylists.Length <= 10) // Only log if reasonable number
                    {
                        foreach (var pl in cache.AllPlaylists)
                        {
                            logger?.LogDebug("Found playlist: '{PlaylistName}' (ID: {PlaylistId})", pl.Name, pl.Id);
                        }
                    }
                }

                // Build the membership cache if it's empty (one-time operation per refresh)
                if (cache.PlaylistMembershipCache.Count == 0 && cache.AllPlaylists.Length > 0)
                {
                    logger?.LogDebug("Building playlist membership cache for {PlaylistCount} playlists", cache.AllPlaylists.Length);

                    foreach (var playlist in cache.AllPlaylists)
                    {
                        try
                        {
                            var directChildren = GetPlaylistDirectChildren(playlist, user, libraryManager, logger);
                            var membershipSet = new HashSet<Guid>();
                            foreach (var child in directChildren)
                            {
                                membershipSet.Add(child.Id);
                            }
                            cache.PlaylistMembershipCache[playlist.Id] = membershipSet;
                        }
                        catch (Exception ex)
                        {
                            logger?.LogDebug(ex, "Error building membership cache for playlist '{PlaylistName}'", playlist.Name);
                            cache.PlaylistMembershipCache[playlist.Id] = [];
                        }
                    }

                    logger?.LogDebug("Playlist membership cache built with {CacheCount} playlists", cache.PlaylistMembershipCache.Count);
                }

                // Use the membership cache for O(1) membership checks
                foreach (var playlist in cache.AllPlaylists)
                {
                    if (cache.PlaylistMembershipCache.TryGetValue(playlist.Id, out var membershipSet) &&
                        membershipSet.Contains(baseItem.Id))
                    {
                        // Skip if this playlist matches the origin list (prevent self-reference)
                        if (!string.IsNullOrEmpty(originListName))
                        {
                            var playlistBaseName = NameFormatter.StripPrefixAndSuffix(playlist.Name);
                            var originBaseName = NameFormatter.StripPrefixAndSuffix(originListName);
                            if (playlistBaseName.Equals(originBaseName, StringComparison.OrdinalIgnoreCase))
                            {
                                logger?.LogDebug("Skipping playlist '{PlaylistName}' for item '{ItemName}' - matches origin list '{OriginName}' (preventing self-reference)",
                                    playlist.Name, baseItem.Name, originListName);
                                continue;
                            }
                        }

                        playlists.Add(playlist.Name);
                        logger?.LogDebug("Item '{ItemName}' is in playlist '{PlaylistName}'", baseItem.Name, playlist.Name);
                    }
                }

                if (playlists.Count == 0)
                {
                    logger?.LogDebug("Item '{ItemName}' is not in any playlists", baseItem.Name);
                }

            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to extract playlists for item {Name}", baseItem.Name);
            }

            // Cache the result
            cache.ItemPlaylists[baseItem.Id] = playlists;
            logger?.LogDebug("Cached {Count} playlists for item '{ItemName}'", playlists.Count, baseItem.Name);
            return playlists;
        }

        /// <summary>
        /// Gets direct children of a playlist using reflection.
        /// </summary>
        private static BaseItem[] GetPlaylistDirectChildren(BaseItem playlist, User user, ILibraryManager libraryManager, ILogger? logger)
        {
            // Try common reflection methods first
            var children = TryGetChildrenViaReflection(playlist, user, logger, "Playlist");
            if (children != null && children.Length > 0)
            {
                return children;
            }

            // Fallback: Try accessing LinkedChildren property directly (playlist-specific)
            try
            {
                var linkedChildrenProp = playlist.GetType().GetProperty("LinkedChildren");
                if (linkedChildrenProp != null)
                {
                    var linkedChildrenValue = linkedChildrenProp.GetValue(playlist);
                    if (linkedChildrenValue is Array linkedChildrenArray)
                    {
                        var itemIds = new List<Guid>();
                        foreach (var linkedChild in linkedChildrenArray)
                        {
                            var itemIdProp = linkedChild.GetType().GetProperty("ItemId");
                            if (itemIdProp != null)
                            {
                                var itemIdValue = itemIdProp.GetValue(linkedChild);
                                if (itemIdValue is Guid guidValue)
                                {
                                    itemIds.Add(guidValue);
                                }
                            }
                        }

                        children = itemIds
                            .Select(id => libraryManager.GetItemById(id))
                            .Where(item => item != null)
                            .Cast<BaseItem>()
                            .ToArray();

                        logger?.LogDebug("Playlist '{PlaylistName}' LinkedChildren property returned {ItemCount} items", playlist.Name, children.Length);
                        return children;
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "LinkedChildren property access failed for playlist '{PlaylistName}'", playlist.Name);
            }

            return [];
        }

        /// <summary>
        /// Gets a user by ID using the user manager.
        /// </summary>
        /// <param name="userManager">The user manager instance.</param>
        /// <param name="userId">The user ID to look up.</param>
        /// <returns>The user object if found, null otherwise.</returns>
        public static User? GetUserById(IUserManager userManager, Guid userId)
        {
            if (userManager == null)
            {
                throw new InvalidOperationException("UserManager is null - cannot retrieve user information.");
            }

            return userManager.GetUserById(userId);
        }

        /// <summary>
        /// Safely converts a DateTime to Unix timestamp, handling invalid dates.
        /// Treats the DateTime as UTC to ensure consistency with other date handling in the plugin.
        /// </summary>
        /// <param name="dateTime">The DateTime to convert.</param>
        /// <returns>Unix timestamp in seconds, or 0 if the date is invalid.</returns>
        private static double SafeToUnixTimeSeconds(DateTime dateTime)
        {
            try
            {
                // Check if the date is within valid range for DateTimeOffset
                if (dateTime < new DateTime(1, 1, 1) || dateTime > new DateTime(9999, 12, 31))
                {
                    return 0; // Return 0 for invalid dates,
                }

                // Check for common invalid dates
                if (dateTime == DateTime.MinValue || dateTime == DateTime.MaxValue)
                {
                    return 0;
                }

                // Treat the DateTime as UTC to ensure consistency with other date handling in the plugin
                // This assumes Jellyfin stores dates in UTC, which is the typical behavior
                return new DateTimeOffset(dateTime, TimeSpan.Zero).ToUnixTimeSeconds();
            }
            catch (ArgumentOutOfRangeException)
            {
                // If DateTimeOffset creation fails, return 0
                return 0;
            }
            catch (Exception)
            {
                // For any other unexpected errors, return 0
                return 0;
            }
        }

        /// <summary>
        /// Default comparison fields for Similar To matching - Genre and Tags provide the best balance
        /// of accuracy and performance. Exposed as IReadOnlyList to prevent accidental mutation.
        /// </summary>
        public static IReadOnlyList<string> DefaultSimilarityComparisonFields { get; } = new[] { "Genre", "Tags" };

        /// <summary>
        /// Reference metadata extracted from similar-to queries for comparison.
        /// Uses Lists instead of HashSets to preserve duplicates - duplicates represent stronger signals
        /// when multiple reference items share the same metadata.
        /// </summary>
        public sealed class ReferenceMetadata
        {
            public List<string> Genres { get; set; } = [];
            public List<string> Tags { get; set; } = [];
            public List<string> Actors { get; set; } = [];
            public List<string> ActorRoles { get; set; } = [];
            public List<string> Directors { get; set; } = [];
            public List<string> Composers { get; set; } = [];
            public List<string> Writers { get; set; } = [];
            public List<string> GuestStars { get; set; } = [];
            public List<string> Producers { get; set; } = [];
            public List<string> Conductors { get; set; } = [];
            public List<string> Lyricists { get; set; } = [];
            public List<string> Arrangers { get; set; } = [];
            public List<string> SoundEngineers { get; set; } = [];
            public List<string> Mixers { get; set; } = [];
            public List<string> Remixers { get; set; } = [];
            public List<string> Creators { get; set; } = [];
            public List<string> PersonArtists { get; set; } = [];
            public List<string> PersonAlbumArtists { get; set; } = [];
            public List<string> Authors { get; set; } = [];
            public List<string> Illustrators { get; set; } = [];
            public List<string> Pencilers { get; set; } = [];
            public List<string> Inkers { get; set; } = [];
            public List<string> Colorists { get; set; } = [];
            public List<string> Letterers { get; set; } = [];
            public List<string> CoverArtists { get; set; } = [];
            public List<string> Editors { get; set; } = [];
            public List<string> Translators { get; set; } = [];
            public List<string> Studios { get; set; } = [];
            public List<string> AudioLanguages { get; set; } = [];
            public List<string> Names { get; set; } = [];
            public List<int> ProductionYears { get; set; } = [];
            public List<string> ParentalRatings { get; set; } = [];

            // Cached frequency maps (built once; reused for every candidate item)
            // This avoids rebuilding dictionaries thousands of times for large playlists
            public IReadOnlyDictionary<string, int> GenreFreq { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public IReadOnlyDictionary<string, int> TagFreq { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public IReadOnlyDictionary<string, int> ActorFreq { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public IReadOnlyDictionary<string, int> ActorRoleFreq { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public IReadOnlyDictionary<string, int> DirectorFreq { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public IReadOnlyDictionary<string, int> WriterFreq { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public IReadOnlyDictionary<string, int> ProducerFreq { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public IReadOnlyDictionary<string, int> StudioFreq { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public IReadOnlyDictionary<string, int> AudioLangFreq { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Builds reference metadata from SimilarTo expressions by finding and aggregating metadata from matching items.
        /// </summary>
        /// <param name="similarToExpressions">List of SimilarTo expressions to process</param>
        /// <param name="allItems">All items to search through for matches</param>
        /// <param name="comparisonFields">List of fields to extract for comparison (e.g., ["Genre", "Tags"])</param>
        /// <param name="libraryManager">Library manager for accessing expensive fields like People</param>
        /// <param name="logger">Logger for debugging</param>
        /// <returns>Aggregated reference metadata</returns>
        public static ReferenceMetadata BuildReferenceMetadata(
            List<Expression> similarToExpressions,
            IEnumerable<BaseItem> allItems,
            List<string> comparisonFields,
            ILibraryManager libraryManager,
            ILogger? logger)
        {
            var referenceMetadata = new ReferenceMetadata();

            if (similarToExpressions == null || similarToExpressions.Count == 0)
            {
                logger?.LogDebug("No SimilarTo expressions to process");
                return referenceMetadata;
            }

            var referenceItems = new List<BaseItem>();

            // Find all items matching the SimilarTo expressions
            foreach (var expr in similarToExpressions)
            {
                if (string.IsNullOrWhiteSpace(expr?.TargetValue))
                {
                    logger?.LogWarning("SimilarTo expression has null or empty target value");
                    continue;
                }

                logger?.LogDebug("Processing SimilarTo expression: {Operator} '{Value}'", expr.Operator, expr.TargetValue);

                // Reject negative operators for SimilarTo (they would match most of the library)
                if (expr.Operator == "NotContains" || expr.Operator == "IsNotIn" || expr.Operator == "NotEqual")
                {
                    logger?.LogWarning("Negative operator '{Operator}' is not supported for SimilarTo field (would match too many items). Skipping this expression.", expr.Operator);
                    continue;
                }

                // Apply the operator to find matching items
                var matchingItems = allItems.Where(item =>
                {
                    if (item?.Name == null) return false;

                    return expr.Operator switch
                    {
                        "Equal" => item.Name.Equals(expr.TargetValue, StringComparison.OrdinalIgnoreCase),
                        "Contains" => item.Name.Contains(expr.TargetValue, StringComparison.OrdinalIgnoreCase),
                        "IsIn" => IsNameInList(item.Name, expr.TargetValue),
                        "MatchRegex" => MatchesRegex(item.Name, expr.TargetValue, logger),
                        _ => false,
                    };
                }).ToList();

                logger?.LogDebug("Found {Count} items matching SimilarTo query '{Value}'", matchingItems.Count, expr.TargetValue);

                referenceItems.AddRange(matchingItems);
            }

            // Deduplicate reference items by ID
            referenceItems = referenceItems.DistinctBy(item => item.Id).ToList();

            logger?.LogDebug("Total reference items after deduplication: {Count}", referenceItems.Count);

            if (referenceItems.Count == 0)
            {
                logger?.LogWarning("No reference items found for SimilarTo queries");
                return referenceMetadata;
            }

            // Log reference item names for debugging
            foreach (var item in referenceItems.Take(10))
            {
                logger?.LogDebug("Reference item: '{Name}'", item.Name);
            }

            // Default to Genre and Tags if no comparison fields specified (backwards compatibility)
            if (comparisonFields == null || comparisonFields.Count == 0)
            {
                comparisonFields = DefaultSimilarityComparisonFields.ToList();
            }

            // Normalize comparison field names (trim, deduplicate, case-insensitive) for consistency
            comparisonFields = comparisonFields
                .Select(f => f?.Trim())
                .Where(f => !string.IsNullOrEmpty(f))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            logger?.LogDebug("Extracting comparison fields: {Fields}", string.Join(", ", comparisonFields));

            // Extract and aggregate metadata from reference items based on selected comparison fields
            foreach (var item in referenceItems)
            {
                // Pre-fetch people data once per item if any people fields are needed (performance optimization)
                CategorizedPeople? categorizedPeople = null;
                bool needsPeople = comparisonFields.Any(f => FieldRegistry.IsPeopleField(f));

                if (needsPeople && libraryManager != null)
                {
                    try
                    {
                        var peopleQuery = new InternalPeopleQuery { ItemId = item.Id };

                        // Reuse cached GetPeople method lookup for better performance
                        var getPeopleMethod = _getPeopleMethodCache;
                        if (getPeopleMethod == null)
                        {
                            lock (_getPeopleMethodLock)
                            {
                                if (_getPeopleMethodCache == null)
                                {
                                    _getPeopleMethodCache = libraryManager.GetType().GetMethod("GetPeople", new[] { typeof(InternalPeopleQuery) });
                                }
                                getPeopleMethod = _getPeopleMethodCache;
                            }
                        }

                        if (getPeopleMethod != null)
                        {
                            var result = getPeopleMethod.Invoke(libraryManager, new object[] { peopleQuery });
                            if (result is IEnumerable<object> peopleEnum)
                            {
                                categorizedPeople = CategorizePeople(peopleEnum, logger);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogDebug(ex, "Failed to extract people for reference item {ItemName}", item.Name);
                    }
                }

                foreach (var field in comparisonFields)
                {
                    // Normalize field name to lowercase for truly case-insensitive switch
                    // CA1308 suppressed: Using lowercase for internal field name comparison, not security-sensitive
#pragma warning disable CA1308
                    var fieldKey = (field ?? string.Empty).Trim().ToLowerInvariant();
#pragma warning restore CA1308

                    switch (fieldKey)
                    {
                        case "genre":
                            if (item.Genres != null)
                            {
                                foreach (var genre in item.Genres)
                                {
                                    if (!string.IsNullOrWhiteSpace(genre))
                                    {
                                        referenceMetadata.Genres.Add(genre);
                                    }
                                }
                            }
                            break;

                        case "tags":
                            if (item.Tags != null)
                            {
                                foreach (var tag in item.Tags)
                                {
                                    if (!string.IsNullOrWhiteSpace(tag))
                                    {
                                        referenceMetadata.Tags.Add(tag);
                                    }
                                }
                            }
                            break;

                        case "people":
                        case "actors":
                        case "actorroles":
                        case "directors":
                        case "composers":
                        case "writers":
                        case "gueststars":
                        case "producers":
                        case "conductors":
                        case "lyricists":
                        case "arrangers":
                        case "soundengineers":
                        case "mixers":
                        case "remixers":
                        case "creators":
                        case "personartists":
                        case "personalbumartists":
                        case "authors":
                        case "illustrators":
                        case "pencilers":
                        case "inkers":
                        case "colorists":
                        case "letterers":
                        case "coverartists":
                        case "editors":
                        case "translators":
                            // Use pre-fetched categorized people data (queried once per item for all roles)
                            if (categorizedPeople != null)
                            {
                                var sourceList = fieldKey switch
                                {
                                    "people" => categorizedPeople.AllPeople,
                                    "actors" => categorizedPeople.Actors,
                                    "actorroles" => categorizedPeople.ActorRoles,
                                    "directors" => categorizedPeople.Directors,
                                    "composers" => categorizedPeople.Composers,
                                    "writers" => categorizedPeople.Writers,
                                    "gueststars" => categorizedPeople.GuestStars,
                                    "producers" => categorizedPeople.Producers,
                                    "conductors" => categorizedPeople.Conductors,
                                    "lyricists" => categorizedPeople.Lyricists,
                                    "arrangers" => categorizedPeople.Arrangers,
                                    "soundengineers" => categorizedPeople.SoundEngineers,
                                    "mixers" => categorizedPeople.Mixers,
                                    "remixers" => categorizedPeople.Remixers,
                                    "creators" => categorizedPeople.Creators,
                                    "personartists" => categorizedPeople.PersonArtists,
                                    "personalbumartists" => categorizedPeople.PersonAlbumArtists,
                                    "authors" => categorizedPeople.Authors,
                                    "illustrators" => categorizedPeople.Illustrators,
                                    "pencilers" => categorizedPeople.Pencilers,
                                    "inkers" => categorizedPeople.Inkers,
                                    "colorists" => categorizedPeople.Colorists,
                                    "letterers" => categorizedPeople.Letterers,
                                    "coverartists" => categorizedPeople.CoverArtists,
                                    "editors" => categorizedPeople.Editors,
                                    "translators" => categorizedPeople.Translators,
                                    _ => null,
                                };

                                var targetList = fieldKey switch
                                {
                                    "people" => referenceMetadata.Actors, // Note: "People (All)" aggregates all person types, using Actors as proxy for SimilarTo
                                    "actors" => referenceMetadata.Actors,
                                    "actorroles" => referenceMetadata.ActorRoles,
                                    "directors" => referenceMetadata.Directors,
                                    "composers" => referenceMetadata.Composers,
                                    "writers" => referenceMetadata.Writers,
                                    "gueststars" => referenceMetadata.GuestStars,
                                    "producers" => referenceMetadata.Producers,
                                    "conductors" => referenceMetadata.Conductors,
                                    "lyricists" => referenceMetadata.Lyricists,
                                    "arrangers" => referenceMetadata.Arrangers,
                                    "soundengineers" => referenceMetadata.SoundEngineers,
                                    "mixers" => referenceMetadata.Mixers,
                                    "remixers" => referenceMetadata.Remixers,
                                    "creators" => referenceMetadata.Creators,
                                    "personartists" => referenceMetadata.PersonArtists,
                                    "personalbumartists" => referenceMetadata.PersonAlbumArtists,
                                    "authors" => referenceMetadata.Authors,
                                    "illustrators" => referenceMetadata.Illustrators,
                                    "pencilers" => referenceMetadata.Pencilers,
                                    "inkers" => referenceMetadata.Inkers,
                                    "colorists" => referenceMetadata.Colorists,
                                    "letterers" => referenceMetadata.Letterers,
                                    "coverartists" => referenceMetadata.CoverArtists,
                                    "editors" => referenceMetadata.Editors,
                                    "translators" => referenceMetadata.Translators,
                                    _ => null,
                                };

                                if (sourceList != null && targetList != null)
                                {
                                    targetList.AddRange(sourceList);
                                }
                            }
                            break;

                        case "studios":
                            if (item.Studios != null)
                            {
                                foreach (var studio in item.Studios)
                                {
                                    if (!string.IsNullOrWhiteSpace(studio))
                                    {
                                        referenceMetadata.Studios.Add(studio);
                                    }
                                }
                            }
                            break;

                        case "audio languages":
                            // Reuse compatibility helper to extract audio languages via reflection-backed paths
                            // This avoids direct GetMediaStreams() call which can fail on some BaseItem types/Jellyfin versions
                            try
                            {
                                var tempOperand = new Operand(item.Name);
                                ExtractAudioLanguages(tempOperand, item, null, logger); // No cache available in BuildReferenceMetadata
                                if (tempOperand.AudioLanguages != null && tempOperand.AudioLanguages.Count > 0)
                                {
                                    referenceMetadata.AudioLanguages.AddRange(tempOperand.AudioLanguages);
                                }
                            }
                            catch (Exception ex)
                            {
                                logger?.LogDebug(ex, "Failed to extract audio languages for reference item {ItemName}", item.Name);
                            }
                            break;

                        case "name":
                            if (!string.IsNullOrWhiteSpace(item.Name))
                            {
                                referenceMetadata.Names.Add(item.Name);
                            }
                            break;

                        case "production year":
                            if (item.ProductionYear.HasValue && item.ProductionYear.Value > 0)
                            {
                                referenceMetadata.ProductionYears.Add(item.ProductionYear.Value);
                            }
                            break;

                        case "parental rating":
                            if (!string.IsNullOrWhiteSpace(item.OfficialRating))
                            {
                                referenceMetadata.ParentalRatings.Add(item.OfficialRating);
                            }
                            break;
                    }
                }
            }

            // PERFORMANCE: Build frequency maps once here for O(1) lookups during scoring
            // This avoids rebuilding dictionaries for every candidate item (huge win on large libraries)
            referenceMetadata.GenreFreq = referenceMetadata.Genres
                .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            referenceMetadata.TagFreq = referenceMetadata.Tags
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(t => t.Key, t => t.Count(), StringComparer.OrdinalIgnoreCase);

            referenceMetadata.ActorFreq = referenceMetadata.Actors
                .GroupBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(a => a.Key, a => a.Count(), StringComparer.OrdinalIgnoreCase);

            referenceMetadata.ActorRoleFreq = referenceMetadata.ActorRoles
                .GroupBy(r => r, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(r => r.Key, r => r.Count(), StringComparer.OrdinalIgnoreCase);

            referenceMetadata.WriterFreq = referenceMetadata.Writers
                .GroupBy(w => w, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(w => w.Key, w => w.Count(), StringComparer.OrdinalIgnoreCase);

            referenceMetadata.ProducerFreq = referenceMetadata.Producers
                .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(p => p.Key, p => p.Count(), StringComparer.OrdinalIgnoreCase);

            referenceMetadata.DirectorFreq = referenceMetadata.Directors
                .GroupBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(d => d.Key, d => d.Count(), StringComparer.OrdinalIgnoreCase);

            referenceMetadata.StudioFreq = referenceMetadata.Studios
                .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(s => s.Key, s => s.Count(), StringComparer.OrdinalIgnoreCase);

            referenceMetadata.AudioLangFreq = referenceMetadata.AudioLanguages
                .GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(l => l.Key, l => l.Count(), StringComparer.OrdinalIgnoreCase);

            logger?.LogDebug("Reference metadata - Genres: {GenreCount}, Tags: {TagCount}, Actors: {ActorCount}, Writers: {WriterCount}, Producers: {ProducerCount}, Directors: {DirectorCount}, Studios: {StudioCount}, AudioLanguages: {AudioCount}, Names: {NameCount}, ProductionYears: {YearCount}, ParentalRatings: {RatingCount}",
                referenceMetadata.Genres.Count, referenceMetadata.Tags.Count, referenceMetadata.Actors.Count, referenceMetadata.Writers.Count, referenceMetadata.Producers.Count, referenceMetadata.Directors.Count, referenceMetadata.Studios.Count, referenceMetadata.AudioLanguages.Count, referenceMetadata.Names.Count, referenceMetadata.ProductionYears.Count, referenceMetadata.ParentalRatings.Count);

            return referenceMetadata;
        }

        /// <summary>
        /// Calculates similarity score for an operand against reference metadata.
        /// </summary>
        /// <param name="operand">The operand to calculate similarity for</param>
        /// <param name="referenceMetadata">Reference metadata to compare against</param>
        /// <param name="comparisonFields">List of fields being compared</param>
        /// <param name="logger">Logger for debugging</param>
        /// <returns>True if item passes similarity threshold, false otherwise</returns>
        public static bool CalculateSimilarityScore(
            Operand operand,
            ReferenceMetadata referenceMetadata,
            List<string> comparisonFields,
            ILogger? logger)
        {
            if (operand == null || referenceMetadata == null)
            {
                return false;
            }

            // Default to Genre and Tags if no comparison fields specified (backwards compatibility)
            if (comparisonFields == null || comparisonFields.Count == 0)
            {
                comparisonFields = DefaultSimilarityComparisonFields.ToList();
            }

            // Normalize comparison field names for case-insensitive matching (defensive coding)
            comparisonFields = comparisonFields
                .Select(f => f?.Trim())
                .Where(f => !string.IsNullOrEmpty(f))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // PERFORMANCE OPTIMIZATION: Use pre-computed frequency dictionaries from ReferenceMetadata
            // These were built once in BuildReferenceMetadata and are reused for all candidate items
            var genreFrequencies = referenceMetadata.GenreFreq;
            var tagFrequencies = referenceMetadata.TagFreq;
            var actorFrequencies = referenceMetadata.ActorFreq;
            var actorRoleFrequencies = referenceMetadata.ActorRoleFreq;
            var writerFrequencies = referenceMetadata.WriterFreq;
            var producerFrequencies = referenceMetadata.ProducerFreq;
            var directorFrequencies = referenceMetadata.DirectorFreq;
            var studioFrequencies = referenceMetadata.StudioFreq;
            var audioLangFrequencies = referenceMetadata.AudioLangFreq;

            float score = 0;
            var fieldMatches = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // Track matches per field (case-insensitive)

            // Process each comparison field
            foreach (var field in comparisonFields)
            {
                int fieldMatchCount = 0;

                // Normalize field name to lowercase for truly case-insensitive switch
                // CA1308 suppressed: Using lowercase for internal field name comparison, not security-sensitive
#pragma warning disable CA1308
                var fieldKey = (field ?? string.Empty).Trim().ToLowerInvariant();
#pragma warning restore CA1308

                switch (fieldKey)
                {
                    case "genre":
                        // Frequency-based matching for genres (O(1) dictionary lookup)
                        if (operand.Genres != null && genreFrequencies.Count > 0)
                        {
                            foreach (var genre in operand.Genres.Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                if (genreFrequencies.TryGetValue(genre, out int frequency))
                                {
                                    fieldMatchCount++;
                                    score += frequency;
                                }
                            }
                        }
                        break;

                    case "tags":
                        // Frequency-based matching for tags (O(1) dictionary lookup)
                        if (operand.Tags != null && tagFrequencies.Count > 0)
                        {
                            foreach (var tag in operand.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                if (tagFrequencies.TryGetValue(tag, out int frequency))
                                {
                                    fieldMatchCount++;
                                    score += frequency;
                                }
                            }
                        }
                        break;

                    case "actors":
                        // Frequency-based matching for actors (O(1) dictionary lookup)
                        if (operand.Actors != null && actorFrequencies.Count > 0)
                        {
                            foreach (var actor in operand.Actors.Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                if (actorFrequencies.TryGetValue(actor, out int frequency))
                                {
                                    fieldMatchCount++;
                                    score += frequency;
                                }
                            }
                        }
                        break;

                    case "actorroles":
                        // Frequency-based matching for actor roles/characters (O(1) dictionary lookup)
                        if (operand.ActorRoles != null && actorRoleFrequencies.Count > 0)
                        {
                            foreach (var role in operand.ActorRoles.Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                if (actorRoleFrequencies.TryGetValue(role, out int frequency))
                                {
                                    fieldMatchCount++;
                                    score += frequency;
                                }
                            }
                        }
                        break;

                    case "writers":
                        // Frequency-based matching for writers (O(1) dictionary lookup)
                        if (operand.Writers != null && writerFrequencies.Count > 0)
                        {
                            foreach (var writer in operand.Writers.Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                if (writerFrequencies.TryGetValue(writer, out int frequency))
                                {
                                    fieldMatchCount++;
                                    score += frequency;
                                }
                            }
                        }
                        break;

                    case "producers":
                        // Frequency-based matching for producers (O(1) dictionary lookup)
                        if (operand.Producers != null && producerFrequencies.Count > 0)
                        {
                            foreach (var producer in operand.Producers.Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                if (producerFrequencies.TryGetValue(producer, out int frequency))
                                {
                                    fieldMatchCount++;
                                    score += frequency;
                                }
                            }
                        }
                        break;

                    case "directors":
                        // Frequency-based matching for directors (O(1) dictionary lookup)
                        if (operand.Directors != null && directorFrequencies.Count > 0)
                        {
                            foreach (var director in operand.Directors.Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                if (directorFrequencies.TryGetValue(director, out int frequency))
                                {
                                    fieldMatchCount++;
                                    score += frequency;
                                }
                            }
                        }
                        break;

                    case "studios":
                        // Frequency-based matching for studios (O(1) dictionary lookup)
                        if (operand.Studios != null && studioFrequencies.Count > 0)
                        {
                            foreach (var studio in operand.Studios.Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                if (studioFrequencies.TryGetValue(studio, out int frequency))
                                {
                                    fieldMatchCount++;
                                    score += frequency;
                                }
                            }
                        }
                        break;

                    case "audio languages":
                        // Frequency-based matching for audio languages (O(1) dictionary lookup)
                        if (operand.AudioLanguages != null && audioLangFrequencies.Count > 0)
                        {
                            foreach (var lang in operand.AudioLanguages.Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                if (audioLangFrequencies.TryGetValue(lang, out int frequency))
                                {
                                    fieldMatchCount++;
                                    score += frequency;
                                }
                            }
                        }
                        break;

                    case "name":
                        // Partial similarity for names (frequency-based)
                        if (!string.IsNullOrWhiteSpace(operand.Name) && referenceMetadata.Names.Count > 0)
                        {
                            // Check for exact match
                            int exactFrequency = referenceMetadata.Names.Count(n => n.Equals(operand.Name, StringComparison.OrdinalIgnoreCase));
                            if (exactFrequency > 0)
                            {
                                fieldMatchCount++;
                                score += exactFrequency * 2; // Double weight for exact match,
                            }
                            else
                            {
                                // Check for partial match only if name is reasonably long (3+ chars) to avoid noise
                                var nameForPartial = operand.Name.Trim();
                                if (nameForPartial.Length >= 3)
                                {
                                    int partialMatches = referenceMetadata.Names
                                        .Count(n => n.Contains(nameForPartial, StringComparison.OrdinalIgnoreCase) ||
                                                   nameForPartial.Contains(n, StringComparison.OrdinalIgnoreCase));
                                    if (partialMatches > 0)
                                    {
                                        fieldMatchCount++;
                                        score += partialMatches; // Single weight for partial match,
                                    }
                                }
                            }
                        }
                        break;

                    case "production year":
                        // Within ±2 years range
                        if (operand.ProductionYear > 0 && referenceMetadata.ProductionYears.Count > 0)
                        {
                            var matchingYears = referenceMetadata.ProductionYears
                                .Where(y => Math.Abs(y - operand.ProductionYear) <= 2)
                                .Count();
                            if (matchingYears > 0)
                            {
                                fieldMatchCount++;
                                score += matchingYears;
                            }
                        }
                        break;

                    case "parental rating":
                        // Exact match for parental rating
                        if (!string.IsNullOrWhiteSpace(operand.OfficialRating) && referenceMetadata.ParentalRatings.Count > 0)
                        {
                            int frequency = referenceMetadata.ParentalRatings.Count(r => r.Equals(operand.OfficialRating, StringComparison.OrdinalIgnoreCase));
                            if (frequency > 0)
                            {
                                fieldMatchCount++;
                                score += frequency;
                            }
                        }
                        break;
                }

                // Record matches for this field (use lowercase key for consistency)
                if (fieldMatchCount > 0)
                {
                    fieldMatches[fieldKey] = fieldMatchCount;
                }
            }

            // Store score in operand for potential sorting
            operand.SimilarityScore = score;

            // Check if meets minimum threshold
            // - If only 1 field selected: require at least 1 match
            // - If 2+ fields selected: require at least 2 total matches
            // This scales appropriately with the number of comparison fields
            int totalUniqueMatches = fieldMatches.Values.Sum();
            int minRequiredMatches = comparisonFields.Count == 1 ? 1 : 2;
            bool passes = totalUniqueMatches >= minRequiredMatches;

            // Special handling for Genre field - if Genre is selected, require at least 1 genre match
            // This ensures thematic similarity (use lowercase key)
            bool hasGenreRequirement = comparisonFields.Any(f => f.Equals("Genre", StringComparison.OrdinalIgnoreCase));
            bool hasGenreMatch = fieldMatches.ContainsKey("genre") && fieldMatches["genre"] > 0;

            if (hasGenreRequirement && !hasGenreMatch)
            {
                passes = false; // Fail if Genre is selected but no genre matches,
            }

            if (passes)
            {
                var matchDetails = string.Join(", ", fieldMatches.Select(kvp => $"{kvp.Key}: {kvp.Value}"));
                logger?.LogDebug("Item '{Name}' passes similarity threshold with score {Score}. Matches: {Matches} (total: {Total})",
                    operand.Name, score, matchDetails, totalUniqueMatches);
            }
            else
            {
                var missingFields = comparisonFields.Except(fieldMatches.Keys, StringComparer.OrdinalIgnoreCase).ToList();
                if (hasGenreRequirement && !hasGenreMatch)
                {
                    logger?.LogDebug("Item '{Name}' fails similarity: no genre match (genre required). Total matches: {Total}",
                        operand.Name, totalUniqueMatches);
                }
                else
                {
                    logger?.LogDebug("Item '{Name}' fails similarity: only {Total} unique matches (need at least {Required}). Missing fields: {MissingFields}",
                        operand.Name, totalUniqueMatches, minRequiredMatches, string.Join(", ", missingFields));
                }
            }

            return passes;
        }

        /// <summary>
        /// Helper method to check if a name is in a semicolon-separated list (partial matching).
        /// </summary>
        private static bool IsNameInList(string name, string targetList)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(targetList))
                return false;

            var listItems = targetList.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item));

            return listItems.Any(item => name.Contains(item, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Helper method to check if a name matches a regex pattern.
        /// </summary>
        private static bool MatchesRegex(string name, string pattern, ILogger? logger)
        {
            try
            {
                var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.Compiled);
                return regex.IsMatch(name);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Invalid regex pattern '{Pattern}' in SimilarTo expression", pattern);
                return false;
            }
        }
    }
}