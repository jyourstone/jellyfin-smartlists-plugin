using System;
using System.Linq;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SmartLists.Api.Filters
{
    /// <summary>
    /// Limits every action of the controller it is applied to to users who may use the SmartLists user page
    /// (the rule in <see cref="UserPageAccess"/>), so a new endpoint can't forget the check. Actions marked
    /// <c>[AllowAnonymous]</c> are skipped.
    /// <para>
    /// An action filter rather than an authorization filter on purpose: a result set here still runs through
    /// <see cref="SmartListsProblemDetailsAttribute"/>, so the 401/403 bodies keep their ProblemDetails shape.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public sealed class RequireUserPageAccessAttribute : Attribute, IActionFilter
    {
        /// <summary>
        /// Short-circuits the action with 401 or 403 when the current user may not use the user page.
        /// </summary>
        /// <param name="context">The action-executing context supplied by MVC.</param>
        public void OnActionExecuting(ActionExecutingContext context)
        {
            if (context is null || context.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
            {
                return;
            }

            var userId = context.HttpContext.User.GetUserId();
            if (userId == Guid.Empty)
            {
                context.Result = new UnauthorizedObjectResult(new
                {
                    message = "User not authenticated. Please log in to access SmartLists."
                });
                return;
            }

            var userManager = context.HttpContext.RequestServices.GetRequiredService<IUserManager>();
            var message = UserPageAccess.Evaluate(userManager, userId, Plugin.Instance?.Configuration) switch
            {
                UserPageAccessResult.PageDisabled => "The user page is currently disabled by an administrator. Please contact your administrator for access to SmartLists.",
                UserPageAccessResult.UserNotAllowed => "You do not have permission to access the SmartLists user page. Please contact your administrator for access.",
                _ => null,
            };

            if (message != null)
            {
                context.Result = new ObjectResult(new { message }) { StatusCode = StatusCodes.Status403Forbidden };
            }
        }

        /// <summary>
        /// Does nothing; the check happens before the action runs.
        /// </summary>
        /// <param name="context">The action-executed context supplied by MVC.</param>
        public void OnActionExecuted(ActionExecutedContext context)
        {
        }
    }
}
