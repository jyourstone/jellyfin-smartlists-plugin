using System;
using System.Collections.Generic;
using System.Reflection;
using Jellyfin.Plugin.SmartLists.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SmartLists
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1724:Type names should not match namespaces", Justification = "Plugin class name is required by Jellyfin plugin system")]
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IDisposable
    {
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            
            // Register assembly resolver to help .NET find ImageSharp DLL
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        /// <summary>
        /// Disposes the plugin and unsubscribes from events.
        /// </summary>
        public void Dispose()
        {
            AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
            Instance = null;
        }

        public override Guid Id => Guid.Parse("A0A2A7B2-747A-4113-8B39-757A9D267C79");
        public override string Name => "SmartLists";
        public override string Description => "Create smart, rule-based playlists and collections in Jellyfin.";

        /// <summary>
        /// Gets the current plugin instance.
        /// </summary>
        public static Plugin? Instance { get; private set; }

        /// <summary>
        /// Resolves assembly loading for SixLabors.ImageSharp.
        /// </summary>
        private Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
        {
            // Only handle ImageSharp assembly
            var assemblyName = new AssemblyName(args.Name);
            if (!assemblyName.Name!.Equals("SixLabors.ImageSharp", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Try to load from the plugin directory
            var pluginDirectory = System.IO.Path.GetDirectoryName(GetType().Assembly.Location);
            if (string.IsNullOrEmpty(pluginDirectory))
            {
                return null;
            }

            var imageSharpPath = System.IO.Path.Combine(pluginDirectory, "SixLabors.ImageSharp.dll");
            if (System.IO.File.Exists(imageSharpPath))
            {
                return Assembly.LoadFrom(imageSharpPath);
            }

            return null;
        }

        /// <summary>
        /// Gets the plugin's web pages.
        /// </summary>
        /// <returns>The web pages.</returns>
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return [
                new PluginPageInfo
                {
                    Name = Name,
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html",
                },
                // Sidebar injection script (attempts to add menu item to dashboard sidebar)
                new PluginPageInfo
                {
                    Name = "sidebar.js",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.sidebar.js",
                },
                // Core utilities and constants (must load first)
                new PluginPageInfo
                {
                    Name = "config-core.js",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config-core.js",
                },
                // Formatters and option generators
                new PluginPageInfo
                {
                    Name = "config-formatters.js",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config-formatters.js",
                },
                // Schedule management
                new PluginPageInfo
                {
                    Name = "config-schedules.js",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config-schedules.js",
                },
                // Sort management
                new PluginPageInfo
                {
                    Name = "config-sorts.js",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config-sorts.js",
                },
                // Generic multi-select component
                new PluginPageInfo
                {
                    Name = "config-multi-select.js",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config-multi-select.js",
                },
                // Multi-select component CSS
                new PluginPageInfo
                {
                    Name = "config-multi-select.css",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config-multi-select.css",
                },
                // User selection component
                new PluginPageInfo
                {
                    Name = "config-user-select.js",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config-user-select.js",
                },
                // Rule management
                new PluginPageInfo
                {
                    Name = "config-rules.js",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config-rules.js",
                },
                // Playlist CRUD operations
                new PluginPageInfo
                {
                    Name = "config-lists.js",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config-lists.js",
                },
                // Filtering and search
                new PluginPageInfo
                {
                    Name = "config-filters.js",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config-filters.js",
                },
                // Bulk actions
                new PluginPageInfo
                {
                    Name = "config-bulk-actions.js",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config-bulk-actions.js",
                },
                // Status page
                new PluginPageInfo
                {
                    Name = "config-status.js",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config-status.js",
                },
                // API calls
                new PluginPageInfo
                {
                    Name = "config-api.js",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config-api.js",
                },
                // Initialization (must load last)
                new PluginPageInfo
                {
                    Name = "config-init.js",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config-init.js",
                }
            ];
        }
    }
}

