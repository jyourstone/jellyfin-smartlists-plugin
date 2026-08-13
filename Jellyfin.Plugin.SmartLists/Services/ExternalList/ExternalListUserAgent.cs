using Jellyfin.Plugin.SmartLists.Core.Enums;

namespace Jellyfin.Plugin.SmartLists.Services.ExternalList
{
    /// <summary>
    /// Resolves the User-Agent header for external list requests, honoring the
    /// User-Agent override configured in Settings > External Lists.
    /// </summary>
    public static class ExternalListUserAgent
    {
        /// <summary>
        /// Returns the configured User-Agent override, or <paramref name="providerDefault"/>
        /// when the mode is Default or no override value is available yet.
        /// </summary>
        /// <param name="providerDefault">The provider's built-in User-Agent.</param>
        /// <returns>The User-Agent to send.</returns>
        public static string Resolve(string providerDefault)
        {
            var config = Plugin.Instance?.Configuration;
            var overrideValue = config?.UserAgentMode switch
            {
                UserAgentMode.Custom => config.CustomUserAgent,
                UserAgentMode.Clone or UserAgentMode.AutoClone => config.ClonedUserAgent,
                _ => null,
            };

            return string.IsNullOrWhiteSpace(overrideValue) ? providerDefault : overrideValue.Trim();
        }
    }
}
