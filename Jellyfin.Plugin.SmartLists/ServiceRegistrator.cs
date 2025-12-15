using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using MediaBrowser.Common;
using Jellyfin.Plugin.SmartLists.Services.Shared;

namespace Jellyfin.Plugin.SmartLists
{
    /// <summary>
    /// Service registrator for SmartLists plugin services.
    /// </summary>
    public sealed class ServiceRegistrator : IPluginServiceRegistrator
    {
        /// <summary>
        /// Registers services for the SmartLists plugin.
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <param name="applicationHost">The application host.</param>
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            // Register RefreshStatusService first
            serviceCollection.AddSingleton<RefreshStatusService>();
            
            // Register RefreshQueueService as singleton
            serviceCollection.AddSingleton<RefreshQueueService>(sp =>
            {
                var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RefreshQueueService>>();
                var userManager = sp.GetRequiredService<MediaBrowser.Controller.Library.IUserManager>();
                var libraryManager = sp.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>();
                var playlistManager = sp.GetRequiredService<MediaBrowser.Controller.Playlists.IPlaylistManager>();
                var collectionManager = sp.GetRequiredService<MediaBrowser.Controller.Collections.ICollectionManager>();
                var userDataManager = sp.GetRequiredService<MediaBrowser.Controller.Library.IUserDataManager>();
                var providerManager = sp.GetRequiredService<MediaBrowser.Controller.Providers.IProviderManager>();
                var applicationPaths = sp.GetRequiredService<MediaBrowser.Controller.IServerApplicationPaths>();
                var refreshStatusService = sp.GetRequiredService<RefreshStatusService>();
                var loggerFactory = sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
                
                var queueService = new RefreshQueueService(
                    logger,
                    userManager,
                    libraryManager,
                    playlistManager,
                    collectionManager,
                    userDataManager,
                    providerManager,
                    applicationPaths,
                    refreshStatusService,
                    loggerFactory);
                
                // Set the reference in RefreshStatusService
                refreshStatusService.SetRefreshQueueService(queueService);
                
                return queueService;
            });
            
            serviceCollection.AddHostedService<AutoRefreshHostedService>();
            serviceCollection.AddScoped<IManualRefreshService, ManualRefreshService>();
            
            // Register middleware in the HTTP pipeline using IStartupFilter
            // The middleware disables compression by removing Accept-Encoding header
            // This ensures we receive uncompressed responses that we can modify
            // Note: Don't register SidebarInjectionMiddleware in DI - UseMiddleware handles that
            serviceCollection.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, SidebarInjectionStartupFilter>();
        }
    }
}

