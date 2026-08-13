using System;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SmartLists.Api.Filters
{
    /// <summary>
    /// Normalizes this plugin's error responses to RFC 7807 <see cref="ProblemDetails"/>.
    /// <para>
    /// Controllers in this plugin historically returned three different error bodies:
    /// <c>new { message = "..." }</c>, <c>new { error = "..." }</c> and bare strings
    /// (<c>BadRequest("Invalid list ID format")</c>). This filter rewrites all three into a single
    /// documented shape - <c>{ "title": ..., "detail": ..., "status": ... }</c> - so callers only
    /// ever have to read <c>detail</c>. The HTTP status code is never changed and the
    /// human-readable text is carried across verbatim into <c>detail</c>.
    /// </para>
    /// <para>
    /// <b>Scoping proof - this filter cannot touch any controller but ours.</b>
    /// It is an attribute applied to the three SmartLists controller classes and is deliberately
    /// NOT registered in <c>MvcOptions.Filters</c> (nothing in <c>ServiceRegistrator</c> references
    /// it). MVC discovers filters per action: <c>DefaultApplicationModelProvider</c> collects
    /// <see cref="IFilterMetadata"/> attributes declared on a controller type and its methods into
    /// that controller's own <c>ControllerModel.Filters</c>, and the filter pipeline for an action
    /// is built exclusively from its own controller/action models plus the global collection. Since
    /// this type appears in neither the global collection nor any non-SmartLists controller model,
    /// core Jellyfin actions are structurally unreachable. As belt-and-braces against a future
    /// global registration, <see cref="OnResultExecuting"/> also bails out when the executing
    /// controller does not come from this plugin's assembly.
    /// </para>
    /// <para>
    /// Three further gates keep the blast radius minimal even within our own controllers:
    /// only <see cref="ObjectResult"/>s are considered, only status codes &gt;= 400 (so the
    /// <c>Ok(new { message = ... })</c> success bodies the UI shows as notifications are untouched),
    /// and only values this filter recognises as an error body are rewritten - anything else is
    /// passed through unchanged.
    /// </para>
    /// <para>
    /// Ceiling: this runs inside MVC's result pipeline, so it cannot normalize responses produced
    /// before or outside it - <c>[Authorize]</c> challenges, model-binding validation failures
    /// (already framework <c>ValidationProblemDetails</c>) and Jellyfin's exception middleware.
    /// The contract is "every error body this plugin authors", not "every byte these routes emit".
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public sealed class SmartListsProblemDetailsAttribute : Attribute, IResultFilter
    {
        /// <summary>
        /// Rewrites a recognised error body into <see cref="ProblemDetails"/> before it is serialized.
        /// </summary>
        /// <param name="context">The result-executing context supplied by MVC.</param>
        public void OnResultExecuting(ResultExecutingContext context)
        {
            if (context is null || context.Controller?.GetType().Assembly != typeof(Plugin).Assembly)
            {
                return;
            }

            if (context.Result is not ObjectResult result
                || result.StatusCode is not int status
                || status < 400)
            {
                return;
            }

            // Already RFC 7807 (this also covers ValidationProblemDetails) - leave it alone.
            if (result.Value is ProblemDetails)
            {
                return;
            }

            var detail = ExtractDetail(result.Value);
            if (detail is null)
            {
                return;
            }

            // The factory fills in Title/Type from the status code and adds traceId, matching what
            // ASP.NET Core produces for its own client errors. It is registered by AddMvc; if it
            // somehow is not, leave the response exactly as the controller wrote it.
            var factory = context.HttpContext.RequestServices.GetService<ProblemDetailsFactory>();
            if (factory is null)
            {
                return;
            }

            result.Value = factory.CreateProblemDetails(context.HttpContext, statusCode: status, detail: detail);
            result.DeclaredType = typeof(ProblemDetails);
        }

        /// <summary>
        /// No-op; this filter only rewrites the result before it is executed.
        /// </summary>
        /// <param name="context">The result-executed context supplied by MVC.</param>
        public void OnResultExecuted(ResultExecutedContext context)
        {
        }

        /// <summary>
        /// Pulls the human-readable text out of the ad-hoc error bodies this plugin returns.
        /// Anonymous types (<c>new { message = "..." }</c>) have to be read reflectively - there is
        /// no shared type to cast to, and introducing one would mean editing ~166 return statements.
        /// </summary>
        /// <param name="value">The value the controller put in the <see cref="ObjectResult"/>.</param>
        /// <returns>The message text, or null when the value is not a recognised error body.</returns>
        private static string? ExtractDetail(object? value) => value switch
        {
            null => null,
            string text => text,
            // ponytail: five sites return `new { message, error = ex.Message }`. Only `message` is
            // carried over; the `error` half is a raw exception string that no caller reads and that
            // does not belong on the wire. Nothing else drops information.
            _ => ReadStringProperty(value, "message") ?? ReadStringProperty(value, "error")
        };

        private static string? ReadStringProperty(object value, string name)
            => value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value) as string;
    }
}
