using System;
using System.Linq;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services
{
    /// <summary>
    /// Registers the SmartLists user page with Plugin Pages on startup and decides per request whether its sidebar entry is shown.
    /// </summary>
    public class PluginPagesRegistrationService : IHostedService
    {
        // Plugin Pages calls IsUserPageMenuVisible statically; it reaches the injected services through this.
        private static PluginPagesRegistrationService? _instance;
        private readonly ILogger<PluginPagesRegistrationService> _logger;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IUserManager _userManager;

        public PluginPagesRegistrationService(
            ILogger<PluginPagesRegistrationService> logger,
            IHttpContextAccessor httpContextAccessor,
            IUserManager userManager)
        {
            _logger = logger;
            _httpContextAccessor = httpContextAccessor;
            _userManager = userManager;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _instance = this;
            RegisterPage();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Plugin Pages visibility callback for the sidebar entry, run on every GET /PluginPages/User
        /// (once per web-client load). Plugin Pages finds it by reflection from the names registered in
        /// RegisterPage, so it must stay public static, take one string, return bool and have no overloads.
        /// It must never throw: Plugin Pages doesn't catch, so an exception fails that request and drops
        /// every plugin's sidebar entries.
        /// </summary>
        /// <param name="pageId">Id of the page being evaluated (always ours).</param>
        /// <returns>True when the current user can use the user page.</returns>
        public static bool IsUserPageMenuVisible(string pageId)
        {
            var service = _instance;
            try
            {
                // No identifiable user (e.g. an unauthenticated request) can't open the [Authorize] page
                var userId = service?._httpContextAccessor.HttpContext?.User.GetUserId() ?? Guid.Empty;
                return service != null && userId != Guid.Empty && UserPageAccess.Evaluate(
                    service._userManager,
                    userId,
                    Plugin.Instance?.Configuration) == UserPageAccessResult.Allowed;
            }
            catch (Exception ex)
            {
                service?._logger.LogWarning(ex, "Could not check SmartLists user page access for the sidebar");
                return false;
            }
        }

        private void RegisterPage()
        {
            try
            {
                // Try to find the Plugin Pages assembly
                var pluginPagesAssembly = AssemblyLoadContext.All
                    .SelectMany(x => x.Assemblies)
                    .FirstOrDefault(x => x.FullName?.Contains("Jellyfin.Plugin.PluginPages") ?? false);

                if (pluginPagesAssembly == null)
                {
                    _logger.LogInformation("Plugin Pages plugin not found. User pages will be available via direct URL only at: /Plugins/SmartLists/Pages/UserPlaylists");
                    return;
                }

                var registerPage = pluginPagesAssembly.GetType("Jellyfin.Plugin.PluginPages.PluginInterface")?.GetMethod("RegisterPage");
                // RegisterPage takes a Newtonsoft JObject. SmartLists doesn't reference Newtonsoft, so the JObject is
                // built through the parameter's own type: JObject.Parse(json). Parse is overloaded, hence the explicit signature.
                var parse = registerPage?.GetParameters().SingleOrDefault()?.ParameterType.GetMethod("Parse", [typeof(string)]);
                if (registerPage == null || parse == null)
                {
                    _logger.LogWarning("Plugin Pages does not provide PluginInterface.RegisterPage. Update Plugin Pages to 3.0.0.0 or later to show SmartLists in the sidebar.");
                    return;
                }

                var json = JsonSerializer.Serialize(new
                {
                    Id = "smartlists-user-playlists",
                    Url = "/Plugins/SmartLists/Pages/UserPlaylists",
                    DisplayText = "SmartLists",
                    Icon = "playlist_play",
                    // Plugin Pages calls IsEnabledClass.IsEnabledMethod(pageId) in IsEnabledAssembly per request to decide visibility.
                    // It matches with FullName.Contains: the trailing comma pins our assembly without sending the version
                    // (/PluginPages/User returns this to anyone).
                    IsEnabledAssembly = typeof(PluginPagesRegistrationService).Assembly.GetName().Name + ",",
                    IsEnabledClass = nameof(PluginPagesRegistrationService),
                    IsEnabledMethod = nameof(IsUserPageMenuVisible),
                });
                registerPage.Invoke(null, [parse.Invoke(null, [json])]);
                _logger.LogInformation("SmartLists user page registered with Plugin Pages successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registering SmartLists page with Plugin Pages");
            }
        }
    }
}
