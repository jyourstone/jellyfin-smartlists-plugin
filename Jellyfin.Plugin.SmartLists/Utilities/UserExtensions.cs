using System;

namespace Jellyfin.Plugin.SmartLists.Utilities
{
    /// <summary>
    /// Extension methods for user claims.
    /// </summary>
    internal static class UserExtensions
    {
        public static Guid GetUserId(this System.Security.Claims.ClaimsPrincipal user)
        {
            // Jellyfin uses the "Jellyfin-UserId" claim for authenticated users
            var userIdClaim = user.FindFirst("Jellyfin-UserId");
            if (userIdClaim != null && Guid.TryParse(userIdClaim.Value, out var userId))
            {
                return userId;
            }
            return Guid.Empty;
        }
    }
}
