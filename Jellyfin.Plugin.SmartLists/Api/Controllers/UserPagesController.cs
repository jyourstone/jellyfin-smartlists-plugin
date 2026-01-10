using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SmartLists.Api.Controllers
{
    /// <summary>
    /// Controller for serving user-facing pages.
    /// </summary>
    [ApiController]
    [Route("Plugins/SmartLists/Pages")]
    public class UserPagesController : ControllerBase
    {
        /// <summary>
        /// Serves the user playlists management page.
        /// </summary>
        /// <returns>The HTML page for managing user playlists.</returns>
        [HttpGet("UserPlaylists")]
        public ActionResult GetUserPlaylistsPage()
        {
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
    }
}
