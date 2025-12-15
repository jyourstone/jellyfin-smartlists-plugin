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
            _logger.LogInformation("SidebarInjectionStartupFilter initialized - middleware will be registered");
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            _logger.LogDebug("Configuring sidebar injection middleware in HTTP pipeline");
            
            return app =>
            {
                // Register our middleware VERY early in the pipeline (before compression)
                // This ensures we get uncompressed responses that we can modify
                // UseMiddleware will resolve SidebarInjectionMiddleware from DI
                app.UseMiddleware<SidebarInjectionMiddleware>();
                _logger.LogDebug("Sidebar injection middleware registered early in pipeline (before compression)");
                
                // Continue with the rest of the pipeline (including compression middleware)
                next(app);
            };
        }
    }
}

