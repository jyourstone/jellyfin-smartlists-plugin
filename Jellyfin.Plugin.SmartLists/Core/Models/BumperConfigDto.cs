using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartLists.Core.Models
{
    /// <summary>
    /// Configuration for bumper items woven between a smart playlist's main items.
    /// The bumper pool is selected by its own rule sets and media types, ordered by
    /// <see cref="BumperOrder"/>, and a break of <see cref="BreakSize"/> bumpers is inserted after every
    /// <see cref="Interval"/> main items, cycling through the pool with wraparound.
    /// </summary>
    [Serializable]
    public class BumperConfigDto
    {
        /// <summary>
        /// Upper bound for <see cref="BreakSize"/>, shared by save-time validation and the refresh-time clamp.
        /// </summary>
        public const int MaxBreakSize = 10;

        public List<ExpressionSet> ExpressionSets { get; set; } = [];

        public List<string> MediaTypes { get; set; } = [];

        /// <summary>
        /// Order of the bumper pool: "Random" (reshuffled each refresh), "Name", or "ReleaseDate".
        /// </summary>
        public string BumperOrder { get; set; } = "Random";

        public int Interval { get; set; } = 1;

        /// <summary>
        /// Number of bumpers in each break (1-<see cref="MaxBreakSize"/>). Lists saved before this setting
        /// existed have no value stored and deserialize to 1, the original one-bumper break.
        /// </summary>
        public int BreakSize { get; set; } = 1;

        /// <summary>
        /// When true and there are two or more rule groups, each rule group forms its own pool and the
        /// bumpers of a break take turns between them (see PlaylistService.WeaveBumpers). When false, all
        /// rule groups are OR-merged into one pool.
        /// </summary>
        public bool AlternateRuleGroups { get; set; }
    }
}
