using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.Shared
{
    /// <summary>
    /// Startup filter that registers the sidebar injection middleware in the HTTP pipeline.
    /// </summary>
    public class SidebarInjectionStartupFilter : IStartupFilter
    {
        private readonly ILogger<SidebarInjectionStartupFilter> _logger;

        public SidebarInjectionStartupFilter(ILogger<SidebarInjectionStartupFilter> logger)
        {
            _logger = logger;
            _logger.LogDebug("SidebarInjectionStartupFilter initialized - middleware will be registered");
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            _logger.LogDebug("Configuring sidebar injection middleware in HTTP pipeline");
            
            return app =>
            {
                // Register middleware early (before compression) to get uncompressed responses
                app.UseMiddleware<SidebarInjectionMiddleware>();
                _logger.LogDebug("Sidebar injection middleware registered early in pipeline (before compression)");
                next(app);
            };
        }
    }
}

