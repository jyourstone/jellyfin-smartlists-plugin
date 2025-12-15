using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.Shared
{
    /// <summary>
    /// Middleware that injects the sidebar script into HTML responses.
    /// </summary>
    public class SidebarInjectionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<SidebarInjectionMiddleware> _logger;

        public SidebarInjectionMiddleware(RequestDelegate next, ILogger<SidebarInjectionMiddleware> logger)
        {
            _next = next;
            _logger = logger;
            logger.LogInformation("SidebarInjectionMiddleware initialized");
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Skip non-HTML requests early (check path, not content type which isn't set yet)
            var path = context.Request.Path.Value ?? string.Empty;
            
            // Skip API endpoints, static files, and plugin configuration pages
            if (path.StartsWith("/api/", System.StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/web/configurationpage", System.StringComparison.OrdinalIgnoreCase) ||
                path.Contains(".js", System.StringComparison.OrdinalIgnoreCase) ||
                path.Contains(".css", System.StringComparison.OrdinalIgnoreCase) ||
                path.Contains(".json", System.StringComparison.OrdinalIgnoreCase) ||
                path.Contains(".ico", System.StringComparison.OrdinalIgnoreCase) ||
                path.Contains(".png", System.StringComparison.OrdinalIgnoreCase) ||
                path.Contains(".jpg", System.StringComparison.OrdinalIgnoreCase) ||
                path.Contains(".svg", System.StringComparison.OrdinalIgnoreCase))
            {
                await _next(context);
                return;
            }

            _logger.LogDebug("SidebarInjectionMiddleware processing request: {Path}", path);

            // Disable compression for this response by removing Accept-Encoding header
            // This ensures the response we receive is uncompressed
            var originalAcceptEncoding = context.Request.Headers.AcceptEncoding.ToString();
            context.Request.Headers.Remove("Accept-Encoding");

            // Intercept the response body to modify HTML responses
            var originalBodyStream = context.Response.Body;
            
            using (var responseBody = new MemoryStream())
            {
                // Replace the response body stream so we can intercept it
                context.Response.Body = responseBody;

                // Continue down the pipeline
                // Since we removed Accept-Encoding, compression middleware won't compress
                await _next(context);
                
                // Restore Accept-Encoding header
                if (!string.IsNullOrEmpty(originalAcceptEncoding))
                {
                    context.Request.Headers.AcceptEncoding = originalAcceptEncoding;
                }

                // Check if this is actually an HTML response
                var contentType = context.Response.ContentType ?? string.Empty;
                if (!contentType.Contains("text/html", System.StringComparison.OrdinalIgnoreCase) &&
                    !contentType.Contains("application/xhtml", System.StringComparison.OrdinalIgnoreCase))
                {
                    // Not HTML, copy response as-is
                    responseBody.Seek(0, SeekOrigin.Begin);
                    context.Response.Body = originalBodyStream;
                    await responseBody.CopyToAsync(originalBodyStream, context.RequestAborted);
                    return;
                }

                // Read the uncompressed response
                responseBody.Seek(0, SeekOrigin.Begin);
                var responseText = await new StreamReader(responseBody, Encoding.UTF8).ReadToEndAsync();

                // Inject the script if this is the main HTML page
                if (ShouldInjectScript(context, responseText))
                {
                    responseText = InjectScript(responseText);
                    _logger.LogInformation("Injected sidebar script into HTML response for path: {Path}", path);
                }

                // Write the modified response (uncompressed)
                var responseBytes = Encoding.UTF8.GetBytes(responseText);
                context.Response.Body = originalBodyStream;
                context.Response.ContentLength = responseBytes.Length;
                // Ensure compression headers are removed since we're sending uncompressed
                context.Response.Headers.Remove("Content-Encoding");
                // Also remove any transfer encoding
                context.Response.Headers.Remove("Transfer-Encoding");
                await context.Response.Body.WriteAsync(responseBytes, context.RequestAborted);
            }
        }

        private static bool ShouldInjectScript(HttpContext context, string responseText)
        {
            // Only inject into pages that have a <head> or <body> tag
            // and don't already have our script
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return false;
            }

            var hasHead = responseText.Contains("<head", System.StringComparison.OrdinalIgnoreCase);
            var hasBody = responseText.Contains("<body", System.StringComparison.OrdinalIgnoreCase);
            var alreadyInjected = responseText.Contains("smartlists-sidebar-script", System.StringComparison.OrdinalIgnoreCase) ||
                                  responseText.Contains("sidebar.js", System.StringComparison.OrdinalIgnoreCase);

            return (hasHead || hasBody) && !alreadyInjected;
        }

        private static string InjectScript(string html)
        {
            // Try to inject before </body> first (preferred location)
            if (html.Contains("</body>", System.StringComparison.OrdinalIgnoreCase))
            {
                var scriptTag = "<script src=\"/web/configurationpage?name=sidebar.js\"></script>";
                html = html.Replace("</body>", scriptTag + "\n</body>", System.StringComparison.OrdinalIgnoreCase);
                return html;
            }

            // Fallback: inject before </head>
            if (html.Contains("</head>", System.StringComparison.OrdinalIgnoreCase))
            {
                var scriptTag = "<script src=\"/web/configurationpage?name=sidebar.js\"></script>";
                html = html.Replace("</head>", scriptTag + "\n</head>", System.StringComparison.OrdinalIgnoreCase);
                return html;
            }

            return html;
        }
    }
}

