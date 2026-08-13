using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartLists.Core.Enums
{
    /// <summary>
    /// How the User-Agent header for external list requests is chosen.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum UserAgentMode
    {
        /// <summary>
        /// Each provider uses its own built-in default.
        /// </summary>
        Default,

        /// <summary>
        /// Use the admin's browser User-Agent, re-captured on every admin page load.
        /// </summary>
        AutoClone,

        /// <summary>
        /// Use the admin's browser User-Agent captured when settings were last saved.
        /// </summary>
        Clone,

        /// <summary>
        /// Use the custom value entered in settings.
        /// </summary>
        Custom,
    }
}
