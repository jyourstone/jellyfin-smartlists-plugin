using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.SmartLists.Core.Enums;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;

namespace Jellyfin.Plugin.SmartLists.Core.Models
{
    /// <summary>
    /// Base class for all smart lists (Playlists and Collections)
    /// Contains all shared properties and logic
    /// </summary>
    [Serializable]
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "Type")]
    [JsonDerivedType(typeof(SmartPlaylistDto), typeDiscriminator: "Playlist")]
    [JsonDerivedType(typeof(SmartCollectionDto), typeDiscriminator: "Collection")]
    public abstract class SmartListDto
    {
        /// <summary>
        /// Type discriminator - determines if this is a Playlist or Collection
        /// </summary>
        public SmartListType Type { get; set; }

        // Core identification
        // Id is optional for creation (generated if not provided)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; set; }
        public required string Name { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FileName { get; set; }
        
        /// <summary>
        /// Owner user ID - the user this list belongs to or whose context is used for rule evaluation
        /// For playlists using UserPlaylists array, this will be null. For collections, this contains the owner user ID.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? UserId { get; set; }

        /// <summary>
        /// User ID of the user who created this list
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CreatedByUserId { get; set; }

        // Query and filtering
        public List<ExpressionSet> ExpressionSets { get; set; } = [];
        // Order is optional for creation (initialized if not provided)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OrderDto? Order { get; set; }

        private List<string> _mediaTypes = [];

        /// <summary>
        /// Pre-filter media types with validation to prevent corruption
        /// </summary>
        public List<string> MediaTypes
        {
            get => _mediaTypes;
            set
            {
                var source = value ?? [];
                // Keep only known types and remove duplicates (ordinal)
                // Filter out nulls before ContainsKey check to prevent ArgumentNullException
                _mediaTypes = source
                    .Where(mt => mt != null && Core.Constants.MediaTypes.MediaTypeToBaseItemKind.ContainsKey(mt))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }
        }

        /// <summary>
        /// Whether to include extras (behind the scenes, deleted scenes, featurettes, trailers, etc.)
        /// in the item pool. When false (default), only regular library items are included.
        /// When true, extras attached to parent items are also queried and available for rule evaluation.
        /// </summary>
        public bool IncludeExtras { get; set; } = false;

        // State and limits
        public bool Enabled { get; set; } = true; // Default to enabled
        public int? MaxItems { get; set; } // Nullable to support backwards compatibility
        public int? MaxPlayTimeMinutes { get; set; } // Nullable to support backwards compatibility

        // Auto-refresh
        public AutoRefreshMode AutoRefresh { get; set; } = AutoRefreshMode.Never; // Default to never for backward compatibility

        // Scheduling
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<Schedule> Schedules { get; set; } = [];

        // Visibility Scheduling
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<Schedule> VisibilitySchedules { get; set; } = [];

        // Timestamps
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? LastRefreshed { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? DateCreated { get; set; }

        // Statistics (calculated during refresh)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? ItemCount { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? TotalRuntimeMinutes { get; set; }

        // Similarity comparison fields
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string> SimilarityComparisonFields { get; set; } = [];

        /// <summary>
        /// Custom images uploaded by the user.
        /// Key = ImageType name (Primary, Backdrop, Banner, etc.)
        /// Value = Filename stored in the images folder
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string>? CustomImages { get; set; }

        /// <summary>
        /// Custom sort title applied to the Jellyfin playlist/collection.
        /// Overrides the auto-generated sort name when set.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SortTitle { get; set; }

        /// <summary>
        /// Custom overview/description applied to the Jellyfin playlist/collection.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Overview { get; set; }

        /// <summary>
        /// Migrates legacy IsPlayed rules to PlaybackStatus.
        /// Called after deserialization.
        /// </summary>
        public void MigrateLegacyFields()
        {
            if (ExpressionSets != null)
            {
                foreach (var expressionSet in ExpressionSets)
                {
                    if (expressionSet.Expressions != null)
                    {
                        foreach (var expression in expressionSet.Expressions)
                        {
                            if (expression.MemberName == "IsPlayed")
                            {
                                expression.MemberName = "PlaybackStatus";
                                expression.TargetValue = expression.TargetValue == "true" ? "Played" : "Unplayed";
                            }
                        }
                    }
                }
            }
        }
    }
}

