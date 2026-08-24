using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.Orders;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters;
using Jellyfin.Plugin.SmartLists.Services.ExternalList;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core
{
    public class SmartList
    {
        public string Id { get; set; } = null!;
        public string Name { get; set; } = null!;
        public ListOrigin Origin { get; private set; }  // Identifies this list so it stays out of its own Collections/Playlists results
        public string? FileName { get; set; }
        public Guid UserId { get; set; }
        public List<Order> Orders { get; set; }
        public List<SortOption>? SortOptions { get; set; }  // Original sort options (legacy: UseChildValues flag removed)
        public List<string>? MediaTypes { get; set; }
        public int CollectionSearchDepth { get; set; }  // Depth for traversing nested collections/playlists (0 = no recursion, 1-10 = levels)
        public bool MatchByMembers { get; set; }  // Container candidates (Collection/Playlist media types) match when at least one member item passes the rules
        public List<ExpressionSet> ExpressionSets { get; set; }
        public int MaxItems { get; set; }
        public int MaxPlayTimeMinutes { get; set; }
        public RandomGroupSelectionDto? RandomGroupSelection { get; set; }
        public List<string>? SimilarityComparisonFields { get; set; }

        // UserManager for resolving user-specific queries (Jellyfin 10.11+)
        public IUserManager UserManager { get; set; } = null!;

        // Item repository for the DB prefilter's ItemValues-backed name dumps; optional -
        // dump-dependent pushdowns degrade conservatively (stay per-item) when absent.
        public MediaBrowser.Controller.Persistence.IItemRepository? ItemRepository { get; set; }

        // Similarity scores for sorting (populated during filtering when SimilarTo rules are active)
        private readonly ConcurrentDictionary<Guid, float> _similarityScores = new();

        // Item-to-group mappings for per-group limiting (populated during filtering when per-group MaxItems are set)
        private readonly ConcurrentDictionary<Guid, List<int>> _itemGroupMappings = new();

        // Playlist user IDs (normalized GUID strings), used by aggregate user-based sorts.
        private readonly List<string> _playlistUserIds = [];

        // OPTIMIZATION: Static cache for compiled rules to avoid recompilation
        private static readonly ConcurrentDictionary<string, List<List<Func<Operand, bool>>>> _ruleCache = new();

        // Cache management constants and fields
        private const int MAX_CACHE_SIZE = 1000; // Maximum number of cached rule sets
        private const int CLEANUP_THRESHOLD = 800; // Clean up when cache exceeds this size
        private static readonly object _cacheCleanupLock = new();
        private static DateTime _lastCleanupTime = DateTime.MinValue;
        private static readonly TimeSpan MIN_CLEANUP_INTERVAL = TimeSpan.FromMinutes(5); // Minimum time between cleanups

        private static bool IsNonExpensiveExpression(Expression? expr)
        {
            return expr != null
                && !FieldRegistry.IsExpensiveField(expr.MemberName)
                && !IsParentAwareListExpression(expr);
        }

        internal static bool IsParentAwareListExpression(Expression expr)
        {
            return (expr.MemberName == "Tags" && (expr.IncludeParentTagsEffective || expr.OnlyParentTags == true)) ||
                   (expr.MemberName == "Studios" && (expr.IncludeParentStudiosEffective || expr.OnlyParentStudios == true)) ||
                   (expr.MemberName == "Genres" && (expr.IncludeParentGenresEffective || expr.OnlyParentGenres == true));
        }

        public SmartList(SmartPlaylistDto dto)
        {
            ArgumentNullException.ThrowIfNull(dto);

            Id = dto.Id ?? throw new ArgumentException("Playlist ID cannot be null", nameof(dto));
            Name = dto.Name;
            FileName = dto.FileName ?? $"{dto.Id}.json";
            // DEPRECATED: dto.UserId is for backwards compatibility with old single-user playlists.
            // It is planned to be removed in version 10.12. Use UserPlaylists array instead.
            UserId = Guid.TryParse(dto.UserId, out var userId) ? userId : Guid.Empty;
            Origin = new ListOrigin(Id, CollectJellyfinPlaylistIds(dto));

            // Initialize properties before calling InitializeFromDto
            Orders = [];
            ExpressionSets = [];

            InitializeFromDto(dto);

            _playlistUserIds = CollectPlaylistUserIds(dto);
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
            Origin = new ListOrigin(Id, CollectJellyfinCollectionIds(dto));

            // Initialize properties before calling InitializeFromDto
            Orders = [];
            ExpressionSets = [];

            InitializeFromDto(dto);

            if (Guid.TryParse(dto.UserId, out var ownerUserId) && ownerUserId != Guid.Empty)
            {
                _playlistUserIds = [ownerUserId.ToString("N")];
            }
        }

        private static List<string> CollectPlaylistUserIds(SmartPlaylistDto dto)
        {
            var userIds = new List<string>();

            if (dto.UserPlaylists != null)
            {
                foreach (var mapping in dto.UserPlaylists)
                {
                    if (!string.IsNullOrEmpty(mapping.UserId) && Guid.TryParse(mapping.UserId, out var userId) && userId != Guid.Empty)
                    {
                        userIds.Add(userId.ToString("N"));
                    }
                }
            }

            if (userIds.Count == 0 && !string.IsNullOrEmpty(dto.UserId) && Guid.TryParse(dto.UserId, out var legacyUserId) && legacyUserId != Guid.Empty)
            {
                userIds.Add(legacyUserId.ToString("N"));
            }

            return [.. userIds.Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        /// <summary>
        /// Every Jellyfin playlist id that IS this smart list. An AllUsers/multi-user playlist has one
        /// Jellyfin playlist per user, and each of them is a copy of this same list, so all of them must
        /// be excluded from this list's own Playlists results.
        /// </summary>
        private static IEnumerable<Guid> CollectJellyfinPlaylistIds(SmartPlaylistDto dto)
        {
            if (Guid.TryParse(dto.JellyfinPlaylistId, out var legacyId) && legacyId != Guid.Empty)
            {
                yield return legacyId;
            }

            if (dto.UserPlaylists != null)
            {
                foreach (var mapping in dto.UserPlaylists)
                {
                    if (Guid.TryParse(mapping.JellyfinPlaylistId, out var userPlaylistId) && userPlaylistId != Guid.Empty)
                    {
                        yield return userPlaylistId;
                    }
                }
            }
        }

        /// <summary>
        /// The Jellyfin collection (BoxSet) id that IS this smart list, if it has been created yet.
        /// </summary>
        private static IEnumerable<Guid> CollectJellyfinCollectionIds(SmartCollectionDto dto)
        {
            if (Guid.TryParse(dto.JellyfinCollectionId, out var collectionId) && collectionId != Guid.Empty)
            {
                yield return collectionId;
            }
        }

        private void InitializeFromDto(SmartListDto dto)
        {

            // Handle both legacy single Order and new multiple Orders formats
            if (dto.Order?.SortOptions != null && dto.Order.SortOptions.Count > 0)
            {
                // Store original sort options for child value aggregation support
                SortOptions = new List<SortOption>(dto.Order.SortOptions);

                // New format: multiple sort options
                Orders = dto.Order.SortOptions
                    .Select(so =>
                    {
                        // Directionless sorts (no Ascending/Descending variants) are registered in the
                        // OrderMap under their bare name; all other sorts append the sort order
                        var order = OrderFactory.IsDirectionless(so.SortBy)
                            ? OrderFactory.CreateOrder(so.SortBy)
                            : OrderFactory.CreateOrder($"{so.SortBy} {so.SortOrder.ToString()}");

                        if (order is RoundRobinBase rr)
                        {
                            rr.GroupByField = so.GroupByField;
                            rr.OrderWithinGroupsByAirDate = string.Equals(so.WithinGroupOrder, "AirDate", StringComparison.OrdinalIgnoreCase);
                            rr.AirBlockWindowDays = so.AirBlockWindowDays ?? RoundRobinBase.DefaultAirBlockWindowDays;
                        }

                        return order;
                    })
                    .Where(o => o != null)
                    .ToList();
            }
            else if (!string.IsNullOrEmpty(dto.Order?.Name))
            {
                // Legacy format: single order by name
                Orders = [OrderFactory.CreateOrder(dto.Order.Name)];
                SortOptions = null; // No SortOptions for legacy format
            }
            else
            {
                // No order specified, use NoOrder
                Orders = [new NoOrder()];
                SortOptions = null;
            }

            MediaTypes = dto.MediaTypes != null ? new List<string>(dto.MediaTypes) : null; // Create defensive copy to prevent corruption
            MatchByMembers = dto.MatchByMembers;
            MaxItems = dto.MaxItems ?? 0; // Default to 0 (unlimited) for backwards compatibility
            MaxPlayTimeMinutes = dto.MaxPlayTimeMinutes ?? 0; // Default to 0 (unlimited) for backwards compatibility
            RandomGroupSelection = dto.RandomGroupSelection;
            SimilarityComparisonFields = dto.SimilarityComparisonFields != null ? new List<string>(dto.SimilarityComparisonFields) : null; // Create defensive copy

            if (dto.ExpressionSets != null && dto.ExpressionSets.Count > 0)
            {
                ExpressionSets = Engine.FixRuleSets(dto.ExpressionSets);
            }
            else
            {
                ExpressionSets = [];
            }

            // Extract CollectionSearchDepth from the first Collections expression that has it set
            // This is now stored per-rule instead of list-level for more granular control
            CollectionSearchDepth = ExtractCollectionSearchDepthFromExpressions(dto.ExpressionSets) ?? 0;

            // Resolve "Default" sort (NoOrder) based on the list's rules
            ResolveDefaultOrder();
        }

        /// <summary>
        /// When the sort is "Default" (NoOrder), auto-resolve to an appropriate sort
        /// based on the list's rules: External List Order for external list rules,
        /// Similarity for Similar To rules, or Name as fallback.
        /// </summary>
        private void ResolveDefaultOrder()
        {
            // Only resolve if the sole order is NoOrder (the "Default" sort)
            if (Orders == null || Orders.Count != 1 || Orders[0] is not NoOrder)
            {
                return;
            }

            var hasExternalList = ExpressionSets?
                .SelectMany(set => set?.Expressions ?? [])
                .Any(expr => expr?.MemberName == "ExternalList") == true;

            if (hasExternalList)
            {
                Orders = [new ExternalListOrder()];
                SortOptions = [new SortOption { SortBy = "External List Order", SortOrder = SortOrder.Ascending }];
                return;
            }

            var hasSimilarTo = ExpressionSets?
                .SelectMany(set => set?.Expressions ?? [])
                .Any(expr => expr?.MemberName == "SimilarTo") == true;

            if (hasSimilarTo)
            {
                Orders = [new SimilarityOrder()];
                SortOptions = [new SortOption { SortBy = "Similarity", SortOrder = SortOrder.Descending }];
                return;
            }

            // Fallback: resolve to Name Ascending
            Orders = [new NameOrder()];
            SortOptions = [new SortOption { SortBy = "Name", SortOrder = SortOrder.Ascending }];
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
                        hashBuilder.Append(expr.IncludeParentTagsEffective);
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.OnlyParentTags?.ToString() ?? "null");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.IncludeParentStudiosEffective);
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.OnlyParentStudios?.ToString() ?? "null");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.IncludeParentGenresEffective);
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.OnlyParentGenres?.ToString() ?? "null");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.IncludeCollectionOnly?.ToString() ?? "null");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.IncludePlaylistOnly?.ToString() ?? "null");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.OnlyDefaultAudioLanguage?.ToString() ?? "null");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.RuntimeUnit ?? "");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.IncludeUnwatchedSeries?.ToString() ?? "null");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.IncludeEpisodesWithinSeries?.ToString() ?? "null");
                        hashBuilder.Append(':');
                        hashBuilder.Append(expr.IncludeUnknownDates?.ToString() ?? "null");
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
        ///
        /// Similarity is part of a group's own AND, not a filter over the whole list: a group
        /// carrying SimilarTo rules matches only when its compiled rules pass AND the item is
        /// similar to THAT group's references. A group holding nothing but SimilarTo rules has no
        /// compiled rules, so similarity alone decides it. <paramref name="bestSimilarityScore"/>
        /// returns the highest score among the groups that matched - the score the item is sorted by.
        /// </summary>
        private List<int> GetMatchingGroupIndices(
            List<List<Func<Operand, bool>>> compiledRules,
            Operand operand,
            IReadOnlyDictionary<int, OperandFactory.ReferenceMetadata>? groupReferenceMetadata,
            List<string> similarityComparisonFields,
            ILogger? logger,
            out float bestSimilarityScore)
        {
            var matchingGroups = new List<int>();
            bestSimilarityScore = 0f;

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

                    OperandFactory.ReferenceMetadata? groupMetadata = null;
                    if (groupReferenceMetadata != null && groupReferenceMetadata.TryGetValue(groupIndex, out var metadata))
                    {
                        groupMetadata = metadata;
                    }

                    if ((groupRules == null || groupRules.Count == 0) && groupMetadata == null)
                        continue; // Skip empty rule groups

                    try
                    {
                        bool groupMatches = groupRules == null || groupRules.All(rule =>
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

                        // Similarity runs last: it is the expensive half of the group's AND.
                        if (groupMatches && groupMetadata != null)
                        {
                            groupMatches = OperandFactory.CalculateSimilarityScore(operand, groupMetadata, similarityComparisonFields, logger);

                            // CalculateSimilarityScore overwrites operand.SimilarityScore on every
                            // call, so the max is accumulated here rather than read back afterwards.
                            if (groupMatches && operand.SimilarityScore > bestSimilarityScore)
                            {
                                bestSimilarityScore = operand.SimilarityScore.Value;
                            }
                        }

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

        private bool EvaluateLogicGroups(
            List<List<Func<Operand, bool>>> compiledRules,
            Operand operand,
            IReadOnlyDictionary<int, OperandFactory.ReferenceMetadata>? groupReferenceMetadata,
            List<string> similarityComparisonFields,
            ILogger? logger,
            out float bestSimilarityScore)
        {
            // For backward compatibility and simple OR logic, check if any group matches
            var matchingGroups = GetMatchingGroupIndices(compiledRules, operand, groupReferenceMetadata, similarityComparisonFields, logger, out bestSimilarityScore);
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

                // Container candidates (Collection/Playlist media types) arrive through the normal
                // media pool. A list must never include its own container, in either MatchByMembers
                // mode - drop it from the pool before any evaluation (self-reference guard, #499).
                var hasContainerTypes = MediaTypes?.Any(Constants.MediaTypes.IsContainerType) == true;
                BaseItem[] containerCandidates = [];
                if (hasContainerTypes)
                {
                    itemsArray = [.. itemsArray.Where(i => !(IsContainerKind(i) && Origin.Matches(i)))];

                    if (MatchByMembers)
                    {
                        // Containers are matched by their member items instead of their own metadata -
                        // pull them out of the pool and project member matches back onto them later
                        containerCandidates = [.. itemsArray.Where(IsContainerKind)];
                        itemsArray = [.. itemsArray.Where(i => !IsContainerKind(i))];
                    }
                }

                var itemCount = itemsArray.Length;

                logger?.LogDebug("FilterPlaylistItems called with {ItemCount} items, ExpressionSets={ExpressionSetCount}, MediaTypes={MediaTypes}",
                    itemCount, ExpressionSets?.Count ?? 0, MediaTypes != null ? string.Join(",", MediaTypes) : "None");

                // Early return for empty item collections (container candidates still need matching)
                if (itemCount == 0 && containerCandidates.Length == 0)
                {
                    logger?.LogDebug("No items to filter for playlist '{PlaylistName}'", Name);
                    return [];
                }

                // Round Robin sorts may need maps computed from the UNFILTERED pool before filtering
                // (same injection pattern as SimilarityOrder.Scores / RuleBlockOrder.GroupMappings):
                // - "Collections" grouping: item id -> collection name (episodes resolve via their series).
                // - Least Recently Watched: per-group watch recency. Rules like "Playback Status is
                //   Unwatched" remove watched items from the results, so recency derived from filtered
                //   items would see every group as unwatched.
                Dictionary<Guid, string>? collectionGroupKeys = null;
                if (Orders.OfType<RoundRobinBase>().Any(o => o.GroupByField == "Collections"))
                {
                    collectionGroupKeys = BuildCollectionGroupKeyMap(itemsArray, user, libraryManager, refreshCache, logger);
                    foreach (var rrOrder in Orders.OfType<RoundRobinBase>())
                    {
                        if (rrOrder.GroupByField == "Collections")
                        {
                            rrOrder.CollectionGroupKeys = collectionGroupKeys;
                        }
                    }
                }

                foreach (var lrwOrder in Orders.OfType<RoundRobinLeastRecentlyWatchedOrder>())
                {
                    // Reads the order's own GroupByField/CollectionGroupKeys (injected above) and,
                    // when air blocks are active, collects the state for the mid-block hold that
                    // runs later in PreComputePositions against the filtered items.
                    lrwOrder.BuildGroupRecencyAndHoldState(itemsArray, user, userDataManager, refreshCache, logger);
                }

                // Media type filtering is now handled at the API level in PlaylistService.GetAllUserMedia()
                // This provides significant performance improvements by filtering at the database level
                logger?.LogDebug("Processing {ItemCount} items (already filtered by media type at API level)", itemCount);

                var results = new List<BaseItem>();

                // Analyze field requirements from expression sets - single source of truth for extraction flags
                var fieldReqs = new FieldRequirements();
                // Use list-level CollectionSearchDepth (minimum 1 for collection extraction to work)
                var collectionRecursionDepth = Math.Max(1, CollectionSearchDepth);
                var similarityComparisonFields = (SimilarityComparisonFields == null || SimilarityComparisonFields.Count == 0)
                    ? OperandFactory.DefaultSimilarityComparisonFields.ToList()
                    : SimilarityComparisonFields; // Default to Genre and Tags for backwards compatibility

                try
                {
                    if (ExpressionSets != null)
                    {
                        fieldReqs = FieldRequirements.Analyze(ExpressionSets, Orders, RandomGroupSelection);
                        // Set collection recursion depth from list-level setting
                        fieldReqs.CollectionRecursionDepth = collectionRecursionDepth;

                        if (fieldReqs.AdditionalUserIds.Count > 0)
                        {
                            logger?.LogDebug("Found user-specific expressions for {Count} users: [{UserIds}]",
                                fieldReqs.AdditionalUserIds.Count, string.Join(", ", fieldReqs.AdditionalUserIds));
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Error analyzing expression sets for expensive fields in playlist '{PlaylistName}'. Assuming no expensive fields needed.", Name);
                }

                // CRITICAL: Enable extraction when SimilarTo requires it for comparison fields
                // Without this, similarity matching will fail for fields that need conditional extraction
                try
                {
                    if (fieldReqs.NeedsSimilarTo && similarityComparisonFields is { Count: > 0 })
                    {
                        var simFields = new HashSet<string>(similarityComparisonFields, StringComparer.OrdinalIgnoreCase);

                        // Genre, Tags, Studios require ItemLists extraction
                        if (simFields.Contains("Genre") || simFields.Contains("Tags") || simFields.Contains("Studios"))
                        {
                            fieldReqs.RequiredGroups |= ExtractionGroup.ItemLists;
                            logger?.LogDebug("Enabled ItemLists extraction for SimilarTo comparison fields (Genre/Tags/Studios)");
                        }

                        // People fields (Actors, Directors, Writers, etc.) require People extraction
                        if (simFields.Any(f => FieldRegistry.IsPeopleField(f)))
                        {
                            fieldReqs.RequiredGroups |= ExtractionGroup.People;
                            logger?.LogDebug("Enabled People extraction for SimilarTo comparison fields");
                        }

                        // Audio Languages requires AudioLanguages extraction
                        if (simFields.Contains("Audio Languages"))
                        {
                            fieldReqs.RequiredGroups |= ExtractionGroup.AudioLanguages;
                            logger?.LogDebug("Enabled AudioLanguages extraction for SimilarTo comparison fields");
                        }

                        // Production Year requires Dates extraction
                        if (simFields.Contains("Production Year"))
                        {
                            fieldReqs.RequiredGroups |= ExtractionGroup.Dates;
                            logger?.LogDebug("Enabled Dates extraction for SimilarTo comparison fields (Production Year)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Error merging SimilarTo comparison fields into expensive-field requirements");
                }

                // Early validation of additional users to prevent exceptions during item processing
                if (fieldReqs.AdditionalUserIds.Count > 0 && userDataManager != null)
                {
                    foreach (var userId in fieldReqs.AdditionalUserIds)
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

                // Check if there are any rules to evaluate (including skipped ones like SimilarTo)
                // This prevents "no rules = match everything" when all rules are skipped
                bool hasAnyRules = compiledRules.Any(set => set?.Count > 0) ||
                    ExpressionSets?.Any(set => set?.Expressions?.Any(expr =>
                        expr?.MemberName == "SimilarTo") == true) == true;

                // Check if there are any non-expensive rules for two-phase filtering optimization
                bool hasNonExpensiveRules = false;
                try
                {
                    if (ExpressionSets != null)
                    {
                        hasNonExpensiveRules = ExpressionSets
                            .SelectMany(set => set?.Expressions ?? [])
                            .Any(IsNonExpensiveExpression);
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Error analyzing non-expensive rules in playlist '{PlaylistName}'. Assuming non-expensive rules exist.", Name);
                    hasNonExpensiveRules = true; // Conservative assumption,
                }

                // Build the DB-backed candidate set ONCE per filter run (never per chunk).
                // Null = no shrink possible; both expensive-field paths then behave exactly as
                // before. Only those paths consume it - ProcessItemsSimple stays untouched -
                // so building is skipped entirely when no expensive fields are required.
                HashSet<Guid>? candidateSet = null;
                if ((fieldReqs.RequiredGroups & ~FieldRegistry.CheapExtractionGroups) != ExtractionGroup.None)
                {
                    try
                    {
                        // Mandatory SeriesName bulk warmup: one query replaces the
                        // per-distinct-series GetItemById round-trips, and a complete dump is
                        // the precondition for SeriesName candidate narrowing (the resolver
                        // only narrows when the context carries the pool and the dump).
                        var seriesDumpComplete = fieldReqs.NeedsSeriesName
                            && OperandFactory.WarmSeriesNameCache(libraryManager, refreshCache, logger);

                        candidateSet = CandidateSetBuilder.CreateDefault()
                            .Build(ExpressionSets, new PrefilterContext(libraryManager, user, MediaTypes, logger)
                            {
                                UserManager = UserManager,
                                ItemRepository = ItemRepository,
                                PoolItems = itemsArray,
                                SeriesNamesById = seriesDumpComplete ? refreshCache.SeriesNameById : null,
                                ExtraOwnerSeriesIds = seriesDumpComplete ? refreshCache.ExtraOwnerSeriesId : null,
                            });
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Prefilter candidate set build failed for playlist '{PlaylistName}'. Continuing without prefilter.", Name);
                        candidateSet = null;
                    }
                }

                // Build reference metadata for SimilarTo queries (before chunking to avoid rebuilding per chunk)
                Dictionary<int, OperandFactory.ReferenceMetadata>? groupReferenceMetadata = null;
                if (fieldReqs.NeedsSimilarTo)
                {
                    logger?.LogDebug("Building reference metadata for SimilarTo queries (once per filter run) using fields: {Fields}",
                        string.Join(", ", similarityComparisonFields));

                    // One reference universe for the whole run: the pool plus, in MatchByMembers
                    // mode, the members of every container candidate. Direct items and container
                    // members are then scored against identical reference metadata - and a
                    // reference living inside a candidate container is still found when the pool
                    // itself holds only containers. Member enumeration here is cache-backed, so the
                    // later enumeration in MatchContainersByMembers hits the same cache. It is
                    // materialized because every SimilarTo block resolves against it in turn.
                    List<BaseItem> referenceUniverse = [.. itemsArray];
                    if (containerCandidates.Length > 0)
                    {
                        referenceUniverse.AddRange(
                            containerCandidates
                                .SelectMany(c => GetContainerMembers(c, user, refreshCache, logger))
                                .Where(m => m != null && !IsContainerKind(m)));
                    }

                    // Each rule block scores against its OWN references, so a block without
                    // SimilarTo is never filtered by another block's reference.
                    groupReferenceMetadata = [];
                    for (int groupIndex = 0; groupIndex < (ExpressionSets?.Count ?? 0); groupIndex++)
                    {
                        var groupSimilarTo = ExpressionSets![groupIndex]?.Expressions?
                            .Where(expr => expr?.MemberName == "SimilarTo")
                            .ToList();

                        if (groupSimilarTo is not { Count: > 0 })
                        {
                            continue;
                        }

                        var groupReferenceItems = OperandFactory.ResolveReferenceItems(groupSimilarTo, referenceUniverse, logger);
                        groupReferenceMetadata[groupIndex] = OperandFactory.BuildReferenceMetadataFromItems(
                            groupReferenceItems, similarityComparisonFields, libraryManager, logger);
                    }
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
                            fieldReqs, groupReferenceMetadata, similarityComparisonFields, compiledRules, hasAnyRules, hasNonExpensiveRules, candidateSet, refreshCache);
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

                // MatchByMembers: a container is included when at least one of its members passes
                // the full rule pipeline; passing members' group indices project onto the container
                if (containerCandidates.Length > 0)
                {
                    var matchedContainers = MatchContainersByMembers(containerCandidates, libraryManager, user, userDataManager,
                        logger, fieldReqs, groupReferenceMetadata, similarityComparisonFields, compiledRules, hasAnyRules, hasNonExpensiveRules, refreshCache);
                    results.AddRange(matchedContainers);
                }

                // Matched collections still pull nested collections up to CollectionSearchDepth
                if (hasContainerTypes)
                {
                    AppendNestedCollections(results, libraryManager, user, refreshCache, logger);
                }

                // Check if we need to expand Collections based on media type selection
                var expandedResults = ExpandCollectionsBasedOnMediaType(results, libraryManager, user, userDataManager, logger, refreshCache, fieldReqs);
                logger?.LogDebug("Playlist '{PlaylistName}' expanded from {OriginalCount} items to {ExpandedCount} items after Collections processing",
                    Name, results.Count, expandedResults.Count);

                // Keep a single library item per matched external-list music track
                expandedResults = DedupExternalMusicListMatches(expandedResults, refreshCache, logger);

                expandedResults = ApplyRandomGroupSelection(expandedResults, libraryManager, user, userDataManager, logger, refreshCache);

                // Configure aggregate-user ('(all users)'/'(selected users total)') orders and warm
                // their container child caches against the FINAL filtered/expanded item set, not the
                // raw (media-type-only-filtered) candidate pool - warming every Series/Season/
                // MusicAlbum in an unfiltered pool would issue DB queries for containers the list's
                // own rules are about to discard anyway. This must still run before
                // ApplyPerGroupLimits below, since it calls ApplyMultipleOrders per rule group and
                // would otherwise sort with unconfigured aggregate users.
                ConfigureAggregateUserOrders(expandedResults, libraryManager, user, refreshCache, logger);

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

                    // Pre-compute Round Robin positions before sorting
                    PrepareRoundRobinPositions(expandedResults, logger);

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

        private bool HasRandomGroupSelection()
        {
            return RandomGroupSelection?.Enabled == true
                && !string.IsNullOrWhiteSpace(RandomGroupSelection.GroupBy)
                && RandomGroupSelectionDto.IsSupportedGroupByField(RandomGroupSelection.GroupBy);
        }

        private List<BaseItem> ApplyRandomGroupSelection(
            List<BaseItem> items,
            ILibraryManager libraryManager,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache refreshCache)
        {
            try
            {
                if (!HasRandomGroupSelection() || items == null || items.Count == 0)
                {
                    return items ?? new List<BaseItem>();
                }

                var groupBy = RandomGroupSelection!.GroupBy!;
                var requiredGroup = RandomGroupSelectionDto.GetExtractionGroup(groupBy);
                if (requiredGroup == ExtractionGroup.None)
                {
                    logger?.LogWarning("Random Group Selection for playlist '{PlaylistName}' uses unsupported field '{GroupBy}'. Skipping group selection.", Name, groupBy);
                    return items;
                }

                var extractionOptions = new MediaTypeExtractionOptions
                {
                    RequiredGroups = requiredGroup,
                    IncludeUnwatchedSeries = true,
                    Origin = this.Origin,
                };

                var groups = new Dictionary<string, List<BaseItem>>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in items)
                {
                    try
                    {
                        var operand = OperandFactory.GetMediaType(libraryManager, item, user, userDataManager, UserManager, logger, extractionOptions, refreshCache);
                        var keys = GetRandomGroupKeys(operand, groupBy);

                        foreach (var key in keys)
                        {
                            if (!groups.TryGetValue(key, out var groupItems))
                            {
                                groupItems = new List<BaseItem>();
                                groups[key] = groupItems;
                            }

                            groupItems.Add(item);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogDebug(ex, "Error extracting Random Group Selection key '{GroupBy}' for item '{ItemName}'. Skipping item for grouping.", groupBy, item.Name);
                    }
                }

                var minimumItems = RandomGroupSelection.MinimumItems.GetValueOrDefault();
                var eligibleGroups = groups
                    .Where(kvp => minimumItems <= 0 || kvp.Value.Count >= minimumItems)
                    .ToList();

                if (eligibleGroups.Count == 0)
                {
                    logger?.LogInformation("Random Group Selection for playlist '{PlaylistName}' found no eligible '{GroupBy}' groups from {GroupCount} total groups (MinimumItems: {MinimumItems}). Returning no items.",
                        Name, groupBy, groups.Count, minimumItems);
                    return [];
                }

#pragma warning disable CA5394
                var random = new Random((int)(DateTime.Now.Ticks & 0x7FFFFFFF));
                var selectedGroup = eligibleGroups[random.Next(eligibleGroups.Count)];
#pragma warning restore CA5394

                logger?.LogInformation("Random Group Selection for playlist '{PlaylistName}' selected {GroupBy} '{GroupName}' with {ItemCount} items from {EligibleGroupCount} eligible groups",
                    Name, groupBy, selectedGroup.Key, selectedGroup.Value.Count, eligibleGroups.Count);

                return selectedGroup.Value;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error applying Random Group Selection for playlist '{PlaylistName}'. Returning ungrouped items.", Name);
                return items;
            }
        }

        private static List<string> GetRandomGroupKeys(Operand operand, string groupBy)
        {
            IEnumerable<string> values = groupBy switch
            {
                "Artists" => operand.Artists,
                "AlbumArtists" => operand.AlbumArtists,
                "Album" => [operand.Album],
                "SeriesName" => [operand.SeriesName],
                "Genres" => operand.Genres,
                "Studios" => operand.Studios,
                "Tags" => operand.Tags,
                _ => [],
            };

            return values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
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
                    PrepareRoundRobinPositions(groupItems, logger);
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
        /// Keeps a single library item per matched external-list music track. Multiple copies of
        /// the same song (album + compilation + live versions) can match one list entry; only the
        /// best match survives — exact MusicBrainz recording ID first, then title matches that
        /// needed no trailing "(...)" group stripping, then the rest. Items that did not match a
        /// music external list are never dropped. Only this list's own ExternalList rule URLs are
        /// considered: the refresh cache retains fetched data for other lists (shared, additive),
        /// and matches against those must not affect this list's results.
        /// </summary>
        private List<BaseItem> DedupExternalMusicListMatches(List<BaseItem> items, RefreshQueueService.RefreshCache refreshCache, ILogger? logger)
        {
            if (refreshCache.MusicListPositionsByUrl.IsEmpty)
            {
                return items;
            }

            // URLs referenced by this list's own ExternalList rules
            var activeListUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var set in ExpressionSets ?? [])
            {
                foreach (var expr in set?.Expressions ?? [])
                {
                    if (expr?.MemberName == "ExternalList" && !string.IsNullOrWhiteSpace(expr.TargetValue))
                    {
                        activeListUrls.Add(expr.TargetValue);
                    }
                }
            }

            if (activeListUrls.Count == 0)
            {
                return items;
            }

            // Group candidate items by (list URL, track position)
            var groups = new Dictionary<(string Url, int Position), List<BaseItem>>();
            foreach (var item in items)
            {
                if (!refreshCache.MusicListPositionsByUrl.TryGetValue(item.Id, out var positionsByUrl))
                {
                    continue;
                }

                foreach (var entry in positionsByUrl)
                {
                    if (!activeListUrls.Contains(entry.Key))
                    {
                        continue;
                    }

                    var key = (entry.Key, entry.Value);
                    if (!groups.TryGetValue(key, out var group))
                    {
                        group = [];
                        groups[key] = group;
                    }

                    group.Add(item);
                }
            }

            // An item survives when it wins at least one of the tracks it matched
            var winners = new HashSet<Guid>();
            var losers = new HashSet<Guid>();
            foreach (var group in groups.Values)
            {
                var winner = group
                    .OrderBy(MusicListMatchRank)
                    .ThenBy(i => i.Id)
                    .First();
                winners.Add(winner.Id);
                foreach (var item in group)
                {
                    if (item.Id != winner.Id)
                    {
                        losers.Add(item.Id);
                    }
                }
            }

            losers.ExceptWith(winners);
            if (losers.Count == 0)
            {
                return items;
            }

            logger?.LogDebug("External music list dedup removed {Count} duplicate track match(es)", losers.Count);
            return items.Where(i => !losers.Contains(i.Id)).ToList();
        }

        /// <summary>
        /// Ranks how strongly a library item matched an external music list entry. Lower is better:
        /// 0 = exact recording MBID match (tagged items never match via fallback), 1 = title|artist
        /// fallback with a clean title, 2 = fallback where a trailing "(Live)"-style group was
        /// stripped to match.
        /// </summary>
        private static int MusicListMatchRank(BaseItem item)
        {
            if (!string.IsNullOrEmpty(item.GetProviderId("MusicBrainzRecording")))
            {
                return 0;
            }

            return MusicMatchKey.HasTrailingGroup(item.Name) ? 2 : 1;
        }

        /// <summary>
        /// Records which rule groups a matched container (collection/playlist) belongs to, so
        /// ApplyPerGroupLimits doesn't silently drop it and Rule Block Order places it
        /// in its group's block. Merges with any existing mapping (an item can be both
        /// a direct match and a nested child of another matched collection).
        /// </summary>
        private void TrackContainerGroupMapping(Guid itemId, List<int> matchedSetIndices)
        {
            _itemGroupMappings.AddOrUpdate(
                itemId,
                _ => new List<int>(matchedSetIndices),
                (_, existing) => existing.Union(matchedSetIndices).ToList());
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
            return ExpressionSets?.Any(set =>
                set.Expressions?.Any(expr =>
                    expr.MemberName == "Collections" &&
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
        /// Recursively adds a collection and its nested collections up to the specified depth.
        /// When <paramref name="encounteredIds"/> is provided, every collection reached by this
        /// walk is recorded in it - including collections already appended by an earlier root -
        /// so group tracking can map shared descendants to every matching group. The walk then
        /// continues through already-visited nodes (guarded per-call against cycles) while
        /// <paramref name="visitedCollectionIds"/> still deduplicates the appended results.
        /// </summary>
        private static void AddCollectionWithNestedCollections(
            BaseItem collection,
            List<BaseItem> matchingCollections,
            HashSet<Guid> visitedCollectionIds,
            Dictionary<Guid, BaseItem> allCollectionsById,
            User user,
            ILogger? logger,
            int currentDepth,
            int maxDepth,
            ListOrigin origin,
            HashSet<Guid>? encounteredIds = null)
        {
            bool alreadyAdded = visitedCollectionIds.Contains(collection.Id);

            if (encounteredIds == null)
            {
                // No group tracking: already-visited nodes need no re-walk
                if (alreadyAdded)
                {
                    logger?.LogDebug("Skipping collection '{CollectionName}' - already visited (preventing circular reference)", collection.Name);
                    return;
                }
            }
            else if (!encounteredIds.Add(collection.Id))
            {
                return; // Circular reference protection within this root's walk
            }

            if (!alreadyAdded)
            {
                visitedCollectionIds.Add(collection.Id);
                matchingCollections.Add(collection);
                logger?.LogDebug("Collection '{CollectionName}' added by the nested-collection walk (depth={Depth})", collection.Name, currentDepth);
            }

            // If we haven't reached max depth, look for nested collections
            if (currentDepth < maxDepth)
            {
                var childItems = GetCollectionChildren(collection, user, logger);
                foreach (var child in childItems)
                {
                    // Check if child is a collection
                    if (child.GetBaseItemKind() == BaseItemKind.BoxSet && allCollectionsById.ContainsKey(child.Id))
                    {
                        // Skip if this child is the list being built (prevent self-reference)
                        if (origin.Matches(child))
                        {
                            continue;
                        }

                        AddCollectionWithNestedCollections(
                            child,
                            matchingCollections,
                            visitedCollectionIds,
                            allCollectionsById,
                            user,
                            logger,
                            currentDepth + 1,
                            maxDepth,
                            origin,
                            encounteredIds);
                    }
                }
            }
        }

        /// <summary>
        /// Gets children of a collection using reflection.
        /// </summary>
        private static BaseItem[] GetCollectionChildren(BaseItem collection, User user, ILogger? logger)
        {
            try
            {
                // Try GetChildren method
                var getChildrenMethod = collection.GetType().GetMethod("GetChildren", [typeof(User), typeof(bool)]);
                if (getChildrenMethod != null)
                {
                    var children = getChildrenMethod.Invoke(collection, [user, true]);
                    if (children is IEnumerable<BaseItem> childrenEnumerable)
                    {
                        return [.. childrenEnumerable];
                    }
                }

                // Try GetLinkedChildren method
                var getLinkedChildrenMethod = collection.GetType().GetMethod("GetLinkedChildren", Type.EmptyTypes);
                if (getLinkedChildrenMethod != null)
                {
                    var linkedChildren = getLinkedChildrenMethod.Invoke(collection, null);
                    if (linkedChildren is IEnumerable<BaseItem> linkedEnumerable)
                    {
                        return [.. linkedEnumerable];
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Error getting children for collection '{CollectionName}'", collection.Name);
            }

            return [];
        }

        /// <summary>
        /// True when the item is a container candidate kind (BoxSet or Playlist).
        /// </summary>
        private static bool IsContainerKind(BaseItem item) =>
            item.GetBaseItemKind() is BaseItemKind.BoxSet or BaseItemKind.Playlist;

        /// <summary>
        /// MatchByMembers mode: evaluates each container candidate's member items against the full
        /// compiled rule pipeline and includes a container when at least one member passes. Members
        /// shared across containers are evaluated exactly once - the unique member set runs through
        /// the same chunked two-phase pipeline as regular items - then per-container inclusion is a
        /// lookup. When group tracking is active (per-group limits / Rule Block Order), a container
        /// maps to every rule group any of its passing members matched.
        /// </summary>
        private List<BaseItem> MatchContainersByMembers(
            BaseItem[] containerCandidates,
            ILibraryManager libraryManager,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            FieldRequirements fieldReqs,
            IReadOnlyDictionary<int, OperandFactory.ReferenceMetadata>? groupReferenceMetadata,
            List<string> similarityComparisonFields,
            List<List<Func<Operand, bool>>> compiledRules,
            bool hasAnyRules,
            bool hasNonExpensiveRules,
            RefreshQueueService.RefreshCache refreshCache)
        {
            var matchedContainers = new List<BaseItem>();

            try
            {
                // Enumerate direct members per container (cached - no per-container queries beyond
                // an uncached first enumeration). Container-kind members are not evaluated as
                // members; nested collections are handled by the nested-collection walk instead.
                var membersByContainer = new Dictionary<Guid, BaseItem[]>();
                var uniqueMembers = new Dictionary<Guid, BaseItem>();
                foreach (var container in containerCandidates)
                {
                    var members = GetContainerMembers(container, user, refreshCache, logger)
                        .Where(m => m != null && !IsContainerKind(m))
                        .ToArray();
                    membersByContainer[container.Id] = members;
                    foreach (var member in members)
                    {
                        uniqueMembers.TryAdd(member.Id, member);
                    }
                }

                // Evaluate every unique member exactly once through the same pipeline as regular
                // items (two-phase filtering included). The DB-prefilter candidate set is NOT
                // applied: it was built for the pool's media types, and members can be of any kind.
                var passedMemberIds = new HashSet<Guid>();
                if (uniqueMembers.Count > 0)
                {
                    var config = Plugin.Instance?.Configuration;
                    var batchSize = config?.ProcessingBatchSize ?? 300;
                    if (batchSize <= 0)
                    {
                        batchSize = 300;
                    }

                    var memberList = uniqueMembers.Values.ToList();
                    for (int chunkStart = 0; chunkStart < memberList.Count; chunkStart += batchSize)
                    {
                        var chunk = memberList.Skip(chunkStart).Take(batchSize);
                        var passingMembers = ProcessItemChunk(chunk, libraryManager, user, userDataManager, logger,
                            fieldReqs, groupReferenceMetadata, similarityComparisonFields, compiledRules, hasAnyRules, hasNonExpensiveRules, null, refreshCache);
                        foreach (var member in passingMembers)
                        {
                            passedMemberIds.Add(member.Id);
                        }
                    }
                }

                logger?.LogDebug("MatchByMembers: {PassingCount}/{MemberCount} unique members passed the rules across {ContainerCount} container candidates",
                    passedMemberIds.Count, uniqueMembers.Count, containerCandidates.Length);

                bool trackGroups = NeedsGroupTracking();
                bool trackScores = fieldReqs.NeedsSimilarTo;
                foreach (var container in containerCandidates)
                {
                    var members = membersByContainer[container.Id];
                    HashSet<int>? containerGroups = trackGroups ? [] : null;
                    bool anyMemberPassed = false;
                    float bestScore = 0f;

                    foreach (var member in members)
                    {
                        if (!passedMemberIds.Contains(member.Id))
                        {
                            continue;
                        }

                        anyMemberPassed = true;
                        if (containerGroups == null && !trackScores)
                        {
                            break; // Nothing further to aggregate - the first passing member is enough
                        }

                        if (containerGroups != null && _itemGroupMappings.TryGetValue(member.Id, out var memberGroups))
                        {
                            containerGroups.UnionWith(memberGroups);
                        }

                        if (trackScores && _similarityScores.TryGetValue(member.Id, out var memberScore) && memberScore > bestScore)
                        {
                            bestScore = memberScore;
                        }
                    }

                    if (!anyMemberPassed)
                    {
                        continue;
                    }

                    matchedContainers.Add(container);
                    if (containerGroups is { Count: > 0 })
                    {
                        TrackContainerGroupMapping(container.Id, [.. containerGroups]);
                    }

                    // Similarity sorting looks scores up by result-item id, and the container -
                    // not its members - is the result item. Carry the best passing member's score
                    // so SimilarityOrder ranks the container by its closest match instead of 0.
                    if (trackScores)
                    {
                        _similarityScores[container.Id] = bestScore;
                    }

                    logger?.LogDebug("Container '{ContainerName}' matched via its members", container.Name);
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error matching containers by members for '{ListName}'", Name);
            }

            return matchedContainers;
        }

        /// <summary>
        /// Gets the direct member items of a container candidate, preferring the RefreshCache
        /// (CollectionDirectChildren / CollectionChildItems / PlaylistChildItems) before falling
        /// back to reflection. Uncached lookups are stored in the child-items caches; the
        /// Factory-built CollectionDirectChildren cache is only read, never seeded, because its
        /// builder treats a non-empty cache as fully built.
        /// </summary>
        private static BaseItem[] GetContainerMembers(BaseItem container, User user, RefreshQueueService.RefreshCache refreshCache, ILogger? logger)
        {
            if (container.GetBaseItemKind() == BaseItemKind.BoxSet)
            {
                if (refreshCache.CollectionDirectChildren.TryGetValue(container.Id, out var directChildren))
                {
                    return directChildren;
                }

                if (refreshCache.CollectionChildItems.TryGetValue(container.Id, out var cachedChildren))
                {
                    return cachedChildren;
                }

                var children = GetCollectionChildren(container, user, logger);
                refreshCache.CollectionChildItems.TryAdd(container.Id, children);
                return children;
            }

            if (refreshCache.PlaylistChildItems.TryGetValue(container.Id, out var cachedMembers))
            {
                return cachedMembers;
            }

            // GetCollectionChildren's GetChildren/GetLinkedChildren reflection works for playlists too
            var members = GetCollectionChildren(container, user, logger);
            refreshCache.PlaylistChildItems.TryAdd(container.Id, members);
            return members;
        }

        /// <summary>
        /// Appends nested collections of every matched collection in <paramref name="results"/> up
        /// to CollectionSearchDepth via the existing <see cref="AddCollectionWithNestedCollections"/>
        /// walk (Origin guard and group tracking included). Nested children inherit the matched
        /// root's rule-group mapping so per-group limits and Rule Block Order see them.
        /// Applies in both MatchByMembers modes.
        /// </summary>
        private void AppendNestedCollections(List<BaseItem> results, ILibraryManager libraryManager, User user,
            RefreshQueueService.RefreshCache refreshCache, ILogger? logger)
        {
            try
            {
                var maxDepth = GetMaxCollectionRecursionDepth();
                if (maxDepth <= 0 || results.Count == 0)
                {
                    return;
                }

                var matchedRoots = results.Where(static r => r.GetBaseItemKind() == BaseItemKind.BoxSet).ToList();
                if (matchedRoots.Count == 0)
                {
                    return;
                }

                // All-collections lookup for resolving nested children (shared per-drain cache)
                BaseItem[] allCollections;
                if (refreshCache.AllCollections != null)
                {
                    allCollections = refreshCache.AllCollections;
                }
                else
                {
                    var collectionQuery = new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = [BaseItemKind.BoxSet],
                        Recursive = true,
                    };
                    allCollections = [.. libraryManager.GetItemsResult(collectionQuery).Items];
                    refreshCache.AllCollections = allCollections;
                }

                var allCollectionsById = new Dictionary<Guid, BaseItem>();
                foreach (var collection in allCollections)
                {
                    allCollectionsById.TryAdd(collection.Id, collection);
                }

                bool trackGroups = NeedsGroupTracking();
                var walked = new List<BaseItem>();
                var visitedCollectionIds = new HashSet<Guid>();

                foreach (var root in matchedRoots)
                {
                    // When tracking groups, collect every collection this root's walk reaches -
                    // including ones already appended by an earlier root - so shared descendants
                    // get mapped to every matching group, not just the first
                    var encounteredIds = trackGroups ? new HashSet<Guid>() : null;

                    AddCollectionWithNestedCollections(
                        root,
                        walked,
                        visitedCollectionIds,
                        allCollectionsById,
                        user,
                        logger,
                        0,  // Start at depth 0 (root level)
                        maxDepth,
                        Origin,
                        encounteredIds);

                    if (encounteredIds != null && _itemGroupMappings.TryGetValue(root.Id, out var rootGroups) && rootGroups.Count > 0)
                    {
                        // Nested children inherit the matched root's rule groups
                        foreach (var encounteredId in encounteredIds)
                        {
                            TrackContainerGroupMapping(encounteredId, rootGroups);
                        }
                    }
                }

                // The walk re-adds the roots - append only collections not already in the results
                var existingIds = new HashSet<Guid>(results.Select(static r => r.Id));
                var appendedCount = 0;
                foreach (var collection in walked)
                {
                    if (existingIds.Add(collection.Id))
                    {
                        results.Add(collection);
                        appendedCount++;
                    }
                }

                if (appendedCount > 0)
                {
                    logger?.LogDebug("Appended {AppendedCount} nested collection(s) for {RootCount} matched collection(s) (depth={Depth})",
                        appendedCount, matchedRoots.Count, maxDepth);
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error appending nested collections for '{ListName}'", Name);
            }
        }

        /// <summary>
        /// Builds an item id → collection name map for Round Robin "Collections" grouping.
        /// TV collections contain Series items, so episodes resolve membership through their
        /// parent series; movies (and any direct members) resolve by their own id.
        /// When an item belongs to multiple collections, the alphabetically-first collection
        /// name wins (consistent with the Genres/Studios "first value" convention).
        /// Direct members only — nested collections are not flattened.
        /// </summary>
        private static Dictionary<Guid, string> BuildCollectionGroupKeyMap(
            IReadOnlyList<BaseItem> pool,
            User user,
            ILibraryManager libraryManager,
            RefreshQueueService.RefreshCache? refreshCache,
            ILogger? logger)
        {
            var map = new Dictionary<Guid, string>();

            try
            {
                BaseItem[] allCollections;
                if (refreshCache?.AllCollections != null)
                {
                    allCollections = refreshCache.AllCollections;
                }
                else
                {
                    var query = new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = [BaseItemKind.BoxSet],
                        Recursive = true,
                    };
                    allCollections = [.. libraryManager.GetItemsResult(query).Items];
                    if (refreshCache != null)
                    {
                        refreshCache.AllCollections = allCollections;
                    }
                }

                // memberId -> collection name; alphabetically-first collection wins
                var memberToCollection = new Dictionary<Guid, string>();
                foreach (var collection in allCollections.OrderBy(c => c.Name ?? string.Empty, OrderUtilities.SharedNaturalComparer))
                {
                    BaseItem[] children;
                    if (refreshCache != null && refreshCache.CollectionDirectChildren.TryGetValue(collection.Id, out var cachedChildren))
                    {
                        children = cachedChildren;
                    }
                    else
                    {
                        children = GetCollectionChildren(collection, user, logger);
                        refreshCache?.CollectionDirectChildren.TryAdd(collection.Id, children);
                    }

                    foreach (var child in children)
                    {
                        if (!memberToCollection.ContainsKey(child.Id))
                        {
                            memberToCollection[child.Id] = collection.Name ?? string.Empty;
                        }
                    }
                }

                foreach (var item in pool)
                {
                    if (memberToCollection.TryGetValue(item.Id, out var directName))
                    {
                        map[item.Id] = directName;
                    }
                    else if (item is Episode episode && episode.SeriesId != Guid.Empty &&
                             memberToCollection.TryGetValue(episode.SeriesId, out var seriesCollectionName))
                    {
                        map[item.Id] = seriesCollectionName;
                    }
                }

                logger?.LogDebug("Collection group map: {MappedCount} of {PoolCount} items belong to a collection ({CollectionCount} collections checked)",
                    map.Count, pool.Count, allCollections.Length);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Error building collection group map - items will fall back to series/name grouping");
            }

            return map;
        }

        /// <summary>
        /// Gets the collection search depth for the nested-collection walk of matched collections.
        /// Uses the list-level CollectionSearchDepth setting.
        /// </summary>
        private int GetMaxCollectionRecursionDepth()
        {
            // Use list-level CollectionSearchDepth directly
            // depth=0 means only matched collections, no nested collections
            // depth=1+ means include nested collections up to that depth
            return CollectionSearchDepth;
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
                    CollectionRecursionDepth = CollectionSearchDepth,
                    ExtractNextUnwatched = false,
                    ExtractSeriesName = false,
                    IncludeUnwatchedSeries = true,
                    AdditionalUserIds = [],
                    Origin = this.Origin,
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

        private List<BaseItem> ExpandCollectionsBasedOnMediaType(List<BaseItem> items, ILibraryManager libraryManager, User user, IUserDataManager? userDataManager, ILogger? logger, RefreshQueueService.RefreshCache refreshCache, FieldRequirements fieldReqs)
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
                                var matchingEpisodes = FilterEpisodesAgainstRules(seriesEpisodes, libraryManager, user, userDataManager, logger, refreshCache, fieldReqs, series);

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

        private List<BaseItem> FilterEpisodesAgainstRules(List<BaseItem> episodes, ILibraryManager libraryManager, User user, IUserDataManager? userDataManager, ILogger? logger, RefreshQueueService.RefreshCache refreshCache, FieldRequirements fieldReqs, Series? parentSeries = null)
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

                logger?.LogDebug("Filtering {EpisodeCount} episodes against playlist rules", episodes.Count);

                // Create extraction options from field requirements (DRY - single source of truth)
                var extractionOptions = MediaTypeExtractionOptions.FromRequirements(fieldReqs, Origin, CollectionSearchDepth);

                foreach (var episode in episodes)
                {
                    try
                    {
                        var operand = OperandFactory.GetMediaType(libraryManager, episode, user, userDataManager, UserManager, logger, extractionOptions, refreshCache);

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
        /// Pre-computes Round Robin interleave positions for any <see cref="RoundRobinBase"/> orders.
        /// Must be called before ApplyMultipleOrders() so ItemPositions are fresh for the given item set.
        /// </summary>
        /// <param name="items">The items to compute positions for.</param>
        /// <param name="logger">Optional logger for debugging.</param>
        private void PrepareRoundRobinPositions(IEnumerable<BaseItem> items, ILogger? logger)
        {
            if (Orders == null || Orders.Count == 0)
            {
                return;
            }

            List<BaseItem>? materializedItems = null;

            foreach (var order in Orders)
            {
                if (order is RoundRobinBase roundRobin)
                {
                    materializedItems ??= items as List<BaseItem> ?? items.ToList();
                    roundRobin.PreComputePositions(materializedItems, logger: logger);
                }
            }
        }

        /// <summary>
        /// Applies multiple sorting orders in cascade to a collection of items.
        /// </summary>
        /// <param name="items">The items to sort.</param>
        /// <param name="user">User for user-specific sorting.</param>
        /// <param name="userDataManager">User data manager for user-specific sorting.</param>
        /// <param name="logger">Optional logger for debugging.</param>
        /// <param name="refreshCache">Refresh cache for performance.</param>
        /// <returns>The sorted collection of items.</returns>
        internal IEnumerable<BaseItem> ApplyMultipleOrders(IEnumerable<BaseItem> items, User user, IUserDataManager? userDataManager, ILogger? logger, RefreshQueueService.RefreshCache refreshCache)
        {
            if (Orders == null || Orders.Count == 0)
            {
                return items;
            }

            // Wrap orders with ChildAggregatingOrder if UseChildValues is enabled
            var effectiveOrders = WrapOrdersWithChildAggregation(Orders, logger);

            logger?.LogDebug("ApplyMultipleOrders: Processing {OrderCount} sort options", effectiveOrders.Count);
            for (int i = 0; i < effectiveOrders.Count; i++)
            {
                logger?.LogDebug("  Sort #{Index}: {OrderName}", i + 1, effectiveOrders[i].Name);
            }

            // If there's only one order, use the original Order.OrderBy() method
            // Single sort optimization is now safe because GetSortKey logic is unified with OrderBy logic
            if (effectiveOrders.Count == 1)
            {
                logger?.LogDebug("Single sort detected, returning result from Order.OrderBy()");
                return effectiveOrders[0].OrderBy(items, user, userDataManager, logger, refreshCache);
            }

            // Use the common sorting helper for multi-sort scenarios
            return ApplySortingCore(items.ToList(), effectiveOrders, user, userDataManager, logger, refreshCache);
        }

        /// <summary>
        /// Wraps orders with ChildAggregatingOrder when UseChildValues is enabled in the corresponding SortOption.
        /// </summary>
        internal List<Order> WrapOrdersWithChildAggregation(List<Order> orders, ILogger? logger)
        {
            // If no SortOptions stored, return orders unchanged
            if (SortOptions == null || SortOptions.Count == 0)
            {
                return orders;
            }

            // Child aggregation is enabled when CollectionSearchDepth > 0 and at least one sort field supports it
            if (CollectionSearchDepth <= 0)
            {
                return orders;
            }

            var hasAggregableFields = SortOptions.Any(so => ChildAggregatingOrder.IsSupportedSortField(so.SortBy));
            if (!hasAggregableFields)
            {
                return orders;
            }

            logger?.LogDebug("Wrapping orders with child aggregation (CollectionSearchDepth={Depth})", CollectionSearchDepth);

            var wrappedOrders = new List<Order>();
            for (int i = 0; i < orders.Count; i++)
            {
                var order = orders[i];

                // Match with corresponding SortOption (same index)
                if (i < SortOptions.Count)
                {
                    var sortOption = SortOptions[i];

                    // Wrap if field is supported for child value aggregation
                    if (ChildAggregatingOrder.IsSupportedSortField(sortOption.SortBy))
                    {
                        var isDescending = IsDescendingOrder(order);
                        var wrappedOrder = new ChildAggregatingOrder(order, isDescending, sortOption.SortBy, CollectionSearchDepth);
                        wrappedOrders.Add(wrappedOrder);
                        logger?.LogDebug("  Wrapped order #{Index} ({OrderName}) with child aggregation for field {Field} (depth={Depth})",
                            i + 1, order.Name, sortOption.SortBy, CollectionSearchDepth);
                        continue;
                    }
                }

                // Keep original order
                wrappedOrders.Add(order);
            }

            return wrappedOrders;
        }

        /// <summary>
        /// Core sorting logic shared by ApplyMultipleOrders and ApplySecondarySorts.
        /// Creates composite sort keys for all items and applies multi-level sorting.
        /// </summary>
        internal static IEnumerable<BaseItem> ApplySortingCore(
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
            var orderCount = orders.Count;
            var itemsWithKeys = itemsList.Select(item => new
            {
                Item = item,
                SortKeys = orders.Select((order, idx) =>
                {
                    var key = order.GetSortKey(item, user, userDataManager, logger, itemRandomKeys, refreshCache);
                    // For non-final sorts, simplify keys so secondary sorts can take effect.
                    // Without this, the primary sort fully determines order and secondary sorts
                    // become no-ops. Two cases:
                    // 1. DateTime keys (DateCreated, LastPlayed): truncate to day precision so
                    //    items from the same day are grouped, letting e.g. TrackNumber sort within.
                    // 2. Composite keys (ReleaseDate, TrackNumber): strip embedded tiebreakers
                    //    (season/episode, disc/track) so the user's secondary sort determines
                    //    sub-ordering instead.
                    if (idx < orderCount - 1)
                    {
                        if (key is DateTime dt)
                        {
                            return (IComparable)dt.Date;
                        }

                        if (key is Orders.ICompositeSortKey compositeKey)
                        {
                            return compositeKey.PrimaryValue;
                        }
                    }

                    return key;
                }).ToList(),
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
        internal static bool IsDescendingOrder(Order order)
        {
            // Handle ChildAggregatingOrder wrapper - check its IsDescending property
            if (order is ChildAggregatingOrder childAggOrder)
            {
                return childAggOrder.IsDescending;
            }

            return order is NameOrderDesc ||
                   order is NameIgnoreArticlesOrderDesc ||
                   order is ProductionYearOrderDesc ||
                   order is DateCreatedOrderDesc ||
                   order is ReleaseDateOrderDesc ||
                   order is CommunityRatingOrderDesc ||
                   order is PlayCountOrderDesc ||
                   order is PlayCountTotalOrderDesc ||
                   order is PlayCountSelectedUsersTotalOrderDesc ||
                   order is LastPlayedTotalOrderDesc ||
                   order is LastPlayedOrderDesc ||
                   order is RuntimeOrderDesc ||
                   order is ResolutionOrderDesc ||
                   order is SeriesNameOrderDesc ||
                   order is SeriesNameIgnoreArticlesOrderDesc ||
                   order is AlbumNameOrderDesc ||
                   order is ArtistOrderDesc ||
                   order is SeasonNumberOrderDesc ||
                   order is EpisodeNumberOrderDesc ||
                   order is TrackNumberOrderDesc ||
                   order is Orders.RuleBlockOrderDesc ||
                   order is ExternalListOrderDesc ||
                   order is LastEpisodeAirDateOrderDesc ||
                   order is RoundRobinOrderDesc ||
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
                                Origin = this.Origin,
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

        /// <summary>
        /// Intersects a phase's input pool with the DB-backed candidate set. Exempt items are
        /// always kept regardless of the candidate set: extras never appear in candidate
        /// queries (they enter pools via the owner chain, not the main library query), and
        /// Series must survive whenever Collections rules are present so the
        /// DoesSeriesMatchCollectionsRules bypass can still see them.
        /// </summary>
        internal List<BaseItem> FilterByCandidateSet(IEnumerable<BaseItem> items, HashSet<Guid> candidateSet, ILogger? logger, string phase)
        {
            var input = items as ICollection<BaseItem> ?? items.ToList();
            var kept = new List<BaseItem>(Math.Min(input.Count, candidateSet.Count + 8));
            var hasCollectionsRules = HasCollectionsRules();

            foreach (var item in input)
            {
                if (item == null)
                {
                    continue;
                }

                if (candidateSet.Contains(item.Id) || IsPrefilterExempt(item, hasCollectionsRules))
                {
                    kept.Add(item);
                }
            }

            logger?.LogDebug("Prefilter shrank {Phase} pool {From} -> {To} items", phase, input.Count, kept.Count);
            return kept;
        }

        /// <summary>
        /// Items a prefilter must never shrink away, per the safety contract.
        /// </summary>
        private static bool IsPrefilterExempt(BaseItem item, bool hasCollectionsRules)
        {
            // Extras are pulled via GetExtras() on their owners (LibraryManagerHelper.FetchExtras);
            // a candidate query over the main library never returns them.
            if (OperandFactory.IsExtra(item))
            {
                return true;
            }

            // Series-under-Collections bypass: deliberately broader than
            // ShouldExpandEpisodesForCollections - keeping too much is always safe.
            if (item is Series && hasCollectionsRules)
            {
                return true;
            }

            // Container candidates (Collection/Playlist media types) are outside the prefilter
            // safety contract - candidate queries and name dumps target regular library items -
            // so never shrink them away. Keeping too much is always safe.
            if (IsContainerKind(item))
            {
                return true;
            }

            return false;
        }

        private bool HasCollectionsRules()
        {
            return ExpressionSets?.Any(set => set?.Expressions?.Any(expr => expr?.MemberName == "Collections") == true) == true;
        }

        private List<BaseItem> ProcessItemChunk(IEnumerable<BaseItem> items, ILibraryManager libraryManager,
            User user, IUserDataManager? userDataManager, ILogger? logger, FieldRequirements fieldReqs,
            IReadOnlyDictionary<int, OperandFactory.ReferenceMetadata>? groupReferenceMetadata, List<string> similarityComparisonFields, List<List<Func<Operand, bool>>> compiledRules, bool hasAnyRules, bool hasNonExpensiveRules, HashSet<Guid>? candidateSet, RefreshQueueService.RefreshCache refreshCache)
        {
            var results = new List<BaseItem>();

            try
            {
                if (items == null || compiledRules == null)
                {
                    logger?.LogDebug("ProcessItemChunk called with null items or compiledRules");
                    return results;
                }

                // Check if any truly expensive fields are needed (exclude cheap extraction groups)
                // Cheap groups (FileInfo, LibraryInfo, AudioMetadata, TextContent, ItemLists, UserData, Dates) don't require two-phase filtering
                // Use the centralized definition from FieldRegistry to ensure consistency
                var cheapGroups = FieldRegistry.CheapExtractionGroups;

                var expensiveGroups = fieldReqs.RequiredGroups & ~cheapGroups;
                var needsExpensiveFields = expensiveGroups != ExtractionGroup.None;

                if (needsExpensiveFields)
                {
                    // Use the shared RefreshCache passed from FilterPlaylistItems for optimal performance across chunks
                    // groupReferenceMetadata is also provided by caller (built once per filter run, not per chunk)

                    // Optimization: Separate rules into cheap and expensive categories
                    var cheapCompiledRules = new List<List<Func<Operand, bool>>>();

                    logger?.LogDebug("Using two-phase filtering for expensive field optimization (RequiredGroups: {RequiredGroups}, ExpensiveGroups: {ExpensiveGroups})",
                        fieldReqs.RequiredGroups, expensiveGroups);


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

                                    bool isExpensive = !IsNonExpensiveExpression(expr);

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
                        return ProcessItemsSimple(items, libraryManager, user, userDataManager, logger, fieldReqs, groupReferenceMetadata, similarityComparisonFields, compiledRules, hasAnyRules, refreshCache);
                    }

                    if (!hasNonExpensiveRules)
                    {
                        // No non-expensive rules - extract expensive data for all items that have expensive rules
                        logger?.LogDebug("No non-expensive rules found, extracting expensive data for all items");

                        // Materialize items to prevent multiple enumerations
                        var itemList = items as IList<BaseItem> ?? items.ToList();

                        // DB-prefilter: shrink the pool before per-item expensive extraction.
                        // Null candidate set = no shrink possible; behavior is unchanged.
                        if (candidateSet != null)
                        {
                            itemList = FilterByCandidateSet(itemList, candidateSet, logger, "expensive-only");
                        }

                        // Preload People cache in parallel if needed for performance
                        if (fieldReqs.NeedsPeople)
                        {
                            logger?.LogDebug("Preloading People cache for all {Count} items (expensive-only path)", itemList.Count);
                            OperandFactory.PreloadPeopleCache(libraryManager, itemList, refreshCache, logger);
                        }

                        // Process items sequentially for expensive field extraction and evaluation
                        InvalidOperationException? userNotFoundException = null;

                        logger?.LogDebug("Processing {Count} items sequentially (expensive-only path)", itemList.Count);

                        // Create extraction options from field requirements (DRY - single source of truth)
                        var extractionOptions = MediaTypeExtractionOptions.FromRequirements(fieldReqs, Origin, CollectionSearchDepth);

                        foreach (var item in itemList)
                        {
                            if (item == null || userNotFoundException != null) continue;

                            try
                            {
                                var operand = OperandFactory.GetMediaType(libraryManager, item, user, userDataManager, UserManager, logger, extractionOptions, refreshCache);

                                bool matches = false;
                                List<int>? matchingGroups = null;
                                float similarityScore = 0f;

                                if (!hasAnyRules)
                                {
                                    matches = true;
                                }
                                else
                                {
                                    // Check if we need per-group tracking for limits or Rule Block Order sorting
                                    bool needsGroupTracking = NeedsGroupTracking();

                                    if (needsGroupTracking)
                                    {
                                        matchingGroups = GetMatchingGroupIndices(compiledRules, operand, groupReferenceMetadata, similarityComparisonFields, logger, out similarityScore);
                                        matches = matchingGroups.Count > 0;
                                    }
                                    else
                                    {
                                        matches = EvaluateLogicGroups(compiledRules, operand, groupReferenceMetadata, similarityComparisonFields, logger, out similarityScore);
                                    }
                                }

                                if (matches)
                                {
                                    results.Add(item);

                                    // Store similarity score for potential sorting
                                    if (groupReferenceMetadata != null)
                                    {
                                        _similarityScores[item.Id] = similarityScore;
                                    }

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

                                // Phase 1: Extract cheap (non-expensive) properties and check non-expensive rules
                                // Filter RequiredGroups to only include cheap extraction groups (using constant from outer scope)
                                var phase1Groups = fieldReqs.RequiredGroups & cheapGroups;
                                var cheapOperand = OperandFactory.GetMediaType(libraryManager, item, user, userDataManager, UserManager, logger, new MediaTypeExtractionOptions
                                {
                                    RequiredGroups = phase1Groups, // Only cheap extraction groups for Phase 1
                                    IncludeUnwatchedSeries = true,
                                    AdditionalUserIds = [.. fieldReqs.AdditionalUserIds],
                                    Origin = this.Origin,
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

                        // DB-prefilter: shrink the Phase 1 survivors before per-item expensive
                        // extraction in Phase 2. Null candidate set = no shrink possible.
                        // phase1SeriesMatches already bypassed Phase 2 above and are never filtered.
                        if (candidateSet != null)
                        {
                            phase1Survivors = FilterByCandidateSet(phase1Survivors, candidateSet, logger, "Phase 2");
                        }

                        // Preload People cache for Phase 1 survivors if needed
                        if (fieldReqs.NeedsPeople && phase1Survivors.Count > 0)
                        {
                            logger?.LogDebug("Preloading People cache for {Count} Phase 1 survivors", phase1Survivors.Count);
                            OperandFactory.PreloadPeopleCache(libraryManager, phase1Survivors, refreshCache, logger);
                        }

                        // Second pass: Process Phase 1 survivors with expensive data sequentially
                        userNotFoundException = null;
                        var debugItemCount = 0;

                        logger?.LogDebug("Processing {Count} Phase 1 survivors sequentially (Phase 2)", phase1Survivors.Count);

                        // Create extraction options from field requirements for Phase 2 (DRY - single source of truth)
                        var phase2Options = MediaTypeExtractionOptions.FromRequirements(fieldReqs, Origin, CollectionSearchDepth);

                        foreach (var item in phase1Survivors)
                        {
                            if (userNotFoundException != null) break;

                            try
                            {
                                // Phase 2: Extract expensive data and check complete rules
                                var fullOperand = OperandFactory.GetMediaType(libraryManager, item, user, userDataManager, UserManager, logger, phase2Options, refreshCache);

                                // Debug: Log expensive data found for first few items
                                bool shouldLog = debugItemCount < 5;
                                if (shouldLog)
                                {
                                    debugItemCount++;

                                    if (fieldReqs.NeedsAudioLanguages)
                                    {
                                        logger?.LogDebug("Item '{Name}': Found {Count} audio languages: [{Languages}]",
                                            item.Name, fullOperand.AudioLanguages?.Count ?? 0, fullOperand.AudioLanguages != null ? string.Join(", ", fullOperand.AudioLanguages) : "none");
                                    }
                                    if (fieldReqs.NeedsPeople)
                                    {
                                        logger?.LogDebug("Item '{Name}': Found {Count} people: [{People}]",
                                            item.Name, fullOperand.People?.Count ?? 0, fullOperand.People != null ? string.Join(", ", fullOperand.People.Take(5)) : "none");
                                    }
                                    if (fieldReqs.NeedsCollections)
                                    {
                                        logger?.LogDebug("Item '{Name}': Found {Count} collections: [{Collections}]",
                                            item.Name, fullOperand.Collections?.Count ?? 0, fullOperand.Collections != null ? string.Join(", ", fullOperand.Collections) : "none");
                                    }
                                    if (fieldReqs.NeedsNextUnwatched)
                                    {
                                        logger?.LogDebug("Item '{Name}': NextUnwatched status: {NextUnwatchedUsers}",
                                            item.Name, fullOperand.NextUnwatchedByUser?.Count > 0 ? string.Join(", ", fullOperand.NextUnwatchedByUser.Select(x => $"{x.Key}={x.Value}")) : "none");
                                    }
                                }

                                bool matches = false;
                                List<int>? matchingGroups = null;
                                float similarityScore = 0f;

                                if (!hasAnyRules)
                                {
                                    matches = true;
                                }
                                else
                                {
                                    // Check if we need per-group tracking for limits or Rule Block Order sorting
                                    bool needsGroupTracking = NeedsGroupTracking();

                                    if (needsGroupTracking)
                                    {
                                        matchingGroups = GetMatchingGroupIndices(compiledRules, fullOperand, groupReferenceMetadata, similarityComparisonFields, logger, out similarityScore);
                                        matches = matchingGroups.Count > 0;
                                    }
                                    else
                                    {
                                        matches = EvaluateLogicGroups(compiledRules, fullOperand, groupReferenceMetadata, similarityComparisonFields, logger, out similarityScore);
                                    }
                                }

                                if (matches)
                                {
                                    results.Add(item);

                                    // Store similarity score for potential sorting
                                    if (groupReferenceMetadata != null)
                                    {
                                        _similarityScores[item.Id] = similarityScore;
                                    }

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
                    return ProcessItemsSimple(items, libraryManager, user, userDataManager, logger, fieldReqs, groupReferenceMetadata, similarityComparisonFields, compiledRules, hasAnyRules, refreshCache);
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
            User user, IUserDataManager? userDataManager, ILogger? logger, FieldRequirements fieldReqs,
            IReadOnlyDictionary<int, OperandFactory.ReferenceMetadata>? groupReferenceMetadata, List<string> similarityComparisonFields,
            List<List<Func<Operand, bool>>> compiledRules, bool hasAnyRules, RefreshQueueService.RefreshCache refreshCache)
        {
            var results = new List<BaseItem>();

            // Materialize items to prevent multiple enumerations
            var itemList = items as IList<BaseItem> ?? items.ToList();

            // Preload People cache if needed for performance
            if (fieldReqs.NeedsPeople)
            {
                logger?.LogDebug("Preloading People cache for simple processing ({Count} items)", itemList.Count);
                OperandFactory.PreloadPeopleCache(libraryManager, itemList, refreshCache, logger);
            }

            // Process items sequentially
            InvalidOperationException? userNotFoundException = null;

            logger?.LogDebug("Processing {Count} items sequentially (simple path)", itemList.Count);

            // Create extraction options from field requirements (DRY - single source of truth)
            var extractionOptions = MediaTypeExtractionOptions.FromRequirements(fieldReqs, Origin, CollectionSearchDepth);

            try
            {
                foreach (var item in itemList)
                {
                    if (item == null || userNotFoundException != null) continue;

                    try
                    {
                        var operand = OperandFactory.GetMediaType(libraryManager, item, user, userDataManager, UserManager, logger, extractionOptions, refreshCache);

                        bool matches = false;
                        List<int>? matchingGroups = null;
                        float similarityScore = 0f;

                        if (!hasAnyRules)
                        {
                            matches = true;
                        }
                        else
                        {
                            // Check if we need per-group tracking for limits or Rule Block Order sorting
                            bool needsGroupTracking = NeedsGroupTracking();

                            if (needsGroupTracking)
                            {
                                matchingGroups = GetMatchingGroupIndices(compiledRules, operand, groupReferenceMetadata, similarityComparisonFields, logger, out similarityScore);
                                matches = matchingGroups.Count > 0;
                            }
                            else
                            {
                                matches = EvaluateLogicGroups(compiledRules, operand, groupReferenceMetadata, similarityComparisonFields, logger, out similarityScore);
                            }
                        }

                        if (matches)
                        {
                            results.Add(item);

                            // Store similarity score for potential sorting
                            if (groupReferenceMetadata != null)
                            {
                                _similarityScores[item.Id] = similarityScore;
                            }

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

        /// <summary>
        /// Extracts the CollectionSearchDepth from the first Collections expression that has it set.
        /// This allows per-rule control of collection traversal depth.
        /// Name rules are also considered: the include-only migration
        /// (SmartListFileSystem.MigrateIncludeOnlyRulesToMediaTypes) rewrites legacy include-only
        /// Collections/Playlists rules to Name rules and keeps their depth, so the configured
        /// nested-collection walk depth must stay reachable on the migrated shape.
        /// </summary>
        /// <param name="expressionSets">The expression sets to search</param>
        /// <returns>The depth value if found, null otherwise</returns>
        private static int? ExtractCollectionSearchDepthFromExpressions(List<ExpressionSet>? expressionSets)
        {
            if (expressionSets == null || expressionSets.Count == 0)
            {
                return null;
            }

            // Find the first Collections expression that has CollectionSearchDepth set.
            // Name rules only carry a depth when the include-only migration put it there.
            foreach (var set in expressionSets)
            {
                if (set?.Expressions == null) continue;

                foreach (var expr in set.Expressions)
                {
                    if (expr != null && expr.CollectionSearchDepth.HasValue &&
                        (expr.MemberName == "Collections" || expr.MemberName == "Name"))
                    {
                        // Clamp to valid range (0-10)
                        return Math.Max(0, Math.Min(10, expr.CollectionSearchDepth.Value));
                    }
                }
            }

            return null;
        }

        private void ConfigureAggregateUserOrders(
            IReadOnlyCollection<BaseItem> items,
            ILibraryManager libraryManager,
            User currentUser,
            RefreshQueueService.RefreshCache refreshCache,
            ILogger? logger)
        {
            if (Orders == null || Orders.Count == 0)
            {
                return;
            }

            var aggregateOrders = Orders.OfType<IAggregateUsersOrder>().ToList();
            if (aggregateOrders.Count == 0)
            {
                return;
            }

            // "(all users)" sorts must aggregate over EVERY user on the server - that's the whole
            // point of the name - not just the users this particular list happens to be shared
            // with. Legacy "(selected users total)" aliases keep the original, narrower
            // playlist-scoped resolution so previously-saved lists don't silently change behavior.
            var allUsersOrders = aggregateOrders.OfType<IAllUsersScopeOrder>().ToList();
            var selectedUsersOrders = aggregateOrders.Where(o => o is not IAllUsersScopeOrder).ToList();

            if (allUsersOrders.Count > 0)
            {
                var serverUsers = ResolveAllServerUsers(currentUser, logger);
                foreach (var order in allUsersOrders)
                {
                    order.SetAggregateUsers(serverUsers);
                }
            }

            if (selectedUsersOrders.Count > 0)
            {
                var resolvedUsers = ResolvePlaylistScopedUsers(currentUser, logger);
                foreach (var order in selectedUsersOrders)
                {
                    order.SetAggregateUsers(resolvedUsers);
                }
            }

            // Container aggregation (Series/Season/MusicAlbum -> children) reads per-container
            // child caches that are otherwise only ever warmed for whichever single user happens
            // to trigger extraction of an unrelated rule field. These caches are unfiltered by user
            // visibility and keyed by container id only, so a single warm-up covers every aggregate
            // user - see WarmAggregateUserContainerCaches for why.
            WarmAggregateUserContainerCaches(items, libraryManager, refreshCache, logger);
        }

        /// <summary>
        /// Resolves every user known to the server, for orders scoped to literally "all users"
        /// (as opposed to only the users a list is shared with). Falls back to the current user if
        /// the user manager is unavailable or resolution fails, so aggregate sorts degrade to
        /// owner-only semantics rather than throwing.
        /// </summary>
        private List<User> ResolveAllServerUsers(User currentUser, ILogger? logger)
        {
            if (UserManager == null)
            {
                logger?.LogDebug("Aggregate all-users sort fallback in '{PlaylistName}': UserManager unavailable, using current user {UserId}", Name, currentUser.Id);
                return [currentUser];
            }

            try
            {
                var users = PlaylistUserResolver.GetAllUsers(UserManager);
                if (users.Count > 0)
                {
                    return users;
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to resolve all server users for aggregate sort in '{PlaylistName}'. Falling back to current user.", Name);
            }

            return [currentUser];
        }

        /// <summary>
        /// Resolves the users this list is assigned to (its playlist mappings, or the collection
        /// owner) - the original, narrower resolution preserved for the legacy "(selected users
        /// total)" sort aliases.
        /// </summary>
        private List<User> ResolvePlaylistScopedUsers(User currentUser, ILogger? logger)
        {
            var resolvedUsers = new List<User>();
            foreach (var userId in _playlistUserIds)
            {
                if (!Guid.TryParse(userId, out var parsedId) || parsedId == Guid.Empty)
                {
                    continue;
                }

                var targetUser = UserManager?.GetUserById(parsedId);
                if (targetUser != null)
                {
                    resolvedUsers.Add(targetUser);
                }
            }

            if (resolvedUsers.Count == 0)
            {
                resolvedUsers.Add(currentUser);
                logger?.LogDebug("Aggregate user sort fallback in '{PlaylistName}': no playlist users resolved, using current user {UserId}", Name, currentUser.Id);
            }

            return resolvedUsers;
        }

        /// <summary>
        /// Proactively populates the per-container aggregation child caches (Series -> episodes,
        /// Season -> episodes, MusicAlbum -> tracks) for every container candidate in the item pool.
        /// Without this, PlayCount/LastPlayed aggregate sorts only ever see a warm cache for
        /// whichever user happened to trigger an unrelated rule's extraction - every OTHER aggregate
        /// user gets a cache miss and silently falls back to 0 / DateTime.MinValue. These caches are
        /// deliberately unfiltered by any user's parental-rating/library-access restrictions and
        /// keyed by container id only (not per-user) - see
        /// <see cref="RefreshQueueService.RefreshCache.SeriesEpisodesForAggregation"/> - so a single
        /// warm-up per container covers every aggregate user, not one query per user. Reuses the
        /// exact same cache-population helpers the normal extraction pipeline uses
        /// (<see cref="OperandFactory"/>), so a hit here or there is a no-op dictionary lookup, not a
        /// duplicated query path.
        /// </summary>
        private static void WarmAggregateUserContainerCaches(
            IReadOnlyCollection<BaseItem> items,
            ILibraryManager libraryManager,
            RefreshQueueService.RefreshCache refreshCache,
            ILogger? logger)
        {
            foreach (var item in items)
            {
                switch (item)
                {
                    case Series series:
                        OperandFactory.GetCachedSeriesEpisodesForAggregation(series.Id, libraryManager, refreshCache, logger);
                        break;
                    case Season season:
                        OperandFactory.GetCachedSeasonEpisodes(season.Id, libraryManager, refreshCache, logger);
                        break;
                    case MusicAlbum album:
                        OperandFactory.GetCachedAlbumTracks(album.Id, libraryManager, refreshCache, logger);
                        break;
                }
            }
        }
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
            { "PlayCount (all users) Ascending", () => new PlayCountTotalOrder() },
            { "PlayCount (all users) Descending", () => new PlayCountTotalOrderDesc() },
            { "PlayCount (selected users total) Ascending", () => new PlayCountSelectedUsersTotalOrder() },
            { "PlayCount (selected users total) Descending", () => new PlayCountSelectedUsersTotalOrderDesc() },
            { "LastPlayed (owner) Ascending", () => new LastPlayedOrder() },
            { "LastPlayed (owner) Descending", () => new LastPlayedOrderDesc() },
            { "LastPlayed (all users) Ascending", () => new LastPlayedTotalOrder() },
            { "LastPlayed (all users) Descending", () => new LastPlayedTotalOrderDesc() },
            { "Runtime Ascending", () => new RuntimeOrder() },
            { "Runtime Descending", () => new RuntimeOrderDesc() },
            { "Resolution Ascending", () => new ResolutionOrder() },
            { "Resolution Descending", () => new ResolutionOrderDesc() },
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
            { "External List Order Ascending", () => new ExternalListOrder() },
            { "External List Order Descending", () => new ExternalListOrderDesc() },
            { "LastEpisodeAirDate Ascending", () => new LastEpisodeAirDateOrder() },
            { "LastEpisodeAirDate Descending", () => new LastEpisodeAirDateOrderDesc() },
            { "Round Robin Ascending", () => new RoundRobinOrder() },
            { "Round Robin Descending", () => new RoundRobinOrderDesc() },
            { "Random Round Robin", () => new RoundRobinRandomOrder() },
            { "Shuffled Round Robin", () => new RoundRobinShuffledOrder() },
            { "Least Recently Watched Round Robin", () => new RoundRobinLeastRecentlyWatchedOrder() },
            { "NoOrder", () => new NoOrder() },
        };

        public static Order CreateOrder(string orderName)
        {
            return OrderMap.TryGetValue(orderName ?? "", out var factory)
                ? factory()
                : new NoOrder();
        }

        private static readonly HashSet<string> DirectionlessOrders = new(StringComparer.Ordinal)
        {
            "Random",
            "Random Round Robin",
            "Shuffled Round Robin",
            "Least Recently Watched Round Robin",
            "NoOrder",
        };

        /// <summary>
        /// True for sorts that have no Ascending/Descending variants and are registered
        /// under their bare name. Mirrors ORDERLESS_SORTS in config-core.js.
        /// </summary>
        public static bool IsDirectionless(string orderName)
        {
            return DirectionlessOrders.Contains(orderName ?? "");
        }
    }

    /// <summary>
    /// Helper class to analyze field requirements from expression sets.
    /// Uses ExtractionGroup flags for efficient storage and lookup.
    /// </summary>
    public class FieldRequirements
    {
        /// <summary>
        /// Flags indicating which extraction groups are required.
        /// </summary>
        public ExtractionGroup RequiredGroups { get; set; } = ExtractionGroup.None;

        /// <summary>
        /// Whether to include unwatched series in NextUnwatched filtering.
        /// </summary>
        public bool IncludeUnwatchedSeries { get; set; } = true;

        /// <summary>
        /// User IDs from user-specific rules.
        /// </summary>
        public List<string> AdditionalUserIds { get; set; } = [];

        /// <summary>
        /// SimilarTo expressions for reference item lookup.
        /// </summary>
        public List<Expression> SimilarToExpressions { get; set; } = [];

        /// <summary>
        /// How deep to traverse nested collections.
        /// </summary>
        public int CollectionRecursionDepth { get; set; } = 1;

        // Computed accessors for RequiredGroups flags - expensive extraction groups
        public bool NeedsAudioLanguages => RequiredGroups.HasFlag(ExtractionGroup.AudioLanguages);
        public bool NeedsSubtitleLanguages => RequiredGroups.HasFlag(ExtractionGroup.AudioLanguages);
        public bool NeedsAudioQuality => RequiredGroups.HasFlag(ExtractionGroup.AudioQuality);
        public bool NeedsVideoQuality => RequiredGroups.HasFlag(ExtractionGroup.VideoQuality);
        public bool NeedsPeople => RequiredGroups.HasFlag(ExtractionGroup.People);
        public bool NeedsCollections => RequiredGroups.HasFlag(ExtractionGroup.Collections);
        public bool NeedsPlaylists => RequiredGroups.HasFlag(ExtractionGroup.Playlists);
        public bool NeedsNextUnwatched => RequiredGroups.HasFlag(ExtractionGroup.NextUnwatched);
        public bool NeedsSeriesName => RequiredGroups.HasFlag(ExtractionGroup.SeriesName);
        public bool NeedsParentTags => RequiredGroups.HasFlag(ExtractionGroup.ParentTags);
        public bool NeedsParentStudios => RequiredGroups.HasFlag(ExtractionGroup.ParentStudios);
        public bool NeedsParentGenres => RequiredGroups.HasFlag(ExtractionGroup.ParentGenres);
        public bool NeedsSimilarTo => RequiredGroups.HasFlag(ExtractionGroup.SimilarTo);
        public bool NeedsLastEpisodeAirDate => RequiredGroups.HasFlag(ExtractionGroup.LastEpisodeAirDate);
        public bool NeedsExternalLists => RequiredGroups.HasFlag(ExtractionGroup.ExternalLists);

        /// <summary>
        /// External list URLs collected from ExternalList rules. Used for pre-fetching.
        /// </summary>
        public List<string> ExternalListUrls { get; } = [];

        // Computed accessors for cheap extraction groups
        public bool NeedsFileInfo => RequiredGroups.HasFlag(ExtractionGroup.FileInfo);
        public bool NeedsLibraryInfo => RequiredGroups.HasFlag(ExtractionGroup.LibraryInfo);
        public bool NeedsAudioMetadata => RequiredGroups.HasFlag(ExtractionGroup.AudioMetadata);
        public bool NeedsTextContent => RequiredGroups.HasFlag(ExtractionGroup.TextContent);

        /// <summary>
        /// Adds a parent extraction group to requirements when the expression targets the given
        /// member name and the corresponding IncludeParent* flag is explicitly true.
        /// </summary>
        private static void AddParentGroupIfIncluded(
            FieldRequirements requirements,
            string exprMemberName,
            string targetMemberName,
            bool? includeFlag,
            ExtractionGroup group)
        {
            if (exprMemberName == targetMemberName && includeFlag == true)
                requirements.RequiredGroups |= group;
        }

        /// <summary>
        /// Analyzes expression sets to determine field requirements.
        /// Uses FieldRegistry for efficient extraction group lookup.
        /// </summary>
        /// <param name="expressionSets">Filter expression sets to analyze</param>
        /// <param name="orders">Optional sorting orders to check for field requirements</param>
        public static FieldRequirements Analyze(List<ExpressionSet> expressionSets, List<Order>? orders = null, RandomGroupSelectionDto? randomGroupSelection = null)
        {
            var requirements = new FieldRequirements();

            if (randomGroupSelection?.Enabled == true && !string.IsNullOrWhiteSpace(randomGroupSelection.GroupBy))
            {
                requirements.RequiredGroups |= RandomGroupSelectionDto.GetExtractionGroup(randomGroupSelection.GroupBy);
            }

            if (expressionSets == null) return requirements;

            var allExpressions = expressionSets
                .SelectMany(set => set?.Expressions ?? [])
                .Where(expr => expr != null)
                .ToList();

            // Single pass through expressions to gather all required groups
            foreach (var expr in allExpressions)
            {
                // Get the extraction group for this field from the registry
                var group = FieldRegistry.GetExtractionGroup(expr.MemberName);
                requirements.RequiredGroups |= group;

                // Handle special cases for ancestor-inherited fields (conditional on expression flags).
                // Only add each parent group when the folded IncludeParent*Effective flag is true.
                // OnlyParent* alone does NOT trigger extraction: with no source it compiles to
                // constant-false (Engine), so requesting the walk would be wasted work.
                AddParentGroupIfIncluded(requirements, expr.MemberName, "Tags", expr.IncludeParentTagsEffective, ExtractionGroup.ParentTags);
                AddParentGroupIfIncluded(requirements, expr.MemberName, "Studios", expr.IncludeParentStudiosEffective, ExtractionGroup.ParentStudios);
                AddParentGroupIfIncluded(requirements, expr.MemberName, "Genres", expr.IncludeParentGenresEffective, ExtractionGroup.ParentGenres);

                // Collect SimilarTo expressions for reference item lookup
                if (expr.MemberName == "SimilarTo")
                    requirements.SimilarToExpressions.Add(expr);

                // Collect external list URLs for pre-fetching
                if (expr.MemberName == "ExternalList" && !string.IsNullOrWhiteSpace(expr.TargetValue))
                    requirements.ExternalListUrls.Add(expr.TargetValue);

                // Collect user IDs from user-specific rules (normalize to "N" format for consistency)
                // Skip invalid user IDs - validation will catch them later during processing
                if (!string.IsNullOrEmpty(expr.UserId) && Guid.TryParse(expr.UserId, out var guid))
                {
                    requirements.AdditionalUserIds.Add(guid.ToString("N"));
                }
            }

            // Deduplicate user IDs
            requirements.AdditionalUserIds = [.. requirements.AdditionalUserIds.Distinct()];

            // Check if certain fields are used in sorting (ensure extraction happens even if not used in rules)
            if (orders != null)
            {
                // SeriesName sort requires SeriesName extraction
                if (!requirements.RequiredGroups.HasFlag(ExtractionGroup.SeriesName))
                {
                    if (orders.Any(o => o.Name.Contains("SeriesName", StringComparison.OrdinalIgnoreCase)))
                        requirements.RequiredGroups |= ExtractionGroup.SeriesName;
                }

                // Runtime sort requires TextContent extraction (for RuntimeMinutes)
                if (!requirements.RequiredGroups.HasFlag(ExtractionGroup.TextContent))
                {
                    if (orders.Any(o => o.Name.Contains("Runtime", StringComparison.OrdinalIgnoreCase)))
                        requirements.RequiredGroups |= ExtractionGroup.TextContent;
                }

                // Artist/Album sorts require AudioMetadata extraction
                if (!requirements.RequiredGroups.HasFlag(ExtractionGroup.AudioMetadata))
                {
                    if (orders.Any(o => o.Name.Contains("Artist", StringComparison.OrdinalIgnoreCase) ||
                                       o.Name.Contains("Album", StringComparison.OrdinalIgnoreCase)))
                        requirements.RequiredGroups |= ExtractionGroup.AudioMetadata;
                }

                // LastEpisodeAirDate sort requires LastEpisodeAirDate extraction
                if (!requirements.RequiredGroups.HasFlag(ExtractionGroup.LastEpisodeAirDate))
                {
                    if (orders.Any(o => o.Name.Contains("LastEpisodeAirDate", StringComparison.OrdinalIgnoreCase)))
                        requirements.RequiredGroups |= ExtractionGroup.LastEpisodeAirDate;
                }
            }

            // Extract IncludeUnwatchedSeries parameter from NextUnwatched rules
            requirements.IncludeUnwatchedSeries = allExpressions
                .Where(e => e.MemberName == "NextUnwatched")
                .All(e => e.IncludeUnwatchedSeries != false);

            // CollectionRecursionDepth is set from list-level CollectionSearchDepth, not per-rule
            // Default to 1 for backward compatibility (matches direct children only)
            requirements.CollectionRecursionDepth = 1;

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
        /// Natural string comparer: runs of digits compare as numbers, everything else compares
        /// character by character. So "Season 2" sorts before "Season 10", and "2 Fast" before
        /// "10 Cloverfield".
        ///
        /// It walks both strings in step rather than parsing a number out of the front, because
        /// only comparing a LEADING number leaves every embedded number sorting as text -
        /// "Season 10" before "Season 2", which is the order plain string comparison gives.
        /// Digit runs are compared by trimmed length then digit-by-digit instead of being parsed
        /// into an int, so a run longer than int.MaxValue still compares correctly rather than
        /// falling back to text.
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

                int i = 0, j = 0;

                // Zero padding never outranks content ("Season 02" vs "Season 2" are equal as
                // numbers); remembered here so it can break an otherwise exact tie at the end,
                // rather than letting two different strings compare equal.
                int paddingTieBreak = 0;

                while (i < x.Length && j < y.Length)
                {
                    // char.IsDigit covers every BMP Unicode decimal digit, not just ASCII -
                    // Arabic-Indic "٢", Devanagari "२" and so on. Everything below therefore works
                    // on each digit's NUMERIC VALUE rather than its code unit, so a run in any of
                    // those scripts compares as the number it represents. Comparing code units
                    // here would order "٢" (2) after "10" and would not recognise "٠" as a
                    // leading zero.
                    //
                    // KNOWN LIMIT: decimal digits outside the BMP (surrogate pairs - mathematical
                    // alphanumerics like "𝟐", Osage, Chakma) are NOT recognised, because char.IsDigit
                    // sees only the high surrogate. Those compare as text, as they always have.
                    // Making this scalar-aware means rebuilding the whole loop - including the
                    // character comparison and case folding below - around Rune, on a comparer
                    // that runs for every name sort, to serve titles no metadata provider emits.
                    // Pinned by NaturalStringComparerTests.SupplementaryPlaneDigits_AreNotRecognised.
                    if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                    {
                        int xStart = i, yStart = j;
                        while (i < x.Length && char.IsDigit(x[i])) i++;
                        while (j < y.Length && char.IsDigit(y[j])) j++;

                        // Skip leading zeros by value, keeping at least one digit so "0" and "000"
                        // both reduce to a single zero.
                        int xFirst = xStart, yFirst = yStart;
                        while (xFirst < i - 1 && CharUnicodeInfo.GetDecimalDigitValue(x[xFirst]) == 0) xFirst++;
                        while (yFirst < j - 1 && CharUnicodeInfo.GetDecimalDigitValue(y[yFirst]) == 0) yFirst++;

                        // Fewer significant digits means a smaller number.
                        int xLength = i - xFirst, yLength = j - yFirst;
                        if (xLength != yLength)
                        {
                            return xLength < yLength ? -1 : 1;
                        }

                        for (int k = 0; k < xLength; k++)
                        {
                            var xDigit = CharUnicodeInfo.GetDecimalDigitValue(x[xFirst + k]);
                            var yDigit = CharUnicodeInfo.GetDecimalDigitValue(y[yFirst + k]);
                            if (xDigit != yDigit)
                            {
                                return xDigit < yDigit ? -1 : 1;
                            }
                        }

                        if (paddingTieBreak == 0 && (i - xStart) != (j - yStart))
                        {
                            paddingTieBreak = (i - xStart) < (j - yStart) ? -1 : 1;
                        }

                        continue;
                    }

                    // Numbers sort ahead of letters in EVERY script. ASCII gets this for free
                    // ('2' is ordinally below 'A'), but a non-ASCII digit sits far above the Latin
                    // letters in code-unit order, so "٢ Fast" would sort after "Alpha" while
                    // "2 Fast" sorts before it - digits we otherwise treat as numbers behaving
                    // like letters. Deliberately digit-vs-LETTER only: a blanket digits-first rule
                    // would also lift digits above punctuation and reorder existing ASCII titles.
                    if (char.IsDigit(x[i]) && char.IsLetter(y[j]))
                    {
                        return -1;
                    }

                    if (char.IsLetter(x[i]) && char.IsDigit(y[j]))
                    {
                        return 1;
                    }

                    var cx = _ignoreCase ? char.ToUpperInvariant(x[i]) : x[i];
                    var cy = _ignoreCase ? char.ToUpperInvariant(y[j]) : y[j];
                    if (cx != cy)
                    {
                        return cx < cy ? -1 : 1;
                    }

                    i++;
                    j++;
                }

                // One string ran out: the shorter one sorts first ("Rocky" before "Rocky 2").
                var remaining = (x.Length - i).CompareTo(y.Length - j);
                return remaining != 0 ? remaining : paddingTieBreak;
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
            if (DateUtils.TryGetPremiereDate(item, out var releaseDate))
            {
                return releaseDate;
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
