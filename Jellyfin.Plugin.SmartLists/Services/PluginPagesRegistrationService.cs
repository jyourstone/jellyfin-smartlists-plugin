using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services
{
    /// <summary>
    /// Service to register user pages with Plugin Pages plugin on startup.
    /// </summary>
    public class PluginPagesRegistrationService : IHostedService
    {
        private readonly ILogger<PluginPagesRegistrationService> _logger;
        private readonly IServiceProvider _serviceProvider;

        public PluginPagesRegistrationService(ILogger<PluginPagesRegistrationService> logger, IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            RegisterPages();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private void RegisterPages()
        {
            try
            {
                // Always register the menu item - access control is handled at the page/API level
                // This ensures users see the menu item even if they don't have access
                // (they'll see a nice disabled page instead of a 404)

                // Try to find the Plugin Pages assembly
                var pluginPagesAssembly = AssemblyLoadContext.All
                    .SelectMany(x => x.Assemblies)
                    .FirstOrDefault(x => x.FullName?.Contains("Jellyfin.Plugin.PluginPages") ?? false);

                if (pluginPagesAssembly == null)
                {
                    _logger.LogInformation("Plugin Pages plugin not found. User pages will be available via direct URL only at: /Plugins/SmartLists/Pages/UserPlaylists");
                    return;
                }

                // Get the IPluginPagesManager type and PluginPage type
                var managerInterfaceType = pluginPagesAssembly.GetType("Jellyfin.Plugin.PluginPages.Library.IPluginPagesManager");
                var pluginPageType = pluginPagesAssembly.GetType("Jellyfin.Plugin.PluginPages.Library.PluginPage");

                if (managerInterfaceType == null || pluginPageType == null)
                {
                    _logger.LogWarning("Could not find Plugin Pages types. Skipping registration.");
                    return;
                }

                // Try to get the IPluginPagesManager from DI
                var getServiceMethod = typeof(ServiceProviderServiceExtensions).GetMethod(
                    nameof(ServiceProviderServiceExtensions.GetService),
                    new[] { typeof(IServiceProvider) });
                
                if (getServiceMethod == null)
                {
                    _logger.LogWarning("Could not find GetService method.");
                    return;
                }

                var genericGetService = getServiceMethod.MakeGenericMethod(managerInterfaceType);
                var manager = genericGetService.Invoke(null, new object[] { _serviceProvider });

                if (manager == null)
                {
                    _logger.LogWarning("Plugin Pages manager not found in DI. Skipping registration.");
                    return;
                }

                // Create a PluginPage instance
                var page = Activator.CreateInstance(pluginPageType);
                if (page == null)
                {
                    _logger.LogWarning("Could not create PluginPage instance.");
                    return;
                }

                // Set properties using reflection
                var idProperty = pluginPageType.GetProperty("Id");
                var urlProperty = pluginPageType.GetProperty("Url");
                var displayTextProperty = pluginPageType.GetProperty("DisplayText");
                var iconProperty = pluginPageType.GetProperty("Icon");

                idProperty?.SetValue(page, "smartlists-user-playlists");
                urlProperty?.SetValue(page, "/Plugins/SmartLists/Pages/UserPlaylists");
                displayTextProperty?.SetValue(page, "SmartLists");
                iconProperty?.SetValue(page, "playlist_play");

                // Call RegisterPluginPage on the manager
                var registerMethod = managerInterfaceType.GetMethod("RegisterPluginPage");
                if (registerMethod != null)
                {
                    registerMethod.Invoke(manager, new[] { page });
                    _logger.LogInformation("SmartLists user page registered with Plugin Pages successfully");
                }
                else
                {
                    _logger.LogWarning("Could not find RegisterPluginPage method.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registering SmartLists page with Plugin Pages");
            }
        }
    }
}
