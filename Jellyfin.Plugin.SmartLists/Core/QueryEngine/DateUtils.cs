using System;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine
{
    public static class DateUtils
    {
        /// <summary>
        /// Extracts the PremiereDate property from a BaseItem.
        /// </summary>
        /// <param name="item">The BaseItem to extract the release date from. Must not be null.</param>
        /// <param name="premiereDate">The extracted premiere date when available.</param>
        /// <returns>True when the item has a usable premiere date; otherwise false.</returns>
        /// <exception cref="ArgumentNullException">Thrown when item is null.</exception>
        public static bool TryGetPremiereDate(BaseItem item, out DateTime premiereDate)
        {
            ArgumentNullException.ThrowIfNull(item);

            try
            {
                var premiereDateProperty = item.GetType().GetProperty("PremiereDate");
                if (premiereDateProperty != null)
                {
                    var value = premiereDateProperty.GetValue(item);
                    if (value is DateTime dateTime && dateTime != DateTime.MinValue)
                    {
                        premiereDate = dateTime;
                        return true;
                    }
                }
            }
            catch
            {
                // Ignore errors and fall back to unavailable.
            }

            premiereDate = DateTime.MinValue;
            return false;
        }

        /// <summary>
        /// Extracts the PremiereDate property from a BaseItem and returns its Unix timestamp, or 0 on error.
        /// Treats the PremiereDate as UTC to ensure consistency with user-input date handling.
        /// </summary>
        /// <param name="item">The BaseItem to extract the release date from. Must not be null.</param>
        /// <returns>Unix timestamp of the release date, or 0 if the date is not available or invalid.</returns>
        /// <exception cref="ArgumentNullException">Thrown when item is null.</exception>
        public static double GetReleaseDateUnixTimestamp(BaseItem item)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (TryGetPremiereDate(item, out var premiereDateTime))
            {
                // Treat the PremiereDate as UTC to ensure consistency with user-input date handling.
                // This assumes Jellyfin stores dates in UTC, which is the typical behavior.
                try
                {
                    return new DateTimeOffset(premiereDateTime, TimeSpan.Zero).ToUnixTimeSeconds();
                }
                catch (ArgumentException)
                {
                    return new DateTimeOffset(premiereDateTime.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeSeconds();
                }
            }

            return 0;
        }
    }
}
