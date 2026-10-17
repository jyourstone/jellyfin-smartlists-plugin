using System;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SmartLists.Configuration;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SmartLists.Utilities
{
    /// <summary>
    /// Outcome of <see cref="UserPageAccess.Evaluate"/>.
    /// </summary>
    internal enum UserPageAccessResult
    {
        Allowed,
        PageDisabled,
        UserNotAllowed,
    }

    /// <summary>
    /// The one rule for who may use the SmartLists user page, shared by the page itself, the user API
    /// and the Plugin Pages sidebar entry: admins always; everyone else only while the page is enabled
    /// and, when an allowed-users list is set, only the users on it.
    /// </summary>
    internal static class UserPageAccess
    {
        /// <summary>
        /// Evaluates the rule for a user. <see cref="Guid.Empty"/> (no identifiable user) is treated as a
        /// non-admin who isn't on any allowed-users list.
        /// </summary>
        public static UserPageAccessResult Evaluate(IUserManager userManager, Guid userId, PluginConfiguration? config)
        {
            // Admins always have access, even if the user page is disabled (GetUserById throws on Guid.Empty)
            if (userId != Guid.Empty
                && (userManager.GetUserById(userId)?.HasPermission(PermissionKind.IsAdministrator) ?? false))
            {
                return UserPageAccessResult.Allowed;
            }

            // Default to disabled if config not available (fail closed)
            if (config == null || !config.EnableUserPage)
            {
                return UserPageAccessResult.PageDisabled;
            }

            var allowedUsers = config.AllowedUserPageUsers;
            if (allowedUsers == null || allowedUsers.Count == 0)
            {
                return UserPageAccessResult.Allowed;
            }

            // Parse each allowed user GUID and compare (handles both dashed and non-dashed formats)
            return allowedUsers.Any(id => Guid.TryParse(id, out var allowedId) && allowedId == userId)
                ? UserPageAccessResult.Allowed
                : UserPageAccessResult.UserNotAllowed;
        }
    }
}
