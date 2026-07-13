namespace Jellyfin.Plugin.SmartLists.Core.Constants
{
    /// <summary>
    /// Provider ID keys stamped on Jellyfin items by this plugin.
    /// </summary>
    public static class ProviderKeys
    {
        /// <summary>
        /// Tethers a Jellyfin playlist/collection to its smart list DTO ID, so the item can be
        /// recovered when the stored Jellyfin item ID goes stale, and so duplicates provably
        /// created by this plugin can be cleaned up safely.
        /// </summary>
        public const string SmartLists = "SmartLists";
    }
}
