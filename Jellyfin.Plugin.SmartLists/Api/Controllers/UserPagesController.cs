using System.IO;
using System.Reflection;
using Jellyfin.Plugin.SmartLists.Utilities;
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
            if (UserPageAccess.Evaluate(_userManager, User.GetUserId(), Plugin.Instance?.Configuration) != UserPageAccessResult.Allowed)
            {
                return GetFeatureDisabledPage();
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
    <!-- Plugin Pages appends this markup into the Jellyfin web client's own DOM, keeping the style
         element, so every rule is scoped to .smartlists-disabled and colours come from the theme. -->
    <style>
        .smartlists-disabled {
            max-width: 600px;
            margin: 0 auto;
            padding: 40px 0;
            text-align: center;
        }
        .smartlists-disabled .icon {
            font-size: 80px;
            margin-bottom: 20px;
            opacity: 0.6;
        }
        .smartlists-disabled h1 {
            font-size: 28px;
            font-weight: 500;
            margin: 0 0 16px 0;
        }
        .smartlists-disabled p {
            font-size: 16px;
            line-height: 1.6;
            margin: 0 0 24px 0;
            opacity: 0.8;
        }
        .smartlists-disabled .info-box {
            background: var(--jf-palette-background-paper);
            border: 1px solid var(--jf-palette-divider);
            border-radius: 8px;
            padding: 20px;
            margin-top: 30px;
            text-align: left;
        }
        .smartlists-disabled .info-box h2 {
            font-size: 18px;
            font-weight: 500;
            margin: 0 0 12px 0;
            color: var(--jf-palette-primary-main);
        }
        .smartlists-disabled .info-box p {
            margin: 0;
            font-size: 14px;
        }
        .smartlists-disabled .back-link {
            display: inline-block;
            margin-top: 24px;
            color: var(--jf-palette-primary-main);
            text-decoration: none;
            font-size: 14px;
            font-weight: 500;
        }
        .smartlists-disabled .back-link:hover {
            text-decoration: underline;
        }
    </style>
</head>
<body>
    <div class=""smartlists-disabled"">
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
