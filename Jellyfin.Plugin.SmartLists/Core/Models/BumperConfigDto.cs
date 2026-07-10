using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartLists.Core.Models
{
    /// <summary>
    /// Configuration for bumper items woven between a smart playlist's main items.
    /// The bumper pool is selected by its own rule sets and media types, ordered by
    /// <see cref="BumperOrder"/>, and one bumper is inserted after every
    /// <see cref="Interval"/> main items, cycling through the pool with wraparound.
    /// </summary>
    [Serializable]
    public class BumperConfigDto
    {
        public List<ExpressionSet> ExpressionSets { get; set; } = [];

        public List<string> MediaTypes { get; set; } = [];

        /// <summary>
        /// Order of the bumper pool: "Random" (reshuffled each refresh), "Name", or "ReleaseDate".
        /// </summary>
        public string BumperOrder { get; set; } = "Random";

        public int Interval { get; set; } = 1;
    }
}
