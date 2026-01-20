using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartLists.Services.Playlists;
using Jellyfin.Plugin.SmartLists.Services.Collections;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.Shared
{
    /// <summary>
    /// Hosted service that initializes the AutoRefreshService when Jellyfin starts.
    /// </summary>
    public class AutoRefreshHostedService : IHostedService, IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<AutoRefreshHostedService> _logger;
        private AutoRefreshService? _autoRefreshService;

        public AutoRefreshHostedService(IServiceProvider serviceProvider, ILogger<AutoRefreshHostedService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Starting SmartLists AutoRefreshService...");

                // Get required services from DI container
                var libraryManager = _serviceProvider.GetRequiredService<ILibraryManager>();
                var userManager = _serviceProvider.GetRequiredService<IUserManager>();
                var playlistManager = _serviceProvider.GetRequiredService<IPlaylistManager>();
                var collectionManager = _serviceProvider.GetRequiredService<ICollectionManager>();
                var userDataManager = _serviceProvider.GetRequiredService<IUserDataManager>();
                var providerManager = _serviceProvider.GetRequiredService<IProviderManager>();
                var serverApplicationPaths = _serviceProvider.GetRequiredService<IServerApplicationPaths>();
                var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();

                var autoRefreshLogger = loggerFactory.CreateLogger<AutoRefreshService>();
                var playlistServiceLogger = loggerFactory.CreateLogger<PlaylistService>();

                var fileSystem = new SmartListFileSystem(serverApplicationPaths);
                var playlistStore = new PlaylistStore(fileSystem);
                var playlistService = new PlaylistService(userManager, libraryManager, playlistManager, userDataManager, playlistServiceLogger, providerManager);

                var collectionServiceLogger = loggerFactory.CreateLogger<CollectionService>();
                var collectionStore = new CollectionStore(fileSystem);
                var collectionService = new CollectionService(libraryManager, collectionManager, userManager, userDataManager, collectionServiceLogger, providerManager);

                // Get RefreshStatusService from DI - it should be registered as singleton
                // Use GetRequiredService to prevent creating duplicate instances that cause split-brain state
                var refreshStatusService = _serviceProvider.GetRequiredService<RefreshStatusService>();

                // Get RefreshQueueService from DI - it's registered as singleton and required
                var refreshQueueService = _serviceProvider.GetRequiredService<RefreshQueueService>();

                _autoRefreshService = new AutoRefreshService(libraryManager, autoRefreshLogger, playlistStore, playlistService, collectionStore, collectionService, userDataManager, userManager, refreshQueueService, refreshStatusService);

                _logger.LogInformation("SmartLists AutoRefreshService started successfully (schedule timer initialized)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start AutoRefreshService");
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Stopping SmartLists AutoRefreshService...");
                _autoRefreshService?.Dispose();
                _autoRefreshService = null;
                _logger.LogInformation("SmartLists AutoRefreshService stopped successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping AutoRefreshService");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Disposes the hosted service and cleans up resources.
        /// </summary>
        public void Dispose()
        {
            _autoRefreshService?.Dispose();
            _autoRefreshService = null;
        }
    }
}
