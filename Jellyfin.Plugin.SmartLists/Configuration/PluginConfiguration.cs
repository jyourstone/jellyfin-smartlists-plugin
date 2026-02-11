using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SmartLists.Core.Enums;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SmartLists.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets the default sort order for new playlists.
        /// </summary>
        public string DefaultSortBy { get; set; } = "Name";

        /// <summary>
        /// Gets or sets the default sort direction for new playlists.
        /// </summary>
        public string DefaultSortOrder { get; set; } = "Ascending";

        /// <summary>
        /// Gets or sets the default list type for new lists (Playlist or Collection).
        /// </summary>
        public SmartListType DefaultListType { get; set; } = SmartListType.Playlist;

        /// <summary>
        /// Gets or sets whether new playlists should be public by default.
        /// </summary>
        public bool DefaultMakePublic { get; set; } = false;

        /// <summary>
        /// Gets or sets the default maximum number of items for new playlists.
        /// </summary>
        public int DefaultMaxItems { get; set; } = 500;

        /// <summary>
        /// Gets or sets the default maximum playtime in minutes for new playlists.
        /// </summary>
        public int DefaultMaxPlayTimeMinutes { get; set; } = 0;

        /// <summary>
        /// Gets or sets the prefix text to add to playlist names.
        /// Leave empty to not add a prefix.
        /// </summary>
        public string PlaylistNamePrefix { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the suffix text to add to playlist names.
        /// Leave empty to not add a suffix.
        /// </summary>
        public string PlaylistNameSuffix { get; set; } = "[Smart]";

        /// <summary>
        /// Gets or sets the default auto-refresh mode for new playlists.
        /// </summary>
        public AutoRefreshMode DefaultAutoRefresh { get; set; } = AutoRefreshMode.OnLibraryChanges;

        /// <summary>
        /// Gets or sets the default schedule trigger for new playlists.
        /// </summary>
        public ScheduleTrigger? DefaultScheduleTrigger { get; set; } = null; // No schedule by default

        /// <summary>
        /// Gets or sets the default schedule time for Daily/Weekly triggers.
        /// </summary>
        public TimeSpan DefaultScheduleTime { get; set; } = TimeSpan.FromHours(0); // Midnight (00:00) default

        /// <summary>
        /// Gets or sets the default day of week for Weekly triggers.
        /// </summary>
        public DayOfWeek DefaultScheduleDayOfWeek { get; set; } = DayOfWeek.Sunday; // Sunday default

        /// <summary>
        /// Gets or sets the default day of month for Monthly/Yearly triggers.
        /// </summary>
        public int DefaultScheduleDayOfMonth { get; set; } = 1; // 1st of month default

        /// <summary>
        /// Gets or sets the default month for Yearly triggers.
        /// </summary>
        public int DefaultScheduleMonth { get; set; } = 1; // January default

        /// <summary>
        /// Gets or sets the default interval for Interval triggers.
        /// </summary>
        public TimeSpan DefaultScheduleInterval { get; set; } = TimeSpan.FromMinutes(15); // 15 minutes default


        private int _processingBatchSize = 300;

        /// <summary>
        /// Gets or sets the processing batch size for list refreshes.
        /// Items are processed in batches of this size for memory management and progress reporting.
        /// Minimum value: 1
        /// Default: 300
        /// </summary>
        public int ProcessingBatchSize
        {
            get => _processingBatchSize;
            set => _processingBatchSize = value < 1 ? 300 : value;
        }

        /// <summary>
        /// Gets or sets whether the user-facing configuration page is enabled.
        /// When enabled, regular users can access SmartLists from their home screen sidebar.
        /// Requires Plugin Pages and File Transformation plugins to be installed.
        /// Default: true
        /// </summary>
        public bool EnableUserPage { get; set; } = true;

        private List<string>? _allowedUserPageUsers = null;

        /// <summary>
        /// Gets or sets the list of user IDs that have access to the user page.
        /// If empty or null, all users have access (when EnableUserPage is true).
        /// Admins always have access regardless of this setting.
        /// User IDs are automatically normalized to lowercase for consistent matching.
        /// </summary>
        public List<string>? AllowedUserPageUsers
        {
            get => _allowedUserPageUsers;
            set
            {
                _allowedUserPageUsers = value?.Select(id => id.ToLowerInvariant()).ToList();
            }
        }

        // ===== External List Settings =====

        /// <summary>
        /// Gets or sets the MDBList API key for fetching external lists.
        /// Free API keys can be obtained from https://mdblist.com/preferences/
        /// </summary>
        public string? MdbListApiKey { get; set; } = null;

        /// <summary>
        /// Gets or sets the Trakt client ID for fetching external lists.
        /// Create a free app at https://trakt.tv/oauth/applications to get a client ID.
        /// </summary>
        public string? TraktClientId { get; set; } = null;

        /// <summary>
        /// Gets or sets the TMDB API key for fetching external lists.
        /// Get a free API key at https://www.themoviedb.org/settings/api
        /// </summary>
        public string? TmdbApiKey { get; set; } = null;

        // ===== Backup Settings =====

        /// <summary>
        /// Gets or sets whether automated daily backups are enabled.
        /// Default: false
        /// </summary>
        public bool BackupEnabled { get; set; } = false;

        /// <summary>
        /// Gets or sets the number of backup files to retain.
        /// Older backups beyond this limit will be automatically deleted.
        /// Default: 7 (keep 7 most recent backups)
        /// </summary>
        public int BackupRetentionCount { get; set; } = 7;

        /// <summary>
        /// Gets or sets the custom backup path.
        /// If null or empty, uses the default path: {DataPath}/smartlists/backups/
        /// </summary>
        public string? BackupCustomPath { get; set; } = null;
    }
}