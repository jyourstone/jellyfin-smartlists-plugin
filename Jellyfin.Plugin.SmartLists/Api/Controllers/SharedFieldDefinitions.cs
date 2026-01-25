using Jellyfin.Plugin.SmartLists.Core.QueryEngine;

namespace Jellyfin.Plugin.SmartLists.Api.Controllers
{
    /// <summary>
    /// Shared field definitions for both admin and user controllers.
    /// Delegates to FieldRegistry for the single source of truth.
    /// </summary>
    public static class SharedFieldDefinitions
    {
        /// <summary>
        /// Gets the available fields structure for smart playlist rules.
        /// </summary>
        /// <returns>Object containing all field categories, operators, and order options.</returns>
        public static object GetAvailableFields()
        {
            return FieldRegistry.GetAvailableFieldsForApi();
        }
    }
}
