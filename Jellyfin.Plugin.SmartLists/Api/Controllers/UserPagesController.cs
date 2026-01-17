using System;
using System.IO;
using System.Reflection;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SmartLists.Api.Controllers
{
    /// <summary>
    /// Controller for serving user-facing pages.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("Plugins/SmartLists/Pages")]
    public class UserPagesController : ControllerBase
    {
        private readonly IUserManager _userManager;

        public UserPagesController(IUserManager userManager)
        {
            _userManager = userManager;
        }

        /// <summary>
        /// Serves the user playlists management page.
        /// </summary>
        /// <returns>The HTML page for managing user playlists.</returns>
        [HttpGet("UserPlaylists")]
        public ActionResult GetUserPlaylistsPage()
        {
            // Check if user page is enabled
            var config = Plugin.Instance?.Configuration;
            var isEnabled = config?.EnableUserPage ?? false; // Default to disabled if config not available (fail closed)

            // Check if user is admin
            var userId = User.GetUserId();
            bool isAdmin = false;
            
            if (userId != Guid.Empty)
            {
                var user = _userManager.GetUserById(userId);
                isAdmin = user?.HasPermission(PermissionKind.IsAdministrator) ?? false;
            }

            // If disabled and user is not admin, show disabled page
            if (!isEnabled && !isAdmin)
            {
                return GetFeatureDisabledPage();
            }

            // If enabled, check if user is in allowed list (if list is populated)
            if (isEnabled && !isAdmin)
            {
                var allowedUsers = config?.AllowedUserPageUsers;
                if (allowedUsers != null && allowedUsers.Count > 0)
                {
                    // Parse each allowed user GUID and compare (handles both dashed and non-dashed formats)
                    bool isAllowed = false;
                    foreach (var allowedUserStr in allowedUsers)
                    {
                        if (Guid.TryParse(allowedUserStr, out var allowedUserId) && allowedUserId == userId)
                        {
                            isAllowed = true;
                            break;
                        }
                    }
                    
                    if (!isAllowed)
                    {
                        return GetFeatureDisabledPage();
                    }
                }
            }

            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "Jellyfin.Plugin.SmartLists.Configuration.user-playlists.html";
            
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return NotFound("User playlists page not found");
            }

            using var reader = new StreamReader(stream);
            var html = reader.ReadToEnd();
            
            return Content(html, "text/html");
        }

        /// <summary>
        /// Returns an HTML page indicating the feature is disabled.
        /// </summary>
        private ActionResult GetFeatureDisabledPage()
        {
            var html = @"
<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>SmartLists - Feature Disabled</title>
    <style>
        body {
            margin: 0;
            padding: 0;
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, Cantarell, sans-serif;
            background-color: #101010;
            color: #ffffff;
            display: flex;
            align-items: center;
            justify-content: center;
            min-height: 100vh;
        }
        .container {
            max-width: 600px;
            padding: 40px;
            text-align: center;
        }
        .icon {
            font-size: 80px;
            margin-bottom: 20px;
            opacity: 0.6;
        }
        h1 {
            font-size: 28px;
            font-weight: 500;
            margin: 0 0 16px 0;
            color: #ffffff;
        }
        p {
            font-size: 16px;
            line-height: 1.6;
            margin: 0 0 24px 0;
            color: #cccccc;
        }
        .info-box {
            background-color: #1a1a1a;
            border: 1px solid #333333;
            border-radius: 8px;
            padding: 20px;
            margin-top: 30px;
            text-align: left;
        }
        .info-box h2 {
            font-size: 18px;
            font-weight: 500;
            margin: 0 0 12px 0;
            color: var(--jf-palette-primary-main);
        }
        .info-box p {
            margin: 0;
            font-size: 14px;
        }
        .back-link {
            display: inline-block;
            margin-top: 24px;
            color: var(--jf-palette-primary-main);
            text-decoration: none;
            font-size: 14px;
            font-weight: 500;
        }
        .back-link:hover {
            text-decoration: underline;
        }
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""icon"">🚫</div>
        <h1>SmartLists User Page is currently disabled</h1>
        <p>
            The SmartLists user page has been disabled by your server administrator. 
            This feature allows regular users to create and manage their own smart playlists and collections.
        </p>
        
        <div class=""info-box"">
            <h2>What can I do?</h2>
            <p>
                Please contact your Jellyfin administrator to request access to SmartLists. 
                Administrators can manage access in the SmartLists plugin settings.
            </p>
        </div>

        <a href=""/"" class=""back-link"">← Back to Home</a>
    </div>
</body>
</html>";
            
            return Content(html, "text/html; charset=utf-8");
        }
    }
}
