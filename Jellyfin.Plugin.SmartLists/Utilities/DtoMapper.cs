using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SmartLists.Core.Models;

namespace Jellyfin.Plugin.SmartLists.Utilities
{
    /// <summary>
    /// Provides efficient, type-safe mapping between SmartList DTO types.
    /// Eliminates the JSON serialization anti-pattern while preserving all base class properties.
    /// </summary>
    public static class DtoMapper
    {
        /// <summary>
        /// Maps a SmartListDto (or derived type) to SmartPlaylistDto.
        /// Preserves all base class properties and handles null safety.
        /// </summary>
        /// <param name="source">The source SmartListDto to map from.</param>
        /// <returns>A SmartPlaylistDto with all properties copied from the source.</returns>
        public static SmartPlaylistDto ToPlaylistDto(SmartListDto source)
        {
            ArgumentNullException.ThrowIfNull(source);
            
            // If already correct type, return as-is
            if (source is SmartPlaylistDto playlist)
            {
                return playlist;
            }
            
            // Map all base properties
            var target = new SmartPlaylistDto
            {
                // Core identification
                Id = source.Id,
                Name = source.Name,
                FileName = source.FileName,
                UserId = source.UserId,
                CreatedByUserId = source.CreatedByUserId,
                Type = Core.Enums.SmartListType.Playlist,
                
                // Query and filtering
                ExpressionSets = source.ExpressionSets,
                Order = source.Order,
                MediaTypes = source.MediaTypes,
                
                // State and limits
                Enabled = source.Enabled,
                MaxItems = source.MaxItems,
                MaxPlayTimeMinutes = source.MaxPlayTimeMinutes,
                
                // Auto-refresh
                AutoRefresh = source.AutoRefresh,
                
                // Scheduling
                Schedules = source.Schedules,
                VisibilitySchedules = source.VisibilitySchedules,
                
                // Timestamps
                LastRefreshed = source.LastRefreshed,
                DateCreated = source.DateCreated,
                
                // Statistics
                ItemCount = source.ItemCount,
                TotalRuntimeMinutes = source.TotalRuntimeMinutes,
                
                // Similarity comparison
                SimilarityComparisonFields = source.SimilarityComparisonFields,
                
                // Playlist-specific (initialize to defaults)
                JellyfinPlaylistId = null,
                Public = false,
                UserPlaylists = null
            };
            
            return target;
        }
        
        /// <summary>
        /// Maps a SmartListDto (or derived type) to SmartCollectionDto.
        /// Preserves all base class properties and handles null safety.
        /// </summary>
        /// <param name="source">The source SmartListDto to map from.</param>
        /// <returns>A SmartCollectionDto with all properties copied from the source.</returns>
        public static SmartCollectionDto ToCollectionDto(SmartListDto source)
        {
            ArgumentNullException.ThrowIfNull(source);
            
            // If already correct type, return as-is
            if (source is SmartCollectionDto collection)
            {
                return collection;
            }
            
            // Map all base properties
            var target = new SmartCollectionDto
            {
                // Core identification
                Id = source.Id,
                Name = source.Name,
                FileName = source.FileName,
                UserId = source.UserId,
                CreatedByUserId = source.CreatedByUserId,
                Type = Core.Enums.SmartListType.Collection,
                
                // Query and filtering
                ExpressionSets = source.ExpressionSets,
                Order = source.Order,
                MediaTypes = source.MediaTypes,
                
                // State and limits
                Enabled = source.Enabled,
                MaxItems = source.MaxItems,
                MaxPlayTimeMinutes = source.MaxPlayTimeMinutes,
                
                // Auto-refresh
                AutoRefresh = source.AutoRefresh,
                
                // Scheduling
                Schedules = source.Schedules,
                VisibilitySchedules = source.VisibilitySchedules,
                
                // Timestamps
                LastRefreshed = source.LastRefreshed,
                DateCreated = source.DateCreated,
                
                // Statistics
                ItemCount = source.ItemCount,
                TotalRuntimeMinutes = source.TotalRuntimeMinutes,
                
                // Similarity comparison
                SimilarityComparisonFields = source.SimilarityComparisonFields,
                
                // Collection-specific (will be set by caller)
                JellyfinCollectionId = null
            };
            
            return target;
        }
    }
}
