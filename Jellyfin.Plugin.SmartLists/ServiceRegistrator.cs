using System.Net.Http;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using MediaBrowser.Common;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Services;
using Jellyfin.Plugin.SmartLists.Services.Playlists;
using Jellyfin.Plugin.SmartLists.Services.Collections;
using Jellyfin.Plugin.SmartLists.Services.ExternalList;

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

            // Register image service for custom images
            serviceCollection.AddSingleton<SmartListImageService>();

            // Register file system and stores
            serviceCollection.AddSingleton<ISmartListFileSystem, SmartListFileSystem>();
            serviceCollection.AddSingleton<PlaylistStore>();
            serviceCollection.AddSingleton<CollectionStore>();

            // Register playlist and collection services
            serviceCollection.AddSingleton<PlaylistService>();
            serviceCollection.AddSingleton<CollectionService>();

            // Register external list services
            serviceCollection.AddSingleton<IExternalListProvider, MdbListProvider>();
            serviceCollection.AddSingleton<IExternalListProvider, ImdbListProvider>();
            serviceCollection.AddSingleton<IExternalListProvider, TraktListProvider>();
            serviceCollection.AddSingleton<IExternalListProvider, TmdbListProvider>();
            serviceCollection.AddSingleton<IExternalListProvider, LetterboxdListProvider>();
            serviceCollection.AddSingleton<IExternalListProvider, ListenBrainzListProvider>();
            serviceCollection.AddSingleton<IExternalListProvider, ScrobListProvider>();
            serviceCollection.AddSingleton<ExternalListService>();

            // Scrob is the only provider that sends a secret in a custom header (X-Api-Key) to an
            // admin-configured host. .NET strips Authorization on a cross-origin redirect but replays
            // custom headers verbatim, so an auth-proxy redirect (or a hijacked domain) would hand the
            // admin's key to a third-party host. Disable redirects: a 3xx is reported as an error instead.
            serviceCollection.AddHttpClient("Scrob")
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

            // Register backup service
            serviceCollection.AddSingleton<IBackupService, BackupService>();

            // Register scheduled tasks
            serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, CleanupTask>();
            serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, BackupTask>();

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
                var imageService = sp.GetRequiredService<SmartListImageService>();
                var externalListService = sp.GetRequiredService<ExternalListService>();
                // Optional: DB prefilters degrade conservatively without it.
                var itemRepository = sp.GetService<MediaBrowser.Controller.Persistence.IItemRepository>();

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
                    loggerFactory,
                    imageService,
                    externalListService,
                    itemRepository);

                // Set the reference in RefreshStatusService
                refreshStatusService.SetRefreshQueueService(queueService);

                return queueService;
            });
            
            // Register storage migration service BEFORE auto-refresh to ensure storage is migrated first
            serviceCollection.AddHostedService<StorageMigrationHostedService>();

            serviceCollection.AddHostedService<AutoRefreshHostedService>();
            serviceCollection.AddScoped<IManualRefreshService, ManualRefreshService>();

            // Register Plugin Pages integration as a hosted service
            serviceCollection.AddHostedService<PluginPagesRegistrationService>();
        }
    }
}

