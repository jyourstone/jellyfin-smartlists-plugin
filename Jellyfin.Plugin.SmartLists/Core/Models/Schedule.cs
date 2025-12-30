using System;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.SmartLists.Core.Enums;

namespace Jellyfin.Plugin.SmartLists.Core.Models
{
    /// <summary>
    /// Represents a single schedule configuration for a smart list.
    /// Supports multiple schedules per list for flexible scheduling.
    /// </summary>
    [Serializable]
    public class Schedule
    {
        /// <summary>
        /// The type of schedule trigger (Daily, Weekly, Monthly, Yearly, Interval)
        /// </summary>
        public ScheduleTrigger Trigger { get; set; }

        /// <summary>
        /// Time of day for Daily/Weekly/Monthly/Yearly schedules (e.g., 15:00)
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TimeSpan? Time { get; set; }

        /// <summary>
        /// Day of week for Weekly schedules (0 = Sunday, 6 = Saturday)
        /// Serialized as integer for consistency with legacy format and UI expectations
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonConverter(typeof(DayOfWeekAsIntegerConverter))]
        public DayOfWeek? DayOfWeek { get; set; }

        /// <summary>
        /// Day of month for Monthly/Yearly schedules (1-31)
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? DayOfMonth { get; set; }

        /// <summary>
        /// Month for Yearly schedules (1 = January, 12 = December)
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Month { get; set; }

        /// <summary>
        /// Interval for Interval-based schedules (e.g., 2 hours)
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TimeSpan? Interval { get; set; }

        /// <summary>
        /// Action to perform when this schedule triggers (for visibility schedules).
        /// Null for refresh schedules (backward compatibility).
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ScheduleAction? Action { get; set; }
    }
}

