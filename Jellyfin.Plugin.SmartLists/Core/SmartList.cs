using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.Orders;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core
{
    public class SmartList
    {
        public string Id { get; set; } = null!;
        public string Name { get; set; } = null!;
        public string? FileName { get; set; }
        public Guid UserId { get; set; }
        public List<Order> Orders { get; set; }
        public List<string>? MediaTypes { get; set; }
        public List<ExpressionSet> ExpressionSets { get; set; }
        public int MaxItems { get; set; }
        public int MaxPlayTimeMinutes { get; set; }
        public List<string>? SimilarityComparisonFields { get; set; }

        // UserManager for resolving user-specific queries (Jellyfin 10.11+)
        public IUserManager UserManager { get; set; } = null!;

        // Similarity scores for sorting (populated during filtering when SimilarTo rules are active)
        private readonly ConcurrentDictionary<Guid, float> _similarityScores = new();

        // Item-to-group mappings for per-group limiting (populated during filtering when per-group MaxItems are set)
        private readonly ConcurrentDictionary<Guid, List<int>> _itemGroupMappings = new();

        // OPTIMIZATION: Static cache for compiled rules to avoid recompilation
        private static readonly ConcurrentDictionary<string, List<List<Func<Operand, bool>>>> _ruleCache = new();

        // Cache management constants and fields
        private const int MAX_CACHE_SIZE = 1000; // Maximum number of cached rule sets
        private const int CLEANUP_THRESHOLD = 800; // Clean up when cache exceeds this size
        private static readonly object _cacheCleanupLock = new();
        private static DateTime _lastCleanupTime = DateTime.MinValue;
        private static readonly TimeSpan MIN_CLEANUP_INTERVAL = TimeSpan.FromMinutes(5); // Minimum time between cleanups

        // Expensive fields that require database queries or complex extraction
        private static readonly HashSet<string> ExpensiveFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "AudioLanguages", "AudioBitrate", "AudioSampleRate", "AudioBitDepth", "AudioCodec", "AudioProfile", "AudioChannels",
            "Resolution", "Framerate", "VideoCodec", "VideoProfile", "VideoRange", "VideoRangeType",
            "People", "Actors", "Directors", "Composers", "Writers", "GuestStars", "Producers", "Conductors",
            "Lyricists", "Arrangers", "SoundEngineers", "Mixers", "Remixers", "Creators", "PersonArtists",
            "PersonAlbumArtists", "Authors", "Illustrators", "Pencilers", "Inkers", "Colorists", "Letterers",
            "CoverArtists", "Editors", "Translators",
            "Collections", "Playlists", "NextUnwatched", "SeriesName",
        };

        public SmartList(SmartPlaylistDto dto)
        {
            ArgumentNullException.ThrowIfNull(dto);

            Id = dto.Id ?? throw new ArgumentException("Playlist ID cannot be null", nameof(dto));
            Name = dto.Name;
            FileName = dto.FileName ?? $"{dto.Id}.json";
            // DEPRECATED: dto.UserId is for backwards compatibility with old single-user playlists.
            // It is planned to be removed in version 10.12. Use UserPlaylists array instead.
            UserId = Guid.TryParse(dto.UserId, out var userId) ? userId : Guid.Empty;

            // Initialize properties before calling InitializeFromDto
            Orders = [];
            ExpressionSets = [];

            InitializeFromDto(dto);
        }

        public SmartList(SmartCollectionDto dto)
        {
            ArgumentNullException.ThrowIfNull(dto);

            Id = dto.Id ?? throw new ArgumentException("Collection ID cannot be null", nameof(dto));
            Name = dto.Name;
            FileName = dto.FileName ?? $"{dto.Id}.json";
            // DEPRECATED: dto.UserId is for backwards compatibility with old single-user playlists.
            // It is planned to be removed in version 10.12. Use UserPlaylists array instead.
            // Note: Collections still use UserId for owner context (IsPlayed, IsFavorite, etc.)
            UserId = Guid.TryParse(dto.UserId, out var userId) ? userId : Guid.Empty; // Owner user for rule context (IsPlayed, IsFavorite, etc.)

            // Initialize properties before calling InitializeFromDto
            Orders = [];
            ExpressionSets = [];

            InitializeFromDto(dto);
        }

        private void InitializeFromDto(SmartListDto dto)
        {

            // Handle both legacy single Order and new multiple Orders formats
            if (dto.Order?.SortOptions != null && dto.Order.SortOptions.Count > 0)
            {
                // New format: multiple sort options
                Orders = dto.Order.SortOptions
                    .Select(so =>
                    {
                        // Special handling for Random and NoOrder which don't have Ascending/Descending variants
                        if (so.SortBy == "Random" || so.SortBy == "NoOrder")
                        {
                            return OrderFactory.CreateOrder(so.SortBy);
                        }
                        // For all other sorts, append the sort order
                        return OrderFactory.CreateOrder($"{so.SortBy} {so.SortOrder.ToString()}");
                    })
                    .Where(o => o != null)
                    .ToList();
            }
            else if (!string.IsNullOrEmpty(dto.Order?.Name))
            {
                // Legacy format: single order by name
                Orders = [OrderFactory.CreateOrder(dto.Order.Name)];
            }
            else
            {
                // No order specified, use NoOrder
                Orders = [new NoOrder()];
            }

            MediaTypes = dto.MediaTypes != null ? new List<string>(dto.MediaTypes) : null; // Create defensive copy to prevent corruption
            MaxItems = dto.MaxItems ?? 0; // Default to 0 (unlimited) for backwards compatibility
            MaxPlayTimeMinutes = dto.MaxPlayTimeMinutes ?? 0; // Default to 0 (unlimited) for backwards compatibility
            SimilarityComparisonFields = dto.SimilarityComparisonFields != null ? new List<string>(dto.SimilarityComparisonFields) : null; // Create defensive copy

            if (dto.ExpressionSets != null && dto.ExpressionSets.Count > 0)
            {
                ExpressionSets = Engine.FixRuleSets(dto.ExpressionSets);
            }
            else
            {
                ExpressionSets = [];
            }
        }

        private List<List<Func<Operand, bool>>> CompileRuleSets(string? defaultUserId = null, ILogger? logger = null)
        {
            try
            {
                // Check if cache cleanup is needed (with rate limiting)
                CheckAndCleanupCache(logger);

                // Input validation
                if (ExpressionSets == null || ExpressionSets.Count == 0)
                {
                    logger?.LogDebug("No expression sets to compile for playlist '{PlaylistName}'", Name);
                    return [];
                }

                // Use provided defaultUserId or fall back to SmartList.UserId for backwards compatibility
                // DEPRECATED: SmartList.UserId fallback is for backwards compatibility with old single-user playlists.
                // It is planned to be removed in version 10.12. Use UserPlaylists array instead.
                // Normalize to "N" format (no dashes) to match UserPlaylists format
                var effectiveDefaultUserId = defaultUserId ?? (UserId != Guid.Empty ? UserId.ToString("N") : null);

                // OPTIMIZATION: Generate a cache key based on the rule set content and defaultUserId
                var ruleSetHash = GenerateRuleSetHash(effectiveDefaultUserId);

                return _ruleCache.GetOrAdd(ruleSetHash, _ =>
                {
                    try
                    {
                        logger?.LogDebug("Compiling rules for playlist {PlaylistName} (cache miss)", Name);

                        var compiledRuleSets = new List<List<Func<Operand, bool>>>();

                        for (int setIndex = 0; setIndex < ExpressionSets.Count; setIndex++)
                        {
                            var set = ExpressionSets[setIndex];
                            if (set?.Expressions == null)
                            {
                                logger?.LogDebug("Skipping null expression set at index {SetIndex} for playlist '{PlaylistName}'", setIndex, Name);
                                compiledRuleSets.Add([]);
                                continue;
                            }

                            var compiledRules = new List<Func<Operand, bool>>();

                            for (int exprIndex = 0; exprIndex < set.Expressions.Count; exprIndex++)
                            {
                                var expr = set.Expressions[exprIndex];
                                if (expr == null)
                                {
                                    logger?.LogDebug("Skipping null expression at set {SetIndex}, index {ExprIndex} for playlist '{PlaylistName}'", setIndex, exprIndex, Name);
                                    continue;
                                }

                                // Skip SimilarTo expressions - they're handled separately during filtering
                                if (expr.MemberName == "SimilarTo")
                                {
                                    logger?.LogDebug("Skipping SimilarTo expression at set {SetIndex}, index {ExprIndex} for playlist '{PlaylistName}' - handled separately", setIndex, exprIndex, Name);
                                    continue;
                                }

                                // Skip Collections expressions with IncludeCollectionOnly=true - they're handled separately
                                if (expr.MemberName == "Collections" && expr.IncludeCollectionOnly == true)
                                {
                                    logger?.LogDebug("Skipping Collections expression with IncludeCollectionOnly=true at set {SetIndex}, index {ExprIndex} for playlist '{PlaylistName}' - handled separately", setIndex, exprIndex, Name);
                                    continue;
                                }

                                // Skip Playlists expressions with IncludePlaylistOnly=true - they're handled separately
                                if (expr.MemberName == "Playlists" && expr.IncludePlaylistOnly == true)
                                {
                                    logger?.LogDebug("Skipping Playlists expression with IncludePlaylistOnly=true at set {SetIndex}, index {ExprIndex} for playlist '{PlaylistName}' - handled separately", setIndex, exprIndex, Name);
                                    continue;
                                }

                                try
                                {
                                    // Use effectiveDefaultUserId (passed parameter or SmartList.UserId fallback)
                                    if (string.IsNullOrEmpty(effectiveDefaultUserId))
                                    {
                                        logger?.LogError("SmartList '{PlaylistName}' has no valid default user ID. Cannot compile rules.", Name);
                                        continue; // Skip this rule set,
                                    }

                                    var compiledRule = Engine.CompileRule<Operand>(expr, effectiveDefaultUserId, logger);
                                    if (compiledRule != null)
                                    {
                                        compiledRules.Add(compiledRule);
                                    }
                                    else
                                    {
                                        logger?.LogWarning("Failed to compile rule at set {SetIndex}, index {ExprIndex} for playlist '{PlaylistName}': {Field} {Operator} {Value}",
                                            setIndex, exprIndex, Name, expr.MemberName, expr.Operator, expr.TargetValue);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logger?.LogError(ex, "Error compiling rule at set {SetIndex}, index {ExprIndex} for playlist '{PlaylistName}': {Field} {Operator} {Value}",
                                        setIndex, exprIndex, Name, expr.MemberName, expr.Operator, expr.TargetValue);
                                    // Skip this rule and continue with others
                                }
                            }

                            compiledRuleSets.Add(compiledRules);
                            logger?.LogDebug("Compiled {RuleCount} rules for expression set {SetIndex} in playlist '{PlaylistName}'",
                                compiledRules.Count, setIndex, Name);
                        }

                        logger?.LogDebug("Successfully compiled {SetCount} rule sets for playlist '{PlaylistName}'",
                            compiledRuleSets.Count, Name);

                        return compiledRuleSets;
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError(ex, "Critical error during rule compilation for playlist '{PlaylistName}'. Returning empty rule set.", Name);
                        return [];
                    }
                });
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Critical error in CompileRuleSets for playlist '{PlaylistName}'. Returning empty rule set.", Name);
                return [];
            }
        }

        /// <summary>
        /// Checks cache size and performs cleanup if needed, with rate limiting to prevent excessive cleanup operations.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        private static void CheckAndCleanupCache(ILogger? logger = null)
        {
            var currentCacheSize = _ruleCache.Count;

            // Only check for cleanup if we're approaching the threshold
            if (currentCacheSize <= CLEANUP_THRESHOLD)
                return;

            var now = DateTime.UtcNow;

            // Rate limit cleanup operations to prevent excessive cleanup
            if (now - _lastCleanupTime < MIN_CLEANUP_INTERVAL)
                return;

            // Use lock to ensure only one thread performs cleanup at a time
            lock (_cacheCleanupLock)
            {
                // Double-check conditions after acquiring lock
                if (_ruleCache.Count <= CLEANUP_THRESHOLD || now - _lastCleanupTime < MIN_CLEANUP_INTERVAL)
                    return;

                logger?.LogDebug("Rule cache size ({CurrentSize}) exceeded threshold ({Threshold}). Performing cleanup.",
                    _ruleCache.Count, CLEANUP_THRESHOLD);

                // Simple cleanup strategy: remove half the cache when it gets too large
                // This is more efficient than LRU for this use case since rule compilation is expensive
                var keysToRemove = _ruleCache.Keys.Take(_ruleCache.Count / 2).ToList();

                int removedCount = 0;
                foreach (var key in keysToRemove)
                {
                    if (_ruleCache.TryRemove(key, out _))
                    {
                        removedCount++;
                    }
                }

                _lastCleanupTime = now;

                logger?.LogDebug("Rule cache cleanup completed. Removed {RemovedCount} entries. Cache size: {CurrentSize}/{MaxSize}",
                    removedCount, _ruleCache.Count, MAX_CACHE_SIZE);
            }
        }

        /// <summary>
        /// Manually clears the entire rule cache. Useful for troubleshooting or memory management.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public static void ClearRuleCache(ILogger? logger = null)
        {
            lock (_cacheCleanupLock)
            {
                var previousCount = _ruleCache.Count;
                _ruleCache.Clear();
                _lastCleanupTime = DateTime.UtcNow;

                logger?.LogDebug("Rule cache manually cleared. Removed {RemovedCount} entries.", previousCount);
            }
        }

        /// <summary>
        /// Gets current cache statistics for monitoring and debugging.
        /// </summary>
        /// <returns>A tuple containing current cache size, maximum size, and cleanup threshold.</returns>
        public static (int CurrentSize, int MaxSize, int CleanupThreshold, DateTime LastCleanup) GetCacheStats()
        {
            return (_ruleCache.Count, MAX_CACHE_SIZE, CLEANUP_THRESHOLD, _lastCleanupTime);
        }

        private string GenerateRuleSetHash(string? defaultUserId = null)
        {
            try
            {
                // Input validation
                if (ExpressionSets == null)
                {
                    return $"id:{Id ?? ""}|sets:0|defaultUser:{defaultUserId ?? ""}";
                }

                // Use StringBuilder for efficient string concatenation
                var hashBuilder = new System.Text.StringBuilder();
                hashBuilder.Append(Id ?? "");
                hashBuilder.Append('|');
                hashBuilder.Append(ExpressionSets.Count);
                hashBuilder.Append('|');
                hashBuilder.Append("defaultUser:");
                hashBuilder.Append(defaultUserId ?? "");

                for (int i = 0; i < ExpressionSets.Count; i++)
                {
                    var set = ExpressionSets[i];

                    hashBuilder.Append("|set");
                    hashBuilder.Append(i);
                    hashBuilder.Append(':');

                    // Handle null expression sets
                    if (set?.Expressions == null)
                    {
                        hashBuilder.Append("null");
                        continue;
                    }

                    hashBuilder.Append(set.Expressions.Count);

                    for (int j = 0; j < set.Expressions.Count; j++)
                    {
                        var expr = set.Expressions[j];

                        hashBuilder.Append("|expr");
                        hashBuilder.Append(i);
                        hashBuilder.Append('_');
                        hashBuilder.Append(j);
                        hashBuilder.Append(':');

                        // Handle null expressions
                        if (expr == null)
                        {
                            hashBuilder.Append("null");
                            continue;
                        }

                        // Handle null expression properties and append efficiently
                        hashBuilder.Append(expr.MemberName ?? "");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.Operator ?? "");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.TargetValue ?? "");

                        // Include option fields that affect rule compilation
                        // These must be part of the hash to ensure cache invalidation when toggled
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.UserId ?? "");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.IncludeParentSeriesTags?.ToString() ?? "null");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.IncludeParentSeriesStudios?.ToString() ?? "null");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.IncludeParentSeriesGenres?.ToString() ?? "null");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.IncludeCollectionOnly?.ToString() ?? "null");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.IncludePlaylistOnly?.ToString() ?? "null");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.OnlyDefaultAudioLanguage?.ToString() ?? "null");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.IncludeUnwatchedSeries?.ToString() ?? "null");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.IncludeEpisodesWithinSeries?.ToString() ?? "null");
                    }
                }

                return hashBuilder.ToString();
            }
            catch (Exception)
            {
                // If hash generation fails, return a fallback hash based on basic properties
                return $"fallback:{Id ?? ""}:{ExpressionSets?.Count ?? 0}:defaultUser:{defaultUserId ?? ""}";
            }
        }

        /// <summary>
        /// Returns all rule group indices that match the given operand.
        /// Used for per-group limiting - allows tracking which groups each item matches.
        /// </summary>
        private List<int> GetMatchingGroupIndices(List<List<Func<Operand, bool>>> compiledRules, Operand operand)
        {
            var matchingGroups = new List<int>();

            try
            {
                if (compiledRules == null || operand == null)
                {
                    return matchingGroups;
                }

                // Each ExpressionSet is a logic group
                // Rules within each group always use AND logic
                for (int groupIndex = 0; groupIndex < ExpressionSets.Count && groupIndex < compiledRules.Count; groupIndex++)
                {
                    var group = ExpressionSets[groupIndex];
                    var groupRules = compiledRules[groupIndex];

                    if (group == null)
                        continue; // Skip null groups

                    // Check if this group has only skipped rules (SimilarTo or IncludeCollectionOnly Collections or IncludePlaylistOnly Playlists)
                    // If so, skip it for item evaluation (these are handled separately)
                    bool hasOnlySkippedRules = group.Expressions != null && 
                        group.Expressions.All(expr => 
                            expr?.MemberName == "SimilarTo" || 
                            (expr?.MemberName == "Collections" && expr.IncludeCollectionOnly == true) ||
                            (expr?.MemberName == "Playlists" && expr.IncludePlaylistOnly == true));

                    if (hasOnlySkippedRules)
                    {
                        continue; // Skip groups with only skipped rules - they don't match items
                    }

                    if (groupRules == null || groupRules.Count == 0)
                        continue; // Skip empty rule groups

                    try
                    {
                        bool groupMatches = groupRules.All(rule =>
                        {
                            try
                            {
                                return rule?.Invoke(operand) ?? false;
                            }
                            catch (Exception)
                            {
                                // Log at debug level to avoid spam, but continue evaluation
                                // Conservative approach: assume rule doesn't match if it fails
                                return false;
                            }
                        });

                        if (groupMatches)
                        {
                            matchingGroups.Add(groupIndex);
                        }
                    }
                    catch (Exception)
                    {
                        // If we can't evaluate this group, skip it and continue with others
                        continue;
                    }
                }
            }
            catch (Exception)
            {
                // If we can't evaluate any groups, return empty list
                return matchingGroups;
            }

            return matchingGroups;
        }

        private bool EvaluateLogicGroups(List<List<Func<Operand, bool>>> compiledRules, Operand operand)
        {
            // For backward compatibility and simple OR logic, check if any group matches
            var matchingGroups = GetMatchingGroupIndices(compiledRules, operand);
            return matchingGroups.Count > 0;
        }

        private bool EvaluateLogicGroupsForEpisode(List<List<Func<Operand, bool>>> compiledRules, Operand operand, Series? parentSeries, ILogger? logger)
        {
            try
            {
                if (compiledRules == null || operand == null)
                {
                    return false;
                }

                // If we have a parent series, it means this episode is being expanded from a series that matched Collections rules
                // In this case, we should skip Collections rule evaluation for episodes since they inherit from their parent
                bool isFromSeriesExpansion = parentSeries != null;

                // Each ExpressionSet is a logic group
                // Groups are combined with OR logic (any group can match)
                // Rules within each group always use AND logic
                for (int groupIndex = 0; groupIndex < ExpressionSets.Count && groupIndex < compiledRules.Count; groupIndex++)
                {
                    var group = ExpressionSets[groupIndex];
                    var groupRules = compiledRules[groupIndex];

                    if (group == null || groupRules == null || groupRules.Count == 0 || group.Expressions == null)
                        continue; // Skip empty or null groups

                    try
                    {
                        bool groupMatches = true; // Start with true for AND logic within groups

                        // Check each expression in the group
                        // Use separate index for compiled rules since SimilarTo expressions are not compiled
                        int compiledIndex = 0;
                        for (int exprIndex = 0; exprIndex < group.Expressions.Count; exprIndex++)
                        {
                            var expression = group.Expressions[exprIndex];
                            if (expression == null) continue;

                            // SimilarTo is not compiled, skip it (handled separately in similarity calculation)
                            if (expression.MemberName == "SimilarTo")
                            {
                                logger?.LogDebug("Skipping SimilarTo rule in episode evaluation (not compiled)");
                                continue;
                            }

                            // Skip Collections rules with IncludeCollectionOnly=true - handled separately
                            if (expression.MemberName == "Collections" && expression.IncludeCollectionOnly == true)
                            {
                                logger?.LogDebug("Skipping Collections rule with IncludeCollectionOnly=true in episode evaluation (handled separately)");
                                compiledIndex++; // Still advance the compiled index
                                continue;
                            }

                            // Skip Playlists rules with IncludePlaylistOnly=true - handled separately
                            if (expression.MemberName == "Playlists" && expression.IncludePlaylistOnly == true)
                            {
                                logger?.LogDebug("Skipping Playlists rule with IncludePlaylistOnly=true in episode evaluation (handled separately)");
                                compiledIndex++; // Still advance the compiled index
                                continue;
                            }

                            // Skip Collections rules when expanding from parent series since episodes inherit collection membership
                            if (isFromSeriesExpansion && expression.MemberName == "Collections")
                            {
                                logger?.LogDebug("Skipping Collections rule for episode - inherited from parent series '{SeriesName}'", parentSeries?.Name ?? "unknown");
                                compiledIndex++; // Still advance the compiled index
                                continue; // Skip this rule, don't evaluate it,
                            }

                            if (compiledIndex >= groupRules.Count)
                            {
                                logger?.LogDebug("No more compiled rules available at expression {ExprIndex}", exprIndex);
                                break;
                            }

                            var rule = groupRules[compiledIndex++];

                            // Evaluate the rule normally
                            try
                            {
                                if (rule?.Invoke(operand) != true)
                                {
                                    groupMatches = false; // This group fails due to AND logic
                                    break; // No need to check remaining rules in this group,
                                }
                            }
                            catch (Exception)
                            {
                                // Conservative approach: assume rule doesn't match if it fails
                                groupMatches = false;
                                break;
                            }
                        }

                        if (groupMatches)
                        {
                            return true; // This group matches, so the item matches overall,
                        }
                    }
                    catch (Exception)
                    {
                        // If we can't evaluate this group, skip it and continue with others
                        continue;
                    }
                }

                return false; // No groups matched,
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Error evaluating rules for episode, assuming no match");
                return false; // Return false (no match) on any unexpected errors,
            }
        }

        // Returns the ID's of the items, if order is provided the IDs are sorted.
        public IEnumerable<Guid> FilterPlaylistItems(IEnumerable<BaseItem> items, ILibraryManager libraryManager,
            User user, RefreshQueueService.RefreshCache refreshCache, IUserDataManager? userDataManager = null, ILogger? logger = null, Action<int, int>? progressCallback = null)
        {
            var stopwatch = Stopwatch.StartNew();

            // Clear similarity scores from any previous runs
            _similarityScores.Clear();
            
            // Clear item-group mappings from any previous runs
            _itemGroupMappings.Clear();

            try
            {
                // Input validation
                if (items == null)
                {
                    logger?.LogWarning("FilterPlaylistItems called with null items collection for playlist '{PlaylistName}'", Name);
                    return [];
                }

                if (libraryManager == null)
                {
                    logger?.LogError("FilterPlaylistItems called with null libraryManager for playlist '{PlaylistName}'", Name);
                    return [];
                }

                if (user == null)
                {
                    logger?.LogError("FilterPlaylistItems called with null user for playlist '{PlaylistName}'", Name);
                    return [];
                }

                // Materialize items once to avoid double enumeration (Count + ToArray later)
                var itemsArray = items as BaseItem[] ?? items.ToArray();
                var itemCount = itemsArray.Length;

                logger?.LogDebug("FilterPlaylistItems called with {ItemCount} items, ExpressionSets={ExpressionSetCount}, MediaTypes={MediaTypes}",
                    itemCount, ExpressionSets?.Count ?? 0, MediaTypes != null ? string.Join(",", MediaTypes) : "None");

                // Early return for empty item collections
                if (itemCount == 0)
                {
                    logger?.LogDebug("No items to filter for playlist '{PlaylistName}'", Name);
                    return [];
                }

                // Media type filtering is now handled at the API level in PlaylistService.GetAllUserMedia()
                // This provides significant performance improvements by filtering at the database level
                logger?.LogDebug("Processing {ItemCount} items (already filtered by media type at API level)", itemCount);

                var results = new List<BaseItem>();

                // Check if any rules use expensive fields to avoid unnecessary extraction
                var needsAudioLanguages = false;
                var needsAudioQuality = false;
                var needsVideoQuality = false;
                var needsPeople = false;
                var needsCollections = false;
                var needsPlaylists = false;
                var needsNextUnwatched = false;
                var needsSeriesName = false;
                var needsParentSeriesTags = false;
                var needsParentSeriesStudios = false;
                var needsParentSeriesGenres = false;
                var needsSimilarTo = false;
                var includeUnwatchedSeries = true; // Default to true for backwards compatibility
                var similarToExpressions = new List<Expression>();
                var additionalUserIds = new List<string>();
                var similarityComparisonFields = (SimilarityComparisonFields == null || SimilarityComparisonFields.Count == 0) 
                    ? OperandFactory.DefaultSimilarityComparisonFields.ToList() 
                    : SimilarityComparisonFields; // Default to Genre and Tags for backwards compatibility

                try
                {
                    if (ExpressionSets != null)
                    {
                        var fieldReqs = FieldRequirements.Analyze(ExpressionSets, Orders);

                        needsAudioLanguages = fieldReqs.NeedsAudioLanguages;
                        needsAudioQuality = fieldReqs.NeedsAudioQuality;
                        needsVideoQuality = fieldReqs.NeedsVideoQuality;
                        needsPeople = fieldReqs.NeedsPeople;
                        needsCollections = fieldReqs.NeedsCollections;
                        needsPlaylists = fieldReqs.NeedsPlaylists;
                        needsNextUnwatched = fieldReqs.NeedsNextUnwatched;
                        needsSeriesName = fieldReqs.NeedsSeriesName;
                        needsParentSeriesTags = fieldReqs.NeedsParentSeriesTags;
                        needsParentSeriesStudios = fieldReqs.NeedsParentSeriesStudios;
                        needsParentSeriesGenres = fieldReqs.NeedsParentSeriesGenres;
                        needsSimilarTo = fieldReqs.NeedsSimilarTo;
                        similarToExpressions = fieldReqs.SimilarToExpressions;

                        // Extract IncludeUnwatchedSeries parameter from NextUnwatched rules
                        // If any rule explicitly sets it to false, use false; otherwise default to true
                        var nextUnwatchedRules = ExpressionSets
                            .SelectMany(set => set?.Expressions ?? [])
                            .Where(expr => expr?.MemberName == "NextUnwatched")
                            .ToList();

                        includeUnwatchedSeries = !nextUnwatchedRules.Any(rule => rule.IncludeUnwatchedSeries == false);

                        // Collect unique user IDs from user-specific expressions
                        // Normalize to "N" format (no dashes) to match UserPlaylists format
                        additionalUserIds = [..ExpressionSets
                            .SelectMany(set => set?.Expressions ?? [])
                            .Where(expr => expr?.IsUserSpecific == true && !string.IsNullOrEmpty(expr.UserId))
                            .Select(expr => Guid.TryParse(expr.UserId, out var guid) ? guid.ToString("N") : expr.UserId!)
                            .Distinct()];

                        if (additionalUserIds.Count > 0)
                        {
                            logger?.LogDebug("Found user-specific expressions for {Count} users: [{UserIds}]",
                                additionalUserIds.Count, string.Join(", ", additionalUserIds));
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Error analyzing expression sets for expensive fields in playlist '{PlaylistName}'. Assuming no expensive fields needed.", Name);
                }

                // CRITICAL: Enable expensive extraction when SimilarTo requires it (people/audio languages)
                // Without this, similarity matching on people/audio language fields will fail
                try
                {
                    if (needsSimilarTo && similarityComparisonFields is { Count: > 0 })
                    {
                        var simFields = new HashSet<string>(similarityComparisonFields, StringComparer.OrdinalIgnoreCase);
                        if (simFields.Any(f => FieldDefinitions.IsPeopleField(f)))
                        {
                            needsPeople = true;
                            logger?.LogDebug("Enabled People extraction for SimilarTo comparison fields");
                        }
                        if (simFields.Contains("Audio Languages"))
                        {
                            needsAudioLanguages = true;
                            logger?.LogDebug("Enabled Audio Languages extraction for SimilarTo comparison fields");
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Error merging SimilarTo comparison fields into expensive-field requirements");
                }

                // Check if any Collections rule has IncludeCollectionOnly = true
                var hasCollectionsIncludeCollectionOnly = ExpressionSets?.Any(set =>
                    set.Expressions?.Any(expr =>
                        expr.MemberName == "Collections" && expr.IncludeCollectionOnly == true) == true) == true;

                // If IncludeCollectionOnly is enabled, fetch matching collections directly
                if (hasCollectionsIncludeCollectionOnly)
                {
                    logger?.LogDebug("IncludeCollectionOnly is enabled - fetching matching collections directly");
                    var matchingCollections = GetMatchingCollections(libraryManager, user, logger);
                    if (matchingCollections.Count > 0)
                    {
                        logger?.LogDebug("Found {Count} matching collections to include directly", matchingCollections.Count);
                        results.AddRange(matchingCollections);
                    }
                    else
                    {
                        logger?.LogDebug("No matching collections found for IncludeCollectionOnly mode");
                    }
                }

                // Check if any Playlists rule has IncludePlaylistOnly = true
                var hasPlaylistsIncludePlaylistOnly = ExpressionSets?.Any(set =>
                    set.Expressions?.Any(expr =>
                        expr.MemberName == "Playlists" && expr.IncludePlaylistOnly == true) == true) == true;

                // If IncludePlaylistOnly is enabled, fetch matching playlists directly
                if (hasPlaylistsIncludePlaylistOnly)
                {
                    logger?.LogDebug("IncludePlaylistOnly is enabled - fetching matching playlists directly");
                    var matchingPlaylists = GetMatchingPlaylists(libraryManager, user, logger);
                    if (matchingPlaylists.Count > 0)
                    {
                        logger?.LogDebug("Found {Count} matching playlists to include directly", matchingPlaylists.Count);
                        results.AddRange(matchingPlaylists);
                    }
                    else
                    {
                        logger?.LogDebug("No matching playlists found for IncludePlaylistOnly mode");
                    }
                }

                // Check if ALL rules have IncludeCollectionOnly or IncludePlaylistOnly enabled
                // If so, skip media item processing entirely (only return collections/playlists)
                var allRulesAreIncludeOnly = ExpressionSets?.All(set =>
                    set?.Expressions?.All(expr =>
                        (expr.MemberName == "Collections" && expr.IncludeCollectionOnly == true) ||
                        (expr.MemberName == "Playlists" && expr.IncludePlaylistOnly == true)) == true) == true;

                if (allRulesAreIncludeOnly && (hasCollectionsIncludeCollectionOnly || hasPlaylistsIncludePlaylistOnly))
                {
                    logger?.LogDebug("All rules are IncludeCollectionOnly or IncludePlaylistOnly - skipping media item processing, returning {Count} items", results.Count);
                    return results.Select(item => item.Id);
                }

                // Early validation of additional users to prevent exceptions during item processing
                if (additionalUserIds.Count > 0 && userDataManager != null)
                {
                    foreach (var userId in additionalUserIds)
                    {
                        if (Guid.TryParse(userId, out var userGuid))
                        {
                            var targetUser = OperandFactory.GetUserById(UserManager, userGuid);
                            if (targetUser == null)
                            {
                                logger?.LogWarning("User with ID '{UserId}' not found for playlist '{PlaylistName}'. This playlist rule references a user that no longer exists. Skipping playlist processing.", userId, Name);
                                return []; // Return empty results to avoid exception spam,
                            }
                        }
                        else
                        {
                            logger?.LogWarning("Invalid user ID format '{UserId}' for playlist '{PlaylistName}'. Skipping playlist processing.", userId, Name);
                            return []; // Return empty results,
                        }
                    }
                }

                // Compile rules with error handling
                // Use the user parameter's ID as the default for user-specific fields without explicit UserId
                // Normalize to "N" format (no dashes) to match UserPlaylists format
                var defaultUserId = user.Id.ToString("N");
                List<List<Func<Operand, bool>>>? compiledRules = null;
                try
                {
                    compiledRules = CompileRuleSets(defaultUserId, logger);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Failed to compile rules for playlist '{PlaylistName}'. Playlist will return no results.", Name);
                    return [];
                }

                if (compiledRules == null)
                {
                    logger?.LogError("Compiled rules is null for playlist '{PlaylistName}'. Playlist will return no results.", Name);
                    return [];
                }

                // Check if there are any rules to evaluate (including skipped ones like SimilarTo and IncludeCollectionOnly and IncludePlaylistOnly)
                // This prevents "no rules = match everything" when all rules are skipped
                bool hasAnyRules = compiledRules.Any(set => set?.Count > 0) ||
                    ExpressionSets?.Any(set => set?.Expressions?.Any(expr => 
                        expr?.MemberName == "SimilarTo" || 
                        (expr?.MemberName == "Collections" && expr.IncludeCollectionOnly == true) ||
                        (expr?.MemberName == "Playlists" && expr.IncludePlaylistOnly == true)) == true) == true;

                // Check if there are any non-expensive rules for two-phase filtering optimization
                bool hasNonExpensiveRules = false;
                try
                {
                    if (ExpressionSets != null)
                    {
                        hasNonExpensiveRules = ExpressionSets
                            .SelectMany(set => set?.Expressions ?? [])
                            .Any(expr => expr != null
                                && !ExpensiveFields.Contains(expr.MemberName)
                                && !(expr.MemberName == "Tags" && expr.IncludeParentSeriesTags == true)
                                && !(expr.MemberName == "Studios" && expr.IncludeParentSeriesStudios == true)
                                && !(expr.MemberName == "Genres" && expr.IncludeParentSeriesGenres == true));
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Error analyzing non-expensive rules in playlist '{PlaylistName}'. Assuming non-expensive rules exist.", Name);
                    hasNonExpensiveRules = true; // Conservative assumption,
                }

                // Build reference metadata once for SimilarTo queries (before chunking to avoid rebuilding per chunk)
                OperandFactory.ReferenceMetadata? referenceMetadata = null;
                if (needsSimilarTo)
                {
                    logger?.LogDebug("Building reference metadata for SimilarTo queries (once per filter run) using fields: {Fields}",
                        string.Join(", ", similarityComparisonFields));
                    referenceMetadata = OperandFactory.BuildReferenceMetadata(similarToExpressions, itemsArray, similarityComparisonFields, libraryManager, logger);
                }

                // RefreshCache is provided as parameter - shared across multiple playlists/collections for the same user

                // OPTIMIZATION: Process items in batches for large libraries to prevent memory issues
                // Get batch size from configuration, default to 300 if 0 or invalid
                var config = Plugin.Instance?.Configuration;
                var batchSize = config?.ProcessingBatchSize ?? 300;
                if (batchSize <= 0)
                {
                    batchSize = 300; // Default to 300 if invalid
                }
                var chunkSize = batchSize;
                
                // itemsArray already materialized above to avoid double enumeration
                var totalItems = itemsArray.Length;

                // Report initial progress
                progressCallback?.Invoke(0, totalItems);

                if (totalItems > chunkSize)
                {
                    logger?.LogDebug("Processing large library ({TotalItems} items) in chunks of {ChunkSize}", totalItems, chunkSize);
                }

                for (int chunkStart = 0; chunkStart < totalItems; chunkStart += chunkSize)
                {
                    try
                    {
                        var chunkEnd = Math.Min(chunkStart + chunkSize, totalItems);
                        var chunk = itemsArray.Skip(chunkStart).Take(chunkEnd - chunkStart);

                        if (totalItems > chunkSize)
                        {
                            logger?.LogDebug("Processing chunk {ChunkNumber}/{TotalChunks} (items {Start}-{End})",
                                (chunkStart / chunkSize) + 1, (totalItems + chunkSize - 1) / chunkSize, chunkStart + 1, chunkEnd);
                        }

                        // Report progress at the start of each chunk (before processing)
                        progressCallback?.Invoke(chunkStart, totalItems);

                        // Process chunk
                        var chunkResults = ProcessItemChunk(chunk, libraryManager, user, userDataManager, logger,
                            needsAudioLanguages, needsAudioQuality, needsVideoQuality, needsPeople, needsCollections, needsPlaylists, needsNextUnwatched, needsSeriesName, needsParentSeriesTags, needsParentSeriesStudios, needsParentSeriesGenres, needsSimilarTo, includeUnwatchedSeries, additionalUserIds, referenceMetadata, similarityComparisonFields, compiledRules, hasAnyRules, hasNonExpensiveRules, refreshCache);
                        results.AddRange(chunkResults);
                        
                        // Report progress after chunk is complete
                        progressCallback?.Invoke(chunkEnd, totalItems);

                        // OPTIMIZATION: Allow other operations to run between chunks for large libraries
                        if (totalItems > chunkSize * 2)
                        {
                            // Yield control briefly to prevent blocking
                            System.Threading.Thread.Sleep(1);
                        }
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("User with ID") && ex.Message.Contains("not found"))
                    {
                        logger?.LogWarning(ex, "Playlist '{PlaylistName}' references a user that no longer exists. Stopping playlist processing.", Name);
                        return [];
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError(ex, "Error processing chunk {ChunkStart}-{ChunkEnd} for playlist '{PlaylistName}'. Skipping this chunk.",
                            chunkStart, Math.Min(chunkStart + chunkSize, totalItems), Name);
                        // Continue with next chunk
                    }
                }

                stopwatch.Stop();
                logger?.LogDebug("Playlist filtering for '{PlaylistName}' completed in {ElapsedTime}ms: {InputCount} items → {OutputCount} items",
                    Name, stopwatch.ElapsedMilliseconds, totalItems, results.Count);

                // Check if we need to expand Collections based on media type selection
                var expandedResults = ExpandCollectionsBasedOnMediaType(results, libraryManager, user, userDataManager, logger, refreshCache);
                logger?.LogDebug("Playlist '{PlaylistName}' expanded from {OriginalCount} items to {ExpandedCount} items after Collections processing",
                    Name, results.Count, expandedResults.Count);

                // Apply per-group limits if configured (before sorting and global limits)
                if (HasPerGroupLimits())
                {
                    expandedResults = ApplyPerGroupLimits(expandedResults, user, userDataManager, logger, refreshCache);
                    logger?.LogDebug("Playlist '{PlaylistName}' limited to {Count} items after per-group MaxItems applied", Name, expandedResults.Count);
                }

                // Apply ordering and limits with error handling
                try
                {
                    // If using Similarity order, set the scores before sorting
                    // If using RuleBlock order, set the group mappings before sorting
                    foreach (var order in Orders)
                    {
                        if (order is SimilarityOrder similarityOrder)
                        {
                            similarityOrder.Scores = _similarityScores;
                        }
                        else if (order is SimilarityOrderAsc similarityOrderAsc)
                        {
                            similarityOrderAsc.Scores = _similarityScores;
                        }
                        else if (order is Orders.RuleBlockOrder ruleBlockOrder)
                        {
                            ruleBlockOrder.GroupMappings = _itemGroupMappings;
                        }
                        else if (order is Orders.RuleBlockOrderDesc ruleBlockOrderDesc)
                        {
                            ruleBlockOrderDesc.GroupMappings = _itemGroupMappings;
                        }
                    }

                    // Apply multiple orders in cascade
                    var orderedResults = ApplyMultipleOrders(expandedResults, user, userDataManager, logger, refreshCache);

                    // Apply limits (items and/or time)
                    if (MaxItems > 0 || MaxPlayTimeMinutes > 0)
                    {
                        var limitedResults = ApplyLimits(orderedResults, libraryManager, user, userDataManager, refreshCache, logger);

                        var hasRandomOrder = Orders.Any(o => o is RandomOrder);
                        if (hasRandomOrder)
                        {
                            logger?.LogDebug("Applied random order and limited playlist '{PlaylistName}' to {LimitedCount} items from {TotalItems} total items",
                                Name, limitedResults.Count, orderedResults.Count());
                        }
                        else
                        {
                            logger?.LogDebug("Limited playlist '{PlaylistName}' to {LimitedCount} items from {TotalItems} total items (deterministic order)",
                                Name, limitedResults.Count, orderedResults.Count());
                        }

                        return limitedResults.Select(x => x.Id);
                    }
                    else
                    {
                        // No limits - return all ordered results
                        var hasRandomOrder = Orders.Any(o => o is RandomOrder);
                        if (hasRandomOrder)
                        {
                            logger?.LogDebug("Applied random order to playlist '{PlaylistName}' with {TotalItems} items (no limit)",
                                Name, orderedResults.Count());
                        }

                        return orderedResults.Select(x => x.Id);
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error applying ordering and limits to playlist '{PlaylistName}'. Returning unordered results.", Name);
                    return expandedResults.Select(x => x.Id);
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                logger?.LogError(ex, "Critical error in FilterPlaylistItems for playlist '{PlaylistName}' after {ElapsedTime}ms. Returning empty results.",
                    Name, stopwatch.ElapsedMilliseconds);
                return [];
            }
        }

        private bool ShouldExpandEpisodesForCollections()
        {
            // Only expand if Episodes media type is selected AND Collections expansion is enabled
            var isEpisodesMediaType = MediaTypes?.Contains(Constants.MediaTypes.Episode) == true;
            var hasCollectionsEpisodeExpansion = ExpressionSets?.Any(set =>
                set.Expressions?.Any(expr =>
                    expr.MemberName == "Collections" && expr.IncludeEpisodesWithinSeries == true) == true) == true;

            return isEpisodesMediaType && hasCollectionsEpisodeExpansion;
        }

        /// <summary>
        /// Checks if any expression set has a per-group MaxItems limit defined.
        /// </summary>
        private bool HasPerGroupLimits()
        {
            return ExpressionSets?.Any(set => set.MaxItems.HasValue && set.MaxItems.Value > 0) == true;
        }

        private bool UsesRuleBlockOrdering()
        {
            return Orders?.Any(order => 
                order is Orders.RuleBlockOrder || 
                order is Orders.RuleBlockOrderDesc) == true;
        }

        private bool NeedsGroupTracking()
        {
            return HasPerGroupLimits() || UsesRuleBlockOrdering();
        }

        /// <summary>
        /// Applies per-group MaxItems limits to items before global sorting and limits.
        /// Items are organized by rule group, limited per group, then combined.
        /// </summary>
        private List<BaseItem> ApplyPerGroupLimits(List<BaseItem> items, User user, IUserDataManager? userDataManager, ILogger? logger, RefreshQueueService.RefreshCache refreshCache)
        {
            try
            {
                if (items == null || items.Count == 0)
                {
                    return items ?? new List<BaseItem>();
                }

                // Group items by their matching rule groups
                var itemsByGroup = new Dictionary<int, List<BaseItem>>();
                
                // Initialize all groups (even empty ones)
                for (int i = 0; i < ExpressionSets.Count; i++)
                {
                    itemsByGroup[i] = new List<BaseItem>();
                }

                // Organize items into groups
                foreach (var item in items)
                {
                    if (_itemGroupMappings.TryGetValue(item.Id, out var groups))
                    {
                        foreach (var groupIndex in groups)
                        {
                            if (groupIndex >= 0 && groupIndex < ExpressionSets.Count)
                            {
                                itemsByGroup[groupIndex].Add(item);
                            }
                        }
                    }
                }

                // Apply sorting and limits per group, then combine
                // Track globally consumed items so duplicate blocks pull different items from the same pool
                // Example: Two "crowd" blocks with MaxItems=1 each get the 1st and 2nd crowd episode
                var consumedItems = new HashSet<Guid>();
                var resultList = new List<BaseItem>();

                for (int groupIndex = 0; groupIndex < ExpressionSets.Count; groupIndex++)
                {
                    var group = ExpressionSets[groupIndex];
                    var groupItems = itemsByGroup[groupIndex];

                    if (groupItems.Count == 0)
                    {
                        logger?.LogDebug("Rule group {GroupIndex} has no matching items", groupIndex);
                        continue;
                    }

                    logger?.LogDebug("Rule group {GroupIndex} has {Count} matching items before limit", groupIndex, groupItems.Count);

                    // Apply sorting to this group's items (using the playlist's sort orders).
                    // NOTE: This sort happens BEFORE the global sort later in the pipeline.
                    // For Rule Block ordering: this applies secondary sorts within each block,
                    //   then the global sort applies the Rule Block order while preserving these secondary sorts.
                    // For non-Rule Block ordering: this creates a minor redundancy (items sorted twice),
                    //   but per-group limits are primarily designed for Rule Block scenarios.
                    var sortedGroupItems = ApplyMultipleOrders(groupItems, user, userDataManager, logger, refreshCache).ToList();

                    // Filter out items that were already consumed by previous blocks
                    // This allows duplicate blocks to pull the "next" items from the same pool
                    var availableItems = sortedGroupItems.Where(item => !consumedItems.Contains(item.Id)).ToList();

                    logger?.LogDebug("Rule group {GroupIndex} has {Available} available items after filtering consumed items ({Consumed} already used)",
                        groupIndex, availableItems.Count, sortedGroupItems.Count - availableItems.Count);

                    // Apply per-group limit if configured
                    var groupMaxItems = group.MaxItems ?? 0;
                    List<BaseItem> selectedItems;
                    if (groupMaxItems > 0 && availableItems.Count > groupMaxItems)
                    {
                        selectedItems = availableItems.Take(groupMaxItems).ToList();
                        logger?.LogDebug("Rule group {GroupIndex} limited from {Available} to {Limited} items", 
                            groupIndex, availableItems.Count, selectedItems.Count);
                    }
                    else
                    {
                        selectedItems = availableItems;
                    }

                    // Mark these items as consumed and add to result
                    // Update group mappings to reflect which block contributed this item
                    // This ensures Rule Block Order sorts correctly after per-group limiting
                    foreach (var item in selectedItems)
                    {
                        consumedItems.Add(item.Id);
                        resultList.Add(item);
                        
                        // Update the group mapping to show this item was contributed by this specific block
                        // This overrides the original multi-group mapping (e.g., item matching both "crowd" and "german")
                        _itemGroupMappings[item.Id] = new List<int> { groupIndex };
                    }

                    logger?.LogDebug("Rule group {GroupIndex} contributed {Count} items to result", groupIndex, selectedItems.Count);
                }

                logger?.LogDebug("Per-group limiting: {Original} items → {Limited} items across {Groups} groups",
                    items.Count, resultList.Count, ExpressionSets.Count);

                return resultList;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error applying per-group limits for playlist '{PlaylistName}'. Returning all items.", Name);
                return items;
            }
        }

        /// <summary>
        /// Core logic for checking if Collections data matches Collections rules.
        /// </summary>
        /// <param name="collections">The collections data to check</param>
        /// <returns>True if collections match any Collections rule, false otherwise</returns>
        private bool DoCollectionsMatchRules(List<string> collections)
        {
            if (collections == null || collections.Count == 0)
                return false;

            // Check if any collection matches any Collections rule
            // Skip rules with IncludeCollectionOnly=true since those are handled separately
            return ExpressionSets?.Any(set =>
                set.Expressions?.Any(expr =>
                    expr.MemberName == "Collections" &&
                    expr.IncludeCollectionOnly != true && // Skip IncludeCollectionOnly rules
                    DoesCollectionMatchRule(collections, expr)) == true) == true;
        }

        /// <summary>
        /// Checks if collections match a specific Collections rule.
        /// </summary>
        /// <param name="collections">The collections data to check</param>
        /// <param name="expr">The expression rule to check against</param>
        /// <returns>True if collections match the rule, false otherwise</returns>
        private static bool DoesCollectionMatchRule(List<string> collections, Expression expr)
        {
            if (string.IsNullOrEmpty(expr.TargetValue))
                return false;

            switch (expr.Operator)
            {
                case "Equal":
                    // Check both exact name match and name without prefix/suffix
                    // This handles cases where collections have prefix/suffix applied but users enter base name
                    return collections.Any(c => 
                        c != null && 
                        (c.Equals(expr.TargetValue, StringComparison.OrdinalIgnoreCase) ||
                         NameFormatter.StripPrefixAndSuffix(c).Equals(expr.TargetValue, StringComparison.OrdinalIgnoreCase)));

                case "Contains":
                    // Reuse Engine helper for consistency and null safety
                    return Engine.AnyItemContains(collections, expr.TargetValue);

                case "IsIn":
                    // Maintain parity with Engine's "contains any in list" semantics
                    return Engine.AnyItemIsInList(collections, expr.TargetValue);

                case "MatchRegex":
                    // Delegate to Engine to leverage compiled regex cache and uniform error handling
                    try { return Engine.AnyRegexMatch(collections, expr.TargetValue); }
                    catch (ArgumentException) { return false; }

                default:
                    // Unknown operator - treat as no match
                    return false;
            }
        }

        /// <summary>
        /// Gets collections that match Collections rules when IncludeCollectionOnly is enabled.
        /// </summary>
        /// <param name="libraryManager">Library manager to query collections</param>
        /// <param name="user">User context</param>
        /// <param name="logger">Logger for debugging</param>
        /// <returns>List of matching collections</returns>
        private List<BaseItem> GetMatchingCollections(ILibraryManager libraryManager, User user, ILogger? logger)
        {
            var matchingCollections = new List<BaseItem>();
            
            try
            {
                // Query all collections (BoxSet items)
                var collectionQuery = new InternalItemsQuery(user)
                {
                    IncludeItemTypes = [BaseItemKind.BoxSet],
                    Recursive = true,
                };

                var allCollections = libraryManager.GetItemsResult(collectionQuery).Items;
                logger?.LogDebug("Found {Count} total collections to check against Collections rules with IncludeCollectionOnly=true", allCollections.Count);

                // Check each collection against Collections rules with IncludeCollectionOnly=true
                foreach (var collection in allCollections)
                {
                    if (collection == null) continue;

                    // Skip if this collection is the same as the one we're currently building (prevent self-reference)
                    var collectionBaseName = NameFormatter.StripPrefixAndSuffix(collection.Name);
                    var currentListBaseName = NameFormatter.StripPrefixAndSuffix(Name);
                    if (collectionBaseName.Equals(currentListBaseName, StringComparison.OrdinalIgnoreCase))
                    {
                        logger?.LogDebug("Skipping collection '{CollectionName}' - matches current collection being built (preventing self-reference)", collection.Name);
                        continue;
                    }

                    // Check if this collection's name matches any Collections rule with IncludeCollectionOnly=true
                    var collectionNames = new List<string> { collection.Name };
                    if (DoCollectionsMatchRulesForIncludeCollectionOnly(collectionNames))
                    {
                        matchingCollections.Add(collection);
                        logger?.LogDebug("Collection '{CollectionName}' matches Collections rules with IncludeCollectionOnly=true", collection.Name);
                    }
                }

                logger?.LogDebug("Found {Count} matching collections out of {TotalCount} total collections",
                    matchingCollections.Count, allCollections.Count);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error fetching matching collections for IncludeCollectionOnly mode");
            }

            return matchingCollections;
        }

        /// <summary>
        /// Checks if collections match Collections rules with IncludeCollectionOnly=true.
        /// This is used specifically for IncludeCollectionOnly mode to match collection names.
        /// </summary>
        /// <param name="collections">The collection names to check</param>
        /// <returns>True if collections match any Collections rule with IncludeCollectionOnly=true, false otherwise</returns>
        private bool DoCollectionsMatchRulesForIncludeCollectionOnly(List<string> collections)
        {
            if (collections == null || collections.Count == 0)
                return false;

            // Check if any collection matches any Collections rule with IncludeCollectionOnly=true
            return ExpressionSets?.Any(set =>
                set.Expressions?.Any(expr =>
                    expr.MemberName == "Collections" &&
                    expr.IncludeCollectionOnly == true && // Only check IncludeCollectionOnly rules
                    DoesCollectionMatchRule(collections, expr)) == true) == true;
        }

        /// <summary>
        /// Gets playlists that match Playlists rules when IncludePlaylistOnly is enabled.
        /// </summary>
        /// <param name="libraryManager">Library manager to query playlists</param>
        /// <param name="user">User context</param>
        /// <param name="logger">Logger for debugging</param>
        /// <returns>List of matching playlists</returns>
        private List<BaseItem> GetMatchingPlaylists(ILibraryManager libraryManager, User user, ILogger? logger)
        {
            var matchingPlaylists = new List<BaseItem>();
            
            try
            {
                // Query all playlists
                var playlistQuery = new InternalItemsQuery(user)
                {
                    IncludeItemTypes = [BaseItemKind.Playlist],
                    Recursive = true,
                };

                var allPlaylists = libraryManager.GetItemsResult(playlistQuery).Items;
                logger?.LogDebug("Found {Count} total playlists to check against Playlists rules with IncludePlaylistOnly=true", allPlaylists.Count);

                // Check each playlist against Playlists rules with IncludePlaylistOnly=true
                foreach (var playlist in allPlaylists)
                {
                    if (playlist == null) continue;

                    // Skip if this playlist is the same as the one we're currently building (prevent self-reference)
                    var playlistBaseName = NameFormatter.StripPrefixAndSuffix(playlist.Name);
                    var currentListBaseName = NameFormatter.StripPrefixAndSuffix(Name);
                    if (playlistBaseName.Equals(currentListBaseName, StringComparison.OrdinalIgnoreCase))
                    {
                        logger?.LogDebug("Skipping playlist '{PlaylistName}' - matches current list being built (preventing self-reference)", playlist.Name);
                        continue;
                    }

                    // Check if this playlist's name matches any Playlists rule with IncludePlaylistOnly=true
                    var playlistNames = new List<string> { playlist.Name };
                    if (DoPlaylistsMatchRulesForIncludePlaylistOnly(playlistNames))
                    {
                        matchingPlaylists.Add(playlist);
                        logger?.LogDebug("Playlist '{PlaylistName}' matches Playlists rules with IncludePlaylistOnly=true", playlist.Name);
                    }
                }

                logger?.LogDebug("Found {Count} matching playlists out of {TotalCount} total playlists",
                    matchingPlaylists.Count, allPlaylists.Count);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error fetching matching playlists for IncludePlaylistOnly mode");
            }

            return matchingPlaylists;
        }

        /// <summary>
        /// Checks if playlists match Playlists rules with IncludePlaylistOnly=true.
        /// This is used specifically for IncludePlaylistOnly mode to match playlist names.
        /// </summary>
        /// <param name="playlists">The playlist names to check</param>
        /// <returns>True if playlists match any Playlists rule with IncludePlaylistOnly=true, false otherwise</returns>
        private bool DoPlaylistsMatchRulesForIncludePlaylistOnly(List<string> playlists)
        {
            if (playlists == null || playlists.Count == 0)
                return false;

            // Check if any playlist matches any Playlists rule with IncludePlaylistOnly=true
            return ExpressionSets?.Any(set =>
                set.Expressions?.Any(expr =>
                    expr.MemberName == "Playlists" &&
                    expr.IncludePlaylistOnly == true && // Only check IncludePlaylistOnly rules
                    DoesPlaylistMatchRule(playlists, expr)) == true) == true;
        }

        /// <summary>
        /// Checks if playlists match a specific Playlists rule.
        /// </summary>
        /// <param name="playlists">The playlist names to check</param>
        /// <param name="expr">The expression rule to check against</param>
        /// <returns>True if playlists match the rule, false otherwise</returns>
        private static bool DoesPlaylistMatchRule(List<string> playlists, Expression expr)
        {
            if (string.IsNullOrEmpty(expr.TargetValue))
                return false;

            switch (expr.Operator)
            {
                case "Equal":
                    // Check both exact name match and name without prefix/suffix
                    return playlists.Any(p => 
                        p != null && 
                        (p.Equals(expr.TargetValue, StringComparison.OrdinalIgnoreCase) ||
                         NameFormatter.StripPrefixAndSuffix(p).Equals(expr.TargetValue, StringComparison.OrdinalIgnoreCase)));

                case "Contains":
                    // Reuse Engine helper for consistency and null safety
                    return Engine.AnyItemContains(playlists, expr.TargetValue);

                case "IsIn":
                    // Maintain parity with Engine's "contains any in list" semantics
                    return Engine.AnyItemIsInList(playlists, expr.TargetValue);

                case "MatchRegex":
                    // Delegate to Engine to leverage compiled regex cache and uniform error handling
                    try { return Engine.AnyRegexMatch(playlists, expr.TargetValue); }
                    catch (ArgumentException) { return false; }

                default:
                    // Unknown operator - treat as no match
                    return false;
            }
        }

        /// <summary>
        /// Checks if a series matches any Collections rule for episode expansion.
        /// </summary>
        /// <param name="series">The series to check</param>
        /// <param name="libraryManager">Library manager for operand creation</param>
        /// <param name="user">User context</param>
        /// <param name="userDataManager">User data manager</param>
        /// <param name="logger">Logger for debugging</param>
        /// <param name="refreshCache">Cache for performance optimization</param>
        /// <returns>True if the series matches Collections rules, false otherwise</returns>
        private bool DoesSeriesMatchCollectionsRules(Series series,
            ILibraryManager libraryManager, User user, IUserDataManager? userDataManager,
            ILogger? logger, RefreshQueueService.RefreshCache refreshCache)
        {
            try
            {
                logger?.LogDebug("Series '{SeriesName}' checking Collections rules for expansion eligibility", series.Name);

                // Extract Collections data for this series to check if it matches Collections rules
                var collectionsOperand = OperandFactory.GetMediaType(libraryManager, series, user, userDataManager, UserManager, logger, new MediaTypeExtractionOptions
                {
                    ExtractAudioLanguages = false,
                    ExtractPeople = false,
                    ExtractCollections = true,  // Only extract Collections for this check
                    ExtractNextUnwatched = false,
                    ExtractSeriesName = false,
                    IncludeUnwatchedSeries = true,
                    AdditionalUserIds = [],
                    OriginListName = Name,
                },
                refreshCache);
                bool matchesCollectionsRule = DoCollectionsMatchRules(collectionsOperand.Collections);

                if (matchesCollectionsRule)
                {
                    logger?.LogDebug("Series '{SeriesName}' matches Collections rules - eligible for expansion", series.Name);
                }
                else
                {
                    logger?.LogDebug("Series '{SeriesName}' does not match Collections rules - skipping", series.Name);
                }

                return matchesCollectionsRule;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Error checking Collections rules for series '{SeriesName}', excluding from expansion", series.Name);
                return false;
            }
        }

        /// <summary>
        /// Checks if a series matches Collections rules using an existing operand (for cases where Collections data is already extracted).
        /// </summary>
        /// <param name="series">The series to check</param>
        /// <param name="operand">Operand with Collections data already extracted</param>
        /// <param name="logger">Logger for debugging</param>
        /// <returns>True if the series matches Collections rules, false otherwise</returns>
        private bool DoesSeriesMatchCollectionsRules(Series series,
            Operand operand, ILogger? logger)
        {
            try
            {
                logger?.LogDebug("Series '{SeriesName}' checking Collections rules for expansion (using existing operand)", series.Name);

                // Check if this series matches any Collections rule (even if it fails other rules)
                var hasCollectionsInAnyGroup = ExpressionSets?.Any(set =>
                    set.Expressions?.Any(expr => expr.MemberName == "Collections") == true) == true;

                bool matchesCollectionsRule = hasCollectionsInAnyGroup && DoCollectionsMatchRules(operand.Collections);

                if (matchesCollectionsRule)
                {
                    logger?.LogDebug("Series '{SeriesName}' matches Collections rules - eligible for expansion", series.Name);
                }
                else
                {
                    logger?.LogDebug("Series '{SeriesName}' does not match Collections rules - skipping expansion", series.Name);
                }

                return matchesCollectionsRule;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Error checking Collections rules for series '{SeriesName}', excluding from expansion", series.Name);
                return false;
            }
        }

        private List<BaseItem> ExpandCollectionsBasedOnMediaType(List<BaseItem> items, ILibraryManager libraryManager, User user, IUserDataManager? userDataManager, ILogger? logger, RefreshQueueService.RefreshCache refreshCache)
        {
            try
            {
                // Media-type driven Collections expansion logic
                var isEpisodesMediaType = MediaTypes?.Contains(Constants.MediaTypes.Episode) == true;

                // Check if Collections rules have episode expansion enabled
                var hasCollectionsEpisodeExpansion = ExpressionSets?.Any(set =>
                    set.Expressions?.Any(expr =>
                        expr.MemberName == "Collections" && expr.IncludeEpisodesWithinSeries == true) == true) == true;

                // Episodes media type with Collections expansion enabled: Expand and deduplicate
                if (isEpisodesMediaType && hasCollectionsEpisodeExpansion)
                {
                    logger?.LogDebug("Episodes media type + Collections expansion enabled - processing episodes and series for playlist '{PlaylistName}'", Name);

                    var resultItems = new List<BaseItem>();
                    var episodeIds = new HashSet<Guid>(); // Deduplication tracker for episodes
                    var seriesIds = new HashSet<Guid>(); // Deduplication tracker for series

                    foreach (var item in items)
                    {
                        if (item is Episode)
                        {
                            // Direct episode from collection - add if not already seen
                            if (episodeIds.Add(item.Id))
                            {
                                resultItems.Add(item);
                                logger?.LogDebug("Added direct episode '{EpisodeName}' from collection", item.Name);
                            }
                        }
                        else if (item is Series series)
                        {
                            // Series from collection - expand to episodes and add unique ones
                            var seriesEpisodes = GetSeriesEpisodes(series, libraryManager, user, logger);

                            if (seriesEpisodes.Count > 0)
                            {
                                logger?.LogDebug("Expanding series '{SeriesName}' with {TotalEpisodes} episodes", series.Name, seriesEpisodes.Count);

                                // Filter episodes against rules (excluding Collections rules since parent series matched)
                                var matchingEpisodes = FilterEpisodesAgainstRules(seriesEpisodes, libraryManager, user, userDataManager, logger, refreshCache, series);

                                // Add unique matching episodes
                                int addedCount = 0;
                                foreach (var matchingEpisode in matchingEpisodes)
                                {
                                    if (episodeIds.Add(matchingEpisode.Id))
                                    {
                                        resultItems.Add(matchingEpisode);
                                        addedCount++;
                                    }
                                }

                                logger?.LogDebug("Added {AddedEpisodes} unique episodes from series '{SeriesName}' (filtered from {MatchingEpisodes} matching episodes)",
                                    addedCount, series.Name, matchingEpisodes.Count);
                            }
                            else
                            {
                                logger?.LogDebug("Series '{SeriesName}' has no episodes to expand", series.Name);
                            }
                        }
                        else
                        {
                            // Non-TV item, keep as-is
                            resultItems.Add(item);
                        }
                    }

                    logger?.LogDebug("Collections expansion complete: {TotalItems} items from {OriginalItems} original items",
                        resultItems.Count, items.Count);

                    return resultItems;
                }

                // No expansion needed - return original items
                logger?.LogDebug("No Collections episode expansion needed for playlist '{PlaylistName}' - MediaTypes: [{MediaTypes}], HasExpansion: {HasExpansion}",
                    Name, MediaTypes != null ? string.Join(",", MediaTypes) : "None", hasCollectionsEpisodeExpansion);

                return items;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error in Collections processing for playlist '{PlaylistName}', returning original results", Name);
                return items;
            }
        }

        private static List<BaseItem> GetSeriesEpisodes(Series series, ILibraryManager libraryManager, User user, ILogger? logger)
        {
            try
            {
                var query = new InternalItemsQuery(user)
                {
                    IncludeItemTypes = [BaseItemKind.Episode],
                    ParentId = series.Id,
                    Recursive = true,
                };

                var result = libraryManager.GetItemsResult(query);
                logger?.LogDebug("Found {EpisodeCount} episodes for series '{SeriesName}' (ID: {SeriesId})",
                    result.TotalRecordCount, series.Name, series.Id);

                return [.. result.Items];
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error getting episodes for series '{SeriesName}'", series.Name);
                return [];
            }
        }

        private List<BaseItem> FilterEpisodesAgainstRules(List<BaseItem> episodes, ILibraryManager libraryManager, User user, IUserDataManager? userDataManager, ILogger? logger, RefreshQueueService.RefreshCache refreshCache, Series? parentSeries = null)
        {
            try
            {
                var matchingEpisodes = new List<BaseItem>();

                // Compile the rules if not already compiled
                // Use the user parameter's ID as the default for user-specific fields without explicit UserId
                // Normalize to "N" format (no dashes) to match UserPlaylists format
                var defaultUserId = user.Id.ToString("N");
                var compiledRules = CompileRuleSets(defaultUserId, logger);
                if (compiledRules == null || compiledRules.Count == 0)
                {
                    return episodes; // No rules to check against,
                }

                // Check field requirements for performance optimization
                var fieldReqs = FieldRequirements.Analyze(ExpressionSets, Orders);
                var needsAudioLanguages = fieldReqs.NeedsAudioLanguages;
                var needsAudioQuality = fieldReqs.NeedsAudioQuality;
                var needsVideoQuality = fieldReqs.NeedsVideoQuality;
                var needsPeople = fieldReqs.NeedsPeople;
                var needsCollections = fieldReqs.NeedsCollections;
                var needsPlaylists = fieldReqs.NeedsPlaylists;
                var needsNextUnwatched = fieldReqs.NeedsNextUnwatched;
                var needsSeriesName = fieldReqs.NeedsSeriesName;
                var needsParentSeriesTags = fieldReqs.NeedsParentSeriesTags;
                var needsParentSeriesStudios = fieldReqs.NeedsParentSeriesStudios;
                var needsParentSeriesGenres = fieldReqs.NeedsParentSeriesGenres;
                var includeUnwatchedSeries = fieldReqs.IncludeUnwatchedSeries;
                var additionalUserIds = fieldReqs.AdditionalUserIds;

                logger?.LogDebug("Filtering {EpisodeCount} episodes against playlist rules", episodes.Count);

                foreach (var episode in episodes)
                {
                    try
                    {
                        var operand = OperandFactory.GetMediaType(libraryManager, episode, user, userDataManager, UserManager, logger, new MediaTypeExtractionOptions
                        {
                            ExtractAudioLanguages = needsAudioLanguages,
                            ExtractAudioQuality = needsAudioQuality,
                            ExtractVideoQuality = needsVideoQuality,
                            ExtractPeople = needsPeople,
                            ExtractCollections = needsCollections,
                            ExtractPlaylists = needsPlaylists,
                            ExtractNextUnwatched = needsNextUnwatched,
                            ExtractSeriesName = needsSeriesName,
                            ExtractParentSeriesTags = needsParentSeriesTags,
                            ExtractParentSeriesStudios = needsParentSeriesStudios,
                            ExtractParentSeriesGenres = needsParentSeriesGenres,
                            IncludeUnwatchedSeries = includeUnwatchedSeries,
                            AdditionalUserIds = additionalUserIds,
                            OriginListName = Name,
                        }, refreshCache);

                        var matches = EvaluateLogicGroupsForEpisode(compiledRules, operand, parentSeries, logger);

                        if (matches)
                        {
                            matchingEpisodes.Add(episode);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Error evaluating episode '{EpisodeName}' against rules, excluding from results", episode.Name);
                        continue;
                    }
                }

                logger?.LogDebug("Episode filtering complete: {MatchingCount} of {TotalCount} episodes passed rules",
                    matchingEpisodes.Count, episodes.Count);

                return matchingEpisodes;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error filtering episodes against rules, returning all episodes");
                return episodes;
            }
        }

        /// <summary>
        /// Applies multiple sorting orders in cascade to a collection of items.
        /// </summary>
        /// <param name="items">The items to sort</param>
        /// <param name="user">User for user-specific sorting</param>
        /// <param name="userDataManager">User data manager for user-specific sorting</param>
        /// <param name="logger">Optional logger for debugging</param>
        /// <returns>The sorted collection of items</returns>
        private IEnumerable<BaseItem> ApplyMultipleOrders(IEnumerable<BaseItem> items, User user, IUserDataManager? userDataManager, ILogger? logger, RefreshQueueService.RefreshCache refreshCache)
        {
            if (Orders == null || Orders.Count == 0)
            {
                return items;
            }

            logger?.LogDebug("ApplyMultipleOrders: Processing {OrderCount} sort options", Orders.Count);
            for (int i = 0; i < Orders.Count; i++)
            {
                logger?.LogDebug("  Sort #{Index}: {OrderName}", i + 1, Orders[i].Name);
            }

            // If there's only one order, use the original Order.OrderBy() method
            // Single sort optimization is now safe because GetSortKey logic is unified with OrderBy logic
            if (Orders.Count == 1)
            {
                logger?.LogDebug("Single sort detected, returning result from Order.OrderBy()");
                return Orders[0].OrderBy(items, user, userDataManager, logger, refreshCache);
            }

            // Use the common sorting helper for multi-sort scenarios
            return ApplySortingCore(items.ToList(), Orders, user, userDataManager, logger, refreshCache);
        }

        /// <summary>
        /// Core sorting logic shared by ApplyMultipleOrders and ApplySecondarySorts.
        /// Creates composite sort keys for all items and applies multi-level sorting.
        /// </summary>
        private static IEnumerable<BaseItem> ApplySortingCore(
            List<BaseItem> itemsList, 
            List<Order> orders, 
            User user, 
            IUserDataManager? userDataManager, 
            ILogger? logger, 
            RefreshQueueService.RefreshCache refreshCache)
        {
            if (orders == null || orders.Count == 0 || itemsList.Count == 0)
            {
                return itemsList;
            }

            // Create a random seed for this sort operation (different each refresh, but stable within this sort)
            var randomSeed = (int)(DateTime.Now.Ticks & 0x7FFFFFFF);
            var itemRandomKeys = new Dictionary<Guid, int>();

            // Pre-generate random keys for items if any RandomOrder is present
            if (orders.Any(o => o is RandomOrder))
            {
                logger?.LogDebug("RandomOrder detected in multi-sort, pre-generating random keys with seed: {Seed}", randomSeed);
                // Suppress CA5394: Random is acceptable here - we're not using it for security purposes, just for shuffling playlist items
#pragma warning disable CA5394
                var random = new Random(randomSeed);
                foreach (var item in itemsList)
                {
                    itemRandomKeys[item.Id] = random.Next();
                }
#pragma warning restore CA5394
                logger?.LogDebug("Pre-generated {Count} random keys for items", itemRandomKeys.Count);
            }

            // Create sort keys for each item based on all orders
            var itemsWithKeys = itemsList.Select(item => new
            {
                Item = item,
                SortKeys = orders.Select(order => order.GetSortKey(item, user, userDataManager, logger, itemRandomKeys, refreshCache)).ToList(),
            }).ToList();

            // Sort using the composite keys
            IOrderedEnumerable<dynamic>? orderedItems = null;
            for (int i = 0; i < orders.Count; i++)
            {
                var index = i; // Capture for lambda
                var order = orders[i];

                if (i == 0)
                {
                    // First sort
                    if (IsDescendingOrder(order))
                    {
                        orderedItems = itemsWithKeys.OrderByDescending(x => x.SortKeys[index]);
                    }
                    else
                    {
                        orderedItems = itemsWithKeys.OrderBy(x => x.SortKeys[index]);
                    }
                }
                else
                {
                    // Secondary sorts
                    if (orderedItems == null)
                    {
                        throw new InvalidOperationException("orderedItems is null when applying secondary sort");
                    }
                    if (IsDescendingOrder(order))
                    {
                        orderedItems = orderedItems.ThenByDescending(x => x.SortKeys[index]);
                    }
                    else
                    {
                        orderedItems = orderedItems.ThenBy(x => x.SortKeys[index]);
                    }
                }
            }

            if (orderedItems == null)
            {
                return itemsList;
            }

            return orderedItems.Select(x => (BaseItem)x.Item);
        }

        /// <summary>
        /// Determines if an order is descending based on its type.
        /// </summary>
        private static bool IsDescendingOrder(Order order)
        {
            return order is NameOrderDesc ||
                   order is NameIgnoreArticlesOrderDesc ||
                   order is ProductionYearOrderDesc ||
                   order is DateCreatedOrderDesc ||
                   order is ReleaseDateOrderDesc ||
                   order is CommunityRatingOrderDesc ||
                   order is PlayCountOrderDesc ||
                   order is LastPlayedOrderDesc ||
                   order is RuntimeOrderDesc ||
                   order is SeriesNameOrderDesc ||
                   order is SeriesNameIgnoreArticlesOrderDesc ||
                   order is AlbumNameOrderDesc ||
                   order is ArtistOrderDesc ||
                   order is SeasonNumberOrderDesc ||
                   order is EpisodeNumberOrderDesc ||
                   order is TrackNumberOrderDesc ||
                   order is Orders.RuleBlockOrderDesc ||
                   order is SimilarityOrder; // Similarity descending is the default,
        }

        /// <summary>
        /// Applies item count and time-based limits to a collection of items.
        /// </summary>
        /// <param name="items">The ordered items to limit</param>
        /// <param name="libraryManager">Library manager for operand creation</param>
        /// <param name="user">User for operand creation</param>
        /// <param name="userDataManager">User data manager for operand creation</param>
        /// <param name="logger">Optional logger for debugging</param>
        /// <returns>The limited collection of items</returns>
        private List<BaseItem> ApplyLimits(IEnumerable<BaseItem> items, ILibraryManager libraryManager, User user, IUserDataManager? userDataManager, RefreshQueueService.RefreshCache refreshCache, ILogger? logger = null)
        {
            var itemsList = items.ToList();
            if (itemsList.Count == 0) return itemsList;

            var limitedItems = new List<BaseItem>();
            var totalMinutes = 0.0;
            var itemCount = 0;

            foreach (var item in itemsList)
            {
                // Check item count limit
                if (MaxItems > 0 && itemCount >= MaxItems)
                {
                    logger?.LogDebug("Reached item count limit ({MaxItems}) for playlist '{PlaylistName}'", MaxItems, Name);
                    break;
                }

                // Get runtime for this item
                var itemMinutes = 0.0;
                if (MaxPlayTimeMinutes > 0)
                {
                    try
                    {
                        // Use the same runtime extraction logic as in Factory.cs
                        if (item.RunTimeTicks.HasValue)
                        {
                            // Use exact TotalMinutes as double for precise calculation
                            itemMinutes = TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalMinutes;
                        }
                        else
                        {
                            // Fallback: try to get runtime from Operand extraction
                            var operand = OperandFactory.GetMediaType(libraryManager, item, user, userDataManager, UserManager, logger, new MediaTypeExtractionOptions
                            {
                                ExtractAudioLanguages = false,
                                ExtractPeople = false,
                                ExtractCollections = false,
                                ExtractNextUnwatched = false,
                                ExtractSeriesName = false,
                                IncludeUnwatchedSeries = true,
                                AdditionalUserIds = [],
                                OriginListName = Name,
                            },
                            refreshCache);
                            itemMinutes = operand.RuntimeMinutes;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Error getting runtime for item '{ItemName}' in playlist '{PlaylistName}'. Assuming 0 minutes.", item.Name, Name);
                        itemMinutes = 0.0;
                    }
                }

                // Check time limit
                if (MaxPlayTimeMinutes > 0 && totalMinutes + itemMinutes > MaxPlayTimeMinutes)
                {
                    logger?.LogDebug("Reached time limit ({MaxTime} minutes) for playlist '{PlaylistName}' at {CurrentTime:F1} minutes. Next item '{ItemName}' ({ItemMinutes:F1} minutes) would exceed limit.", MaxPlayTimeMinutes, Name, totalMinutes, item.Name, itemMinutes);
                    break;
                }

                // Add item to results
                limitedItems.Add(item);
                totalMinutes += itemMinutes;
                itemCount++;
            }

            logger?.LogInformation("Applied limits to playlist '{PlaylistName}': {ItemCount} items, {TotalMinutes:F1} minutes (MaxItems: {MaxItems}, MaxTime: {MaxTime} minutes)",
                Name, itemCount, totalMinutes, MaxItems, MaxPlayTimeMinutes);

            return limitedItems;
        }

        private List<BaseItem> ProcessItemChunk(IEnumerable<BaseItem> items, ILibraryManager libraryManager,
            User user, IUserDataManager? userDataManager, ILogger? logger, bool needsAudioLanguages, bool needsAudioQuality, bool needsVideoQuality, bool needsPeople, bool needsCollections, bool needsPlaylists, bool needsNextUnwatched, bool needsSeriesName, bool needsParentSeriesTags, bool needsParentSeriesStudios, bool needsParentSeriesGenres, bool needsSimilarTo, bool includeUnwatchedSeries,
            List<string> additionalUserIds, OperandFactory.ReferenceMetadata? referenceMetadata, List<string> similarityComparisonFields, List<List<Func<Operand, bool>>> compiledRules, bool hasAnyRules, bool hasNonExpensiveRules, RefreshQueueService.RefreshCache refreshCache)
        {
            var results = new List<BaseItem>();

            try
            {
                if (items == null || compiledRules == null)
                {
                    logger?.LogDebug("ProcessItemChunk called with null items or compiledRules");
                    return results;
                }

                if (needsAudioLanguages || needsAudioQuality || needsVideoQuality || needsPeople || needsCollections || needsPlaylists || needsNextUnwatched || needsSeriesName || needsParentSeriesTags || needsParentSeriesStudios || needsParentSeriesGenres || needsSimilarTo)
                {
                    // Use the shared RefreshCache passed from FilterPlaylistItems for optimal performance across chunks
                    // referenceMetadata is also provided by caller (built once per filter run, not per chunk)

                    // Optimization: Separate rules into cheap and expensive categories
                    var cheapCompiledRules = new List<List<Func<Operand, bool>>>();

                    logger?.LogDebug("Separating rules into cheap and expensive categories (AudioLanguages: {AudioNeeded}, AudioQuality: {AudioQualityNeeded}, People: {PeopleNeeded}, Collections: {CollectionsNeeded}, Playlists: {PlaylistsNeeded}, NextUnwatched: {NextUnwatchedNeeded}, SeriesName: {SeriesNameNeeded}, ParentSeriesTags: {ParentSeriesTagsNeeded}, ParentSeriesStudios: {ParentSeriesStudiosNeeded}, ParentSeriesGenres: {ParentSeriesGenresNeeded}, SimilarTo: {SimilarToNeeded})",
                        needsAudioLanguages, needsAudioQuality, needsPeople, needsCollections, needsPlaylists, needsNextUnwatched, needsSeriesName, needsParentSeriesTags, needsParentSeriesStudios, needsParentSeriesGenres, needsSimilarTo);


                    try
                    {
                        for (int setIndex = 0; setIndex < ExpressionSets.Count && setIndex < compiledRules.Count; setIndex++)
                        {
                            var set = ExpressionSets[setIndex];
                            if (set?.Expressions == null) continue;

                            var cheapRules = new List<Func<Operand, bool>>();
                            int expensiveCount = 0;

                            // Use separate index for compiled rules since SimilarTo expressions are not compiled
                            int compiledIndex = 0;
                            for (int exprIndex = 0; exprIndex < set.Expressions.Count; exprIndex++)
                            {
                                var expr = set.Expressions[exprIndex];
                                if (expr == null) continue;

                                // SimilarTo is not compiled, skip it
                                if (expr.MemberName == "SimilarTo")
                                {
                                    expensiveCount++;
                                    logger?.LogDebug("Rule set {SetIndex}: Skipping SimilarTo rule (not compiled)", setIndex);
                                    continue;
                                }

                                try
                                {
                                    if (compiledIndex >= compiledRules[setIndex].Count)
                                    {
                                        logger?.LogDebug("Rule set {SetIndex}: No more compiled rules available at expression {ExprIndex}", setIndex, exprIndex);
                                        break;
                                    }

                                    var compiledRule = compiledRules[setIndex][compiledIndex++];

                                    // Check if this is an expensive field
                                    bool isExpensive = ExpensiveFields.Contains(expr.MemberName) ||
                                                      (expr.MemberName == "Tags" && expr.IncludeParentSeriesTags == true) ||
                                                      (expr.MemberName == "Studios" && expr.IncludeParentSeriesStudios == true) ||
                                                      (expr.MemberName == "Genres" && expr.IncludeParentSeriesGenres == true);

                                    if (isExpensive)
                                    {
                                        expensiveCount++;
                                        logger?.LogDebug("Rule set {SetIndex}: Added expensive rule: {Field} {Operator} {Value}",
                                            setIndex, expr.MemberName, expr.Operator, expr.TargetValue);
                                    }
                                    else
                                    {
                                        cheapRules.Add(compiledRule);
                                        logger?.LogDebug("Rule set {SetIndex}: Added non-expensive rule: {Field} {Operator} {Value}",
                                            setIndex, expr.MemberName, expr.Operator, expr.TargetValue);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logger?.LogDebug(ex, "Error processing rule at set {SetIndex}, expression {ExprIndex}", setIndex, exprIndex);
                                }
                            }

                            cheapCompiledRules.Add(cheapRules);

                            logger?.LogDebug("Rule set {SetIndex}: {NonExpensiveCount} non-expensive rules, {ExpensiveCount} expensive rules",
                                setIndex, cheapRules.Count, expensiveCount);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Error separating rules into cheap and expensive categories. Falling back to simple processing.");
                        return ProcessItemsSimple(items, libraryManager, user, userDataManager, logger, needsAudioLanguages, needsAudioQuality, needsVideoQuality, needsPeople, needsCollections, needsPlaylists, needsNextUnwatched, needsSeriesName, needsParentSeriesTags, needsParentSeriesStudios, needsParentSeriesGenres, includeUnwatchedSeries, additionalUserIds, referenceMetadata, similarityComparisonFields, needsSimilarTo, compiledRules, hasAnyRules, refreshCache);
                    }

                    if (!hasNonExpensiveRules)
                    {
                        // No non-expensive rules - extract expensive data for all items that have expensive rules
                        logger?.LogDebug("No non-expensive rules found, extracting expensive data for all items");

                        // Materialize items to prevent multiple enumerations
                        var itemList = items as IList<BaseItem> ?? items.ToList();

                        // Preload People cache in parallel if needed for performance
                        if (needsPeople)
                        {
                            logger?.LogDebug("Preloading People cache for all {Count} items (expensive-only path)", itemList.Count);
                            OperandFactory.PreloadPeopleCache(libraryManager, itemList, refreshCache, logger);
                        }

                        // Process items sequentially for expensive field extraction and evaluation
                        InvalidOperationException? userNotFoundException = null;

                        logger?.LogDebug("Processing {Count} items sequentially (expensive-only path)", itemList.Count);

                        foreach (var item in itemList)
                        {
                            if (item == null || userNotFoundException != null) continue;

                            try
                            {
                                var operand = OperandFactory.GetMediaType(libraryManager, item, user, userDataManager, UserManager, logger, new MediaTypeExtractionOptions
                                {
                                    ExtractAudioLanguages = needsAudioLanguages,
                                    ExtractAudioQuality = needsAudioQuality,
                                    ExtractVideoQuality = needsVideoQuality,
                                    ExtractPeople = needsPeople,
                                    ExtractCollections = needsCollections,
                                    ExtractPlaylists = needsPlaylists,
                                    ExtractNextUnwatched = needsNextUnwatched,
                                    ExtractSeriesName = needsSeriesName,
                                    ExtractParentSeriesTags = needsParentSeriesTags,
                                    ExtractParentSeriesStudios = needsParentSeriesStudios,
                                    ExtractParentSeriesGenres = needsParentSeriesGenres,
                                    IncludeUnwatchedSeries = includeUnwatchedSeries,
                                    AdditionalUserIds = additionalUserIds,
                                    OriginListName = Name,
                                }, refreshCache);

                                // Calculate similarity score if SimilarTo is active
                                bool passesSimilarity = true;
                                if (needsSimilarTo && referenceMetadata != null)
                                {
                                    passesSimilarity = OperandFactory.CalculateSimilarityScore(operand, referenceMetadata, similarityComparisonFields, logger);

                                    // Store similarity score for potential sorting
                                    if (operand.SimilarityScore.HasValue)
                                    {
                                        _similarityScores[item.Id] = operand.SimilarityScore.Value;
                                    }
                                }

                                bool matches = false;
                                List<int>? matchingGroups = null;
                                
                                if (!hasAnyRules)
                                {
                                    matches = true;
                                }
                                else if (compiledRules.All(set => set?.Count == 0))
                                {
                                    // Special case: Only SimilarTo or IncludeCollectionOnly rules (no compiled rules)
                                    // In this case, start with matches = true and let similarity filter decide
                                    matches = true;
                                }
                                else
                                {
                                    // Check if we need per-group tracking for limits or Rule Block Order sorting
                                    bool needsGroupTracking = NeedsGroupTracking();
                                    
                                    if (needsGroupTracking)
                                    {
                                        matchingGroups = GetMatchingGroupIndices(compiledRules, operand);
                                        matches = matchingGroups.Count > 0;
                                    }
                                    else
                                    {
                                        matches = EvaluateLogicGroups(compiledRules, operand);
                                    }
                                }

                                // Apply similarity filter
                                matches = matches && passesSimilarity;

                                if (matches)
                                {
                                    results.Add(item);
                                    
                                    // Track which groups this item matched for per-group limiting
                                    if (matchingGroups != null && matchingGroups.Count > 0)
                                    {
                                        _itemGroupMappings[item.Id] = matchingGroups;
                                    }
                                }
                                // Note: Series expansion logic is now handled in ExpandCollectionsBasedOnMediaType based on media type selection
                            }
                            catch (InvalidOperationException ex) when (ex.Message.Contains("User with ID") && ex.Message.Contains("not found"))
                            {
                                // User-specific rule references a user that no longer exists
                                logger?.LogWarning(ex, "Playlist '{PlaylistName}' references a user that no longer exists. Playlist processing will be skipped.", Name);
                                userNotFoundException = ex;
                                break; // Stop processing
                            }
                            catch (Exception ex)
                            {
                                logger?.LogDebug(ex, "Error processing item '{ItemName}' in expensive-only path. Skipping item.", item.Name);
                                // Skip this item and continue with others
                            }
                        }

                        // Re-throw original user not found exception to preserve message for catch filters
                        if (userNotFoundException != null)
                        {
                            throw userNotFoundException;
                        }

                        logger?.LogDebug("Processing complete (expensive-only path): {Count} items matched", results.Count);
                    }
                    else
                    {
                        // Two-phase filtering: non-expensive rules first, then expensive data extraction
                        logger?.LogDebug("Using two-phase filtering for expensive field optimization");

                        // Materialize items to prevent multiple enumerations
                        var itemList = items as IList<BaseItem> ?? items.ToList();

                        // First pass: Filter items using cheap rules only
                        var phase1Survivors = new List<BaseItem>();
                        var phase1SeriesMatches = new List<BaseItem>();
                        InvalidOperationException? userNotFoundException = null;

                        logger?.LogDebug("Processing {Count} items sequentially (Phase 1 cheap filtering)", itemList.Count);

                        foreach (var item in itemList)
                        {
                            if (item == null || userNotFoundException != null) continue;

                            try
                            {
                                // Special handling: For series when Collections expansion is enabled, check Collections rules first
                                bool shouldCheckCollectionsForSeries = item is Series && ShouldExpandEpisodesForCollections();

                                if (shouldCheckCollectionsForSeries)
                                {
                                    var series = (Series)item;

                                    if (DoesSeriesMatchCollectionsRules(series, libraryManager, user, userDataManager, logger, refreshCache))
                                    {
                                        logger?.LogDebug("Series '{SeriesName}' matches Collections rules - adding for expansion", series.Name);
                                        phase1SeriesMatches.Add(item);
                                    }
                                    continue;
                                }

                                // Phase 1: Extract non-expensive properties and check non-expensive rules
                                var cheapOperand = OperandFactory.GetMediaType(libraryManager, item, user, userDataManager, UserManager, logger, new MediaTypeExtractionOptions
                                {
                                    ExtractAudioLanguages = false,
                                    ExtractPeople = false,
                                    ExtractCollections = false,
                                    ExtractNextUnwatched = false,
                                    ExtractSeriesName = false,
                                    IncludeUnwatchedSeries = true,
                                    AdditionalUserIds = additionalUserIds,
                                    OriginListName = Name,
                                }, refreshCache);

                                // Check if item passes all non-expensive rules for any rule set that has non-expensive rules
                                bool passesNonExpensiveRules = false;
                                bool hasExpensiveOnlyRuleSets = false;

                                for (int setIndex = 0; setIndex < cheapCompiledRules.Count; setIndex++)
                                {
                                    try
                                    {
                                        // Check if this rule set has only expensive rules (no non-expensive rules)
                                        if (cheapCompiledRules[setIndex].Count == 0)
                                        {
                                            hasExpensiveOnlyRuleSets = true;
                                            continue; // Can't evaluate expensive-only rule sets in non-expensive phase
                                        }

                                        // Evaluate non-expensive rules for this rule set
                                        if (cheapCompiledRules[setIndex].All(rule => rule(cheapOperand)))
                                        {
                                            passesNonExpensiveRules = true;
                                            break;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        logger?.LogDebug(ex, "Error evaluating non-expensive rules for item '{ItemName}' in set {SetIndex}. Assuming rules don't match.", item.Name, setIndex);
                                        // Continue to next rule set
                                    }
                                }

                                // Only proceed to Phase 2 if:
                                // 1. Passed non-expensive evaluation OR
                                // 2. There are expensive-only rule sets that still need to be checked
                                if (passesNonExpensiveRules || hasExpensiveOnlyRuleSets)
                                {
                                    phase1Survivors.Add(item);
                                }
                            }
                            catch (InvalidOperationException ex) when (ex.Message.Contains("User with ID") && ex.Message.Contains("not found"))
                            {
                                logger?.LogWarning(ex, "Playlist '{PlaylistName}' references a user that no longer exists. Playlist processing will be skipped.", Name);
                                userNotFoundException = ex;
                                break; // Stop processing
                            }
                            catch (Exception ex)
                            {
                                logger?.LogDebug(ex, "Error in Phase 1 filtering for item '{ItemName}'. Skipping item.", item.Name);
                            }
                        }

                        // Merge series matches that bypass Phase 2 into results
                        results.AddRange(phase1SeriesMatches);

                        // Check and re-throw user not found exception before Phase 2
                        if (userNotFoundException != null)
                        {
                            throw userNotFoundException;
                        }

                        logger?.LogDebug("Phase 1 complete: {Survivors}/{Total} items passed cheap filtering ({SeriesMatches} series matches bypass Phase 2)",
                            phase1Survivors.Count, itemList.Count, phase1SeriesMatches.Count);

                        // Preload People cache for Phase 1 survivors if needed
                        if (needsPeople && phase1Survivors.Count > 0)
                        {
                            logger?.LogDebug("Preloading People cache for {Count} Phase 1 survivors", phase1Survivors.Count);
                            OperandFactory.PreloadPeopleCache(libraryManager, phase1Survivors, refreshCache, logger);
                        }

                        // Second pass: Process Phase 1 survivors with expensive data sequentially
                        userNotFoundException = null;
                        var debugItemCount = 0;

                        logger?.LogDebug("Processing {Count} Phase 1 survivors sequentially (Phase 2)", phase1Survivors.Count);

                        foreach (var item in phase1Survivors)
                        {
                            if (userNotFoundException != null) break;

                            try
                            {
                                // Phase 2: Extract expensive data and check complete rules
                                var fullOperand = OperandFactory.GetMediaType(libraryManager, item, user, userDataManager, UserManager, logger, new MediaTypeExtractionOptions
                                {
                                    ExtractAudioLanguages = needsAudioLanguages,
                                    ExtractAudioQuality = needsAudioQuality,
                                    ExtractVideoQuality = needsVideoQuality,
                                    ExtractPeople = needsPeople,
                                    ExtractCollections = needsCollections,
                                    ExtractPlaylists = needsPlaylists,
                                    ExtractNextUnwatched = needsNextUnwatched,
                                    ExtractSeriesName = needsSeriesName,
                                    ExtractParentSeriesTags = needsParentSeriesTags,
                                    ExtractParentSeriesStudios = needsParentSeriesStudios,
                                    ExtractParentSeriesGenres = needsParentSeriesGenres,
                                    IncludeUnwatchedSeries = includeUnwatchedSeries,
                                    AdditionalUserIds = additionalUserIds,
                                    OriginListName = Name,
                                }, refreshCache);

                                // Debug: Log expensive data found for first few items
                                bool shouldLog = debugItemCount < 5;
                                if (shouldLog)
                                {
                                    debugItemCount++;
                                    
                                    if (needsAudioLanguages)
                                    {
                                        logger?.LogDebug("Item '{Name}': Found {Count} audio languages: [{Languages}]",
                                            item.Name, fullOperand.AudioLanguages?.Count ?? 0, fullOperand.AudioLanguages != null ? string.Join(", ", fullOperand.AudioLanguages) : "none");
                                    }
                                    if (needsPeople)
                                    {
                                        logger?.LogDebug("Item '{Name}': Found {Count} people: [{People}]",
                                            item.Name, fullOperand.People?.Count ?? 0, fullOperand.People != null ? string.Join(", ", fullOperand.People.Take(5)) : "none");
                                    }
                                    if (needsCollections)
                                    {
                                        logger?.LogDebug("Item '{Name}': Found {Count} collections: [{Collections}]",
                                            item.Name, fullOperand.Collections?.Count ?? 0, fullOperand.Collections != null ? string.Join(", ", fullOperand.Collections) : "none");
                                    }
                                    if (needsNextUnwatched)
                                    {
                                        logger?.LogDebug("Item '{Name}': NextUnwatched status: {NextUnwatchedUsers}",
                                            item.Name, fullOperand.NextUnwatchedByUser?.Count > 0 ? string.Join(", ", fullOperand.NextUnwatchedByUser.Select(x => $"{x.Key}={x.Value}")) : "none");
                                    }
                                }

                                // Calculate similarity score if SimilarTo is active
                                bool passesSimilarity = true;
                                if (needsSimilarTo && referenceMetadata != null)
                                {
                                    passesSimilarity = OperandFactory.CalculateSimilarityScore(fullOperand, referenceMetadata, similarityComparisonFields, logger);

                                    // Store similarity score for potential sorting
                                    if (fullOperand.SimilarityScore.HasValue)
                                    {
                                        _similarityScores[item.Id] = fullOperand.SimilarityScore.Value;
                                    }
                                }

                                bool matches = false;
                                List<int>? matchingGroups = null;
                                
                                if (!hasAnyRules)
                                {
                                    matches = true;
                                }
                                else if (compiledRules.All(set => set?.Count == 0))
                                {
                                    // Special case: Only SimilarTo or IncludeCollectionOnly rules (no compiled rules)
                                    // In this case, start with matches = true and let similarity filter decide
                                    matches = true;
                                }
                                else
                                {
                                    // Check if we need per-group tracking for limits or Rule Block Order sorting
                                    bool needsGroupTracking = NeedsGroupTracking();
                                    
                                    if (needsGroupTracking)
                                    {
                                        matchingGroups = GetMatchingGroupIndices(compiledRules, fullOperand);
                                        matches = matchingGroups.Count > 0;
                                    }
                                    else
                                    {
                                        matches = EvaluateLogicGroups(compiledRules, fullOperand);
                                    }
                                }

                                // Apply similarity filter
                                matches = matches && passesSimilarity;

                                if (matches)
                                {
                                    results.Add(item);
                                    
                                    // Track which groups this item matched for per-group limiting
                                    if (matchingGroups != null && matchingGroups.Count > 0)
                                    {
                                        _itemGroupMappings[item.Id] = matchingGroups;
                                    }
                                }
                                // Note: Series expansion logic is now handled in ExpandCollectionsBasedOnMediaType based on media type selection
                            }
                            catch (InvalidOperationException ex) when (ex.Message.Contains("User with ID") && ex.Message.Contains("not found"))
                            {
                                // User-specific rule references a user that no longer exists
                                logger?.LogWarning(ex, "Playlist '{PlaylistName}' references a user that no longer exists. Playlist processing will be skipped.", Name);
                                userNotFoundException = ex;
                                break; // Stop processing
                            }
                            catch (Exception ex)
                            {
                                logger?.LogDebug(ex, "Error processing item '{ItemName}' in two-phase path. Skipping item.", item.Name);
                                // Skip this item and continue with others
                            }
                        }

                        // Re-throw original user not found exception to preserve message for catch filters
                        if (userNotFoundException != null)
                        {
                            throw userNotFoundException;
                        }

                        logger?.LogDebug("Processing complete (Phase 2): {Count} items matched", results.Count);
                    }
                }
                else
                {
                    // No expensive fields needed - use simple filtering
                    return ProcessItemsSimple(items, libraryManager, user, userDataManager, logger, needsAudioLanguages, needsAudioQuality, needsVideoQuality, needsPeople, needsCollections, needsPlaylists, needsNextUnwatched, needsSeriesName, needsParentSeriesTags, needsParentSeriesStudios, needsParentSeriesGenres, includeUnwatchedSeries, additionalUserIds, referenceMetadata, similarityComparisonFields, needsSimilarTo, compiledRules, hasAnyRules, refreshCache);
                }

                return results;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("User with ID") && ex.Message.Contains("not found"))
            {
                // User-specific rule references a user that no longer exists
                logger?.LogWarning(ex, "Playlist '{PlaylistName}' references a user that no longer exists. Playlist processing will be skipped.", Name);
                throw; // Re-throw to stop playlist processing entirely,
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Critical error in ProcessItemChunk. Returning partial results.");
                return results; // Return whatever we managed to process,
            }
        }

        /// <summary>
        /// Simple item processing fallback method with error handling.
        /// </summary>
        private List<BaseItem> ProcessItemsSimple(IEnumerable<BaseItem> items, ILibraryManager libraryManager,
            User user, IUserDataManager? userDataManager, ILogger? logger, bool needsAudioLanguages, bool needsAudioQuality, bool needsVideoQuality, bool needsPeople, bool needsCollections, bool needsPlaylists, bool needsNextUnwatched, bool needsSeriesName, bool needsParentSeriesTags, bool needsParentSeriesStudios, bool needsParentSeriesGenres, bool includeUnwatchedSeries,
            List<string> additionalUserIds, OperandFactory.ReferenceMetadata? referenceMetadata, List<string> similarityComparisonFields, bool needsSimilarTo,
            List<List<Func<Operand, bool>>> compiledRules, bool hasAnyRules, RefreshQueueService.RefreshCache refreshCache)
        {
            var results = new List<BaseItem>();

            // Materialize items to prevent multiple enumerations
            var itemList = items as IList<BaseItem> ?? items.ToList();

            // Preload People cache if needed for performance
            if (needsPeople)
            {
                logger?.LogDebug("Preloading People cache for simple processing ({Count} items)", itemList.Count);
                OperandFactory.PreloadPeopleCache(libraryManager, itemList, refreshCache, logger);
            }

            // Process items sequentially
            InvalidOperationException? userNotFoundException = null;

            logger?.LogDebug("Processing {Count} items sequentially (simple path)", itemList.Count);

            try
            {
                foreach (var item in itemList)
                {
                    if (item == null || userNotFoundException != null) continue;

                    try
                    {
                        var operand = OperandFactory.GetMediaType(libraryManager, item, user, userDataManager, UserManager, logger, new MediaTypeExtractionOptions
                        {
                            ExtractAudioLanguages = needsAudioLanguages,
                            ExtractAudioQuality = needsAudioQuality,
                            ExtractVideoQuality = needsVideoQuality,
                            ExtractPeople = needsPeople,
                            ExtractCollections = needsCollections,
                            ExtractPlaylists = needsPlaylists,
                            ExtractNextUnwatched = needsNextUnwatched,
                            ExtractSeriesName = needsSeriesName,
                            ExtractParentSeriesTags = needsParentSeriesTags,
                            ExtractParentSeriesStudios = needsParentSeriesStudios,
                            ExtractParentSeriesGenres = needsParentSeriesGenres,
                            IncludeUnwatchedSeries = includeUnwatchedSeries,
                            AdditionalUserIds = additionalUserIds,
                            OriginListName = Name,
                        }, refreshCache);

                        // Check similarity first if SimilarTo is active
                        bool passesSimilarity = true;
                        if (needsSimilarTo && referenceMetadata != null)
                        {
                            passesSimilarity = OperandFactory.CalculateSimilarityScore(operand, referenceMetadata, similarityComparisonFields, logger);
                            if (operand.SimilarityScore.HasValue)
                            {
                                _similarityScores[item.Id] = operand.SimilarityScore.Value;
                            }
                        }

                        bool matches = false;
                        List<int>? matchingGroups = null;
                        
                        if (!hasAnyRules)
                        {
                            matches = true;
                        }
                        else if (compiledRules.All(set => set?.Count == 0))
                        {
                            // Special case: Only SimilarTo or IncludeCollectionOnly rules (no compiled rules)
                            // In this case, start with matches = true and let similarity filter decide
                            matches = true;
                        }
                        else
                        {
                            // Check if we need per-group tracking for limits or Rule Block Order sorting
                            bool needsGroupTracking = NeedsGroupTracking();
                            
                            if (needsGroupTracking)
                            {
                                matchingGroups = GetMatchingGroupIndices(compiledRules, operand);
                                matches = matchingGroups.Count > 0;
                            }
                            else
                            {
                                matches = EvaluateLogicGroups(compiledRules, operand);
                            }
                        }

                        // Apply similarity filter
                        matches = matches && passesSimilarity;

                        if (matches)
                        {
                            results.Add(item);
                            
                            // Track which groups this item matched for per-group limiting
                            if (matchingGroups != null && matchingGroups.Count > 0)
                            {
                                _itemGroupMappings[item.Id] = matchingGroups;
                            }
                        }
                        else if (item is Series series && ShouldExpandEpisodesForCollections())
                        {
                            logger?.LogDebug("Series '{SeriesName}' failed other rules but checking Collections rules for expansion", series.Name);
                            // For series that don't match other rules, check if they match Collections rules for expansion

                            if (DoesSeriesMatchCollectionsRules(series, operand, logger))
                            {
                                logger?.LogDebug("Series '{SeriesName}' matches Collections rules for expansion - will expand and filter episodes", series.Name);
                                results.Add(item);
                            }
                        }
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("User with ID") && ex.Message.Contains("not found"))
                    {
                        // User-specific rule references a user that no longer exists
                        logger?.LogWarning(ex, "Playlist '{PlaylistName}' references a user that no longer exists. Playlist processing will be skipped.", Name);
                        userNotFoundException = ex;
                        break; // Stop processing
                    }
                    catch (Exception ex)
                    {
                        logger?.LogDebug(ex, "Error processing item '{ItemName}' in simple path. Skipping item.", item.Name);
                        // Skip this item and continue with others
                    }
                }

                // Re-throw original user not found exception to preserve message for catch filters
                if (userNotFoundException != null)
                {
                    throw userNotFoundException;
                }

                logger?.LogDebug("Processing complete (simple path): {Count} items matched", results.Count);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("User with ID") && ex.Message.Contains("not found"))
            {
                // User-specific rule references a user that no longer exists
                logger?.LogWarning(ex, "Playlist '{PlaylistName}' references a user that no longer exists. Playlist processing will be skipped.", Name);
                throw; // Re-throw to stop playlist processing entirely,
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Critical error in ProcessItemsSimple. Returning partial results.");
            }

            return results;
        }

        // private static void Validate()
        // {
        //     // Future enhancement: Add validation for constructor input
        // }
    }

    public static class OrderFactory
    {
        private static readonly Dictionary<string, Func<Order>> OrderMap = new()
        {
            { "Name Ascending", () => new NameOrder() },
            { "Name Descending", () => new NameOrderDesc() },
            { "Name (Ignore Articles) Ascending", () => new NameIgnoreArticlesOrder() },
            { "Name (Ignore Articles) Descending", () => new NameIgnoreArticlesOrderDesc() },
            { "ProductionYear Ascending", () => new ProductionYearOrder() },
            { "ProductionYear Descending", () => new ProductionYearOrderDesc() },
            { "DateCreated Ascending", () => new DateCreatedOrder() },
            { "Similarity Ascending", () => new SimilarityOrderAsc() },
            { "Similarity Descending", () => new SimilarityOrder() },
            { "DateCreated Descending", () => new DateCreatedOrderDesc() },
            { "ReleaseDate Ascending", () => new ReleaseDateOrder() },
            { "ReleaseDate Descending", () => new ReleaseDateOrderDesc() },
            { "CommunityRating Ascending", () => new CommunityRatingOrder() },
            { "CommunityRating Descending", () => new CommunityRatingOrderDesc() },
            { "PlayCount (owner) Ascending", () => new PlayCountOrder() },
            { "PlayCount (owner) Descending", () => new PlayCountOrderDesc() },
            { "LastPlayed (owner) Ascending", () => new LastPlayedOrder() },
            { "LastPlayed (owner) Descending", () => new LastPlayedOrderDesc() },
            { "Runtime Ascending", () => new RuntimeOrder() },
            { "Runtime Descending", () => new RuntimeOrderDesc() },
            { "SeriesName Ascending", () => new SeriesNameOrder() },
            { "SeriesName Descending", () => new SeriesNameOrderDesc() },
            { "SeriesName (Ignore Articles) Ascending", () => new SeriesNameIgnoreArticlesOrder() },
            { "SeriesName (Ignore Articles) Descending", () => new SeriesNameIgnoreArticlesOrderDesc() },
            { "AlbumName Ascending", () => new AlbumNameOrder() },
            { "AlbumName Descending", () => new AlbumNameOrderDesc() },
            { "Artist Ascending", () => new ArtistOrder() },
            { "Artist Descending", () => new ArtistOrderDesc() },
            { "TrackNumber Ascending", () => new TrackNumberOrder() },
            { "TrackNumber Descending", () => new TrackNumberOrderDesc() },
            { "SeasonNumber Ascending", () => new SeasonNumberOrder() },
            { "SeasonNumber Descending", () => new SeasonNumberOrderDesc() },
            { "EpisodeNumber Ascending", () => new EpisodeNumberOrder() },
            { "EpisodeNumber Descending", () => new EpisodeNumberOrderDesc() },
            { "Random", () => new RandomOrder() },
            { "Rule Block Order Ascending", () => new Orders.RuleBlockOrder() },
            { "Rule Block Order Descending", () => new Orders.RuleBlockOrderDesc() },
            { "NoOrder", () => new NoOrder() },
        };

        public static Order CreateOrder(string orderName)
        {
            return OrderMap.TryGetValue(orderName ?? "", out var factory)
                ? factory()
                : new NoOrder();
        }
    }

    /// <summary>
    /// Helper class to analyze field requirements from expression sets.
    /// </summary>
    public class FieldRequirements
    {
        public bool NeedsAudioLanguages { get; set; }
        public bool NeedsAudioQuality { get; set; }
        public bool NeedsVideoQuality { get; set; }
        public bool NeedsPeople { get; set; }
        public bool NeedsCollections { get; set; }
        public bool NeedsPlaylists { get; set; }
        public bool NeedsNextUnwatched { get; set; }
        public bool NeedsSeriesName { get; set; }
        public bool NeedsParentSeriesTags { get; set; }
        public bool NeedsParentSeriesStudios { get; set; }
        public bool NeedsParentSeriesGenres { get; set; }
        public bool NeedsSimilarTo { get; set; }
        public bool IncludeUnwatchedSeries { get; set; } = true;
        public List<string> AdditionalUserIds { get; set; } = [];
        public List<Expression> SimilarToExpressions { get; set; } = [];

        /// <summary>
        /// Analyzes expression sets to determine field requirements.
        /// </summary>
        /// <param name="expressionSets">Filter expression sets to analyze</param>
        /// <param name="orders">Optional sorting orders to check for field requirements</param>
        public static FieldRequirements Analyze(List<ExpressionSet> expressionSets, List<Order>? orders = null)
        {
            var requirements = new FieldRequirements();

            if (expressionSets == null) return requirements;

            requirements.NeedsAudioLanguages = expressionSets
                .SelectMany(set => set?.Expressions ?? [])
                .Any(expr => expr?.MemberName == "AudioLanguages");

            // Check if any rules use audio quality fields (expensive operations)
            var audioQualityFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AudioBitrate", "AudioSampleRate", "AudioBitDepth", "AudioCodec", "AudioProfile", "AudioChannels",
            };
            requirements.NeedsAudioQuality = expressionSets
                .SelectMany(set => set?.Expressions ?? [])
                .Any(expr => expr?.MemberName != null && audioQualityFields.Contains(expr.MemberName));

            // Check if any rules use video quality fields (expensive operations)
            var videoQualityFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Resolution", "Framerate", "VideoCodec", "VideoProfile", "VideoRange", "VideoRangeType",
            };
            requirements.NeedsVideoQuality = expressionSets
                .SelectMany(set => set?.Expressions ?? [])
                .Any(expr => expr?.MemberName != null && videoQualityFields.Contains(expr.MemberName));

            requirements.NeedsPeople = expressionSets
                .SelectMany(set => set?.Expressions ?? [])
                .Any(expr => expr?.MemberName != null && FieldDefinitions.IsPeopleField(expr.MemberName));

            requirements.NeedsCollections = expressionSets
                .SelectMany(set => set?.Expressions ?? [])
                .Any(expr => expr?.MemberName == "Collections");

            requirements.NeedsPlaylists = expressionSets
                .SelectMany(set => set?.Expressions ?? [])
                .Any(expr => expr?.MemberName == "Playlists");

            requirements.NeedsNextUnwatched = expressionSets
                .SelectMany(set => set?.Expressions ?? [])
                .Any(expr => expr?.MemberName == "NextUnwatched");

            requirements.NeedsSeriesName = expressionSets
                .SelectMany(set => set?.Expressions ?? [])
                .Any(expr => expr?.MemberName == "SeriesName");

            // Also check if SeriesName is used in sorting
            if (orders != null && !requirements.NeedsSeriesName)
            {
                requirements.NeedsSeriesName = orders.Any(o => 
                    o.Name.Contains("SeriesName", StringComparison.OrdinalIgnoreCase));
            }

            // Check if any Tags rule has IncludeParentSeriesTags = true
            requirements.NeedsParentSeriesTags = expressionSets
                .SelectMany(set => set?.Expressions ?? [])
                .Any(expr => expr?.MemberName == "Tags" && expr.IncludeParentSeriesTags == true);

            // Check if any Studios rule has IncludeParentSeriesStudios = true
            requirements.NeedsParentSeriesStudios = expressionSets
                .SelectMany(set => set?.Expressions ?? [])
                .Any(expr => expr?.MemberName == "Studios" && expr.IncludeParentSeriesStudios == true);

            // Check if any Genres rule has IncludeParentSeriesGenres = true
            requirements.NeedsParentSeriesGenres = expressionSets
                .SelectMany(set => set?.Expressions ?? [])
                .Any(expr => expr?.MemberName == "Genres" && expr.IncludeParentSeriesGenres == true);

            // Check if any rules use SimilarTo field
            requirements.NeedsSimilarTo = expressionSets
                .SelectMany(set => set?.Expressions ?? [])
                .Any(expr => expr?.MemberName == "SimilarTo");

            // Extract SimilarTo expressions for reference item lookup
            requirements.SimilarToExpressions = [.. expressionSets
                .SelectMany(set => set?.Expressions ?? [])
                .Where(expr => expr?.MemberName == "SimilarTo")];

            // Extract IncludeUnwatchedSeries parameter from NextUnwatched rules
            requirements.IncludeUnwatchedSeries = expressionSets
                .SelectMany(set => set?.Expressions ?? [])
                .Where(e => e?.MemberName == "NextUnwatched")
                .All(e => e.IncludeUnwatchedSeries != false);

            // Extract additional user IDs from user-specific rules
            requirements.AdditionalUserIds = [.. expressionSets
                .SelectMany(set => set?.Expressions ?? [])
                .Where(e => !string.IsNullOrEmpty(e?.UserId))
                .Select(e => e.UserId!)
                .Distinct()];

            return requirements;
        }
    }

    /// <summary>
    /// Utility class for shared ordering operations
    /// </summary>
    public static class OrderUtilities
    {
        /// <summary>
        /// Shared natural string comparer instance for case-insensitive sorting with numeric awareness.
        /// </summary>
        public static readonly NaturalStringComparer SharedNaturalComparer = new(ignoreCase: true);

        /// <summary>
        /// Natural string comparer that sorts strings with leading numbers numerically.
        /// </summary>
        public class NaturalStringComparer : IComparer<string>
        {
            private readonly bool _ignoreCase;

            public NaturalStringComparer(bool ignoreCase = true)
            {
                _ignoreCase = ignoreCase;
            }

            public int Compare(string? x, string? y)
            {
                if (x == y) return 0;
                if (x == null) return -1;
                if (y == null) return 1;

                // Extract leading numbers
                var (xNum, xHasNum, xRest) = ExtractLeadingNumber(x);
                var (yNum, yHasNum, yRest) = ExtractLeadingNumber(y);

                // If both have leading numbers, compare them numerically first
                if (xHasNum && yHasNum)
                {
                    var numComparison = xNum.CompareTo(yNum);
                    if (numComparison != 0)
                    {
                        return numComparison;
                    }
                    // Numbers are equal, compare the rest of the string
                    return CompareStrings(xRest, yRest);
                }

                // If only one has a leading number, put numbered items first
                if (xHasNum) return -1;
                if (yHasNum) return 1;

                // Neither has a leading number, do normal string comparison
                return CompareStrings(x, y);
            }

            private static (int number, bool hasNumber, string rest) ExtractLeadingNumber(string str)
            {
                int i = 0;

                // Skip leading whitespace
                while (i < str.Length && char.IsWhiteSpace(str[i]))
                {
                    i++;
                }

                if (i >= str.Length || !char.IsDigit(str[i]))
                {
                    return (0, false, str);
                }

                int startDigit = i;

                // Parse the number
                while (i < str.Length && char.IsDigit(str[i]))
                {
                    i++;
                }

                var numberStr = str.Substring(startDigit, i - startDigit);
                if (int.TryParse(numberStr, out int number))
                {
                    return (number, true, str.Substring(i));
                }

                return (0, false, str);
            }

            private int CompareStrings(string x, string y)
            {
                return _ignoreCase
                    ? string.Compare(x, y, StringComparison.OrdinalIgnoreCase)
                    : string.Compare(x, y, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Gets the release date for a BaseItem by checking the PremiereDate property
        /// </summary>
        /// <param name="item">The BaseItem to get the release date for</param>
        /// <returns>The release date or DateTime.MinValue if not available</returns>
        public static DateTime GetReleaseDate(BaseItem item)
        {
            var unixTimestamp = DateUtils.GetReleaseDateUnixTimestamp(item);
            if (unixTimestamp > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds((long)unixTimestamp).DateTime;
            }
            return DateTime.MinValue;
        }

        /// <summary>
        /// Gets the season number for an episode
        /// </summary>
        /// <param name="item">The BaseItem to get the season number for</param>
        /// <returns>The season number or 0 if not available or not an episode</returns>
        public static int GetSeasonNumber(BaseItem item)
        {
            return item is Episode episode
                ? (episode.ParentIndexNumber ?? 0)
                : 0;
        }

        /// <summary>
        /// Gets the episode number for an episode
        /// </summary>
        /// <param name="item">The BaseItem to get the episode number for</param>
        /// <returns>The episode number or 0 if not available or not an episode</returns>
        public static int GetEpisodeNumber(BaseItem item)
        {
            return item is Episode episode
                ? (episode.IndexNumber ?? 0)
                : 0;
        }

        /// <summary>
        /// Checks if a BaseItem is an episode
        /// </summary>
        /// <param name="item">The BaseItem to check</param>
        /// <returns>True if the item is an episode, false otherwise</returns>
        public static bool IsEpisode(BaseItem item)
        {
            return item is Episode;
        }

        /// <summary>
        /// Gets the disc number for an audio item
        /// </summary>
        /// <param name="item">The BaseItem to get the disc number for</param>
        /// <returns>The disc number or 0 if not available</returns>
        public static int GetDiscNumber(BaseItem item)
        {
            // For audio items, ParentIndexNumber represents the disc number
            return item.ParentIndexNumber ?? 0;
        }

        /// <summary>
        /// Gets the track number for an audio item
        /// </summary>
        /// <param name="item">The BaseItem to get the track number for</param>
        /// <returns>The track number or 0 if not available</returns>
        public static int GetTrackNumber(BaseItem item)
        {
            // For audio items, IndexNumber represents the track number
            return item.IndexNumber ?? 0;
        }

        /// <summary>
        /// Common articles in multiple languages to strip from names during sorting
        /// </summary>
        private static readonly string[] Articles =
        [
            "the"//, "a", "an",           // English
            //"le", "la", "les", "l'",    // French
            //"el", "la", "los", "las",   // Spanish
            //"der", "die", "das",        // German
            //"il", "lo", "la", "i", "gli", "le", // Italian
            //"de", "het",                // Dutch
            //"o", "a", "os", "as",       // Portuguese
            //"en", "ett",                // Swedish
            //"en", "ei", "et"            // Norwegian
        ];

        /// <summary>
        /// Strips leading articles from a name for sorting purposes.
        /// Supports article 'The'.
        /// </summary>
        /// <param name="name">The name to process</param>
        /// <returns>The name with leading article removed, or original name if no article found</returns>
        public static string StripLeadingArticles(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return name ?? "";
            }

            var trimmedName = name.Trim();

            foreach (var article in Articles)
            {
                // Check if name starts with article followed by a space or apostrophe
                if (trimmedName.StartsWith(article + " ", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmedName.Substring(article.Length + 1).TrimStart();
                }

                // Special handling for l' (French)
                // if (article.EndsWith("'") && trimmedName.StartsWith(article, StringComparison.OrdinalIgnoreCase))
                // {
                //     return trimmedName.Substring(article.Length).TrimStart();
                // }
            }

            return trimmedName;
        }
    }
}
