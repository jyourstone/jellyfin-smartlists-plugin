using System;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.SmartLists.Core.Constants
{
    /// <summary>
    /// Parses and compares Jellyfin display-aspect-ratio values such as <c>16:9</c>,
    /// <c>2.35:1</c>, and <c>80:29</c>.
    /// </summary>
    public static class AspectRatioTypes
    {
        /// <summary>
        /// Determines whether a value is a positive width-to-height ratio.
        /// </summary>
        /// <param name="value">Ratio in <c>width:height</c> form.</param>
        /// <returns><c>true</c> when both components are valid and positive.</returns>
        public static bool IsValid(string? value)
        {
            return TryParse(value, out _, out _);
        }

        /// <summary>
        /// Determines whether a semicolon-separated list contains only valid ratios.
        /// </summary>
        /// <param name="values">Semicolon-separated ratios.</param>
        /// <returns><c>true</c> when the list is non-empty and every entry is valid.</returns>
        public static bool IsValidList(string? values)
        {
            if (string.IsNullOrWhiteSpace(values))
            {
                return false;
            }

            var entries = values.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return entries.Length > 0 && entries.All(IsValid);
        }

        /// <summary>
        /// Evaluates a ratio comparison. Invalid or missing item values never match, including
        /// negative operators, so non-video items do not leak into aspect-ratio rules.
        /// </summary>
        /// <param name="fieldValue">Jellyfin's stored aspect ratio.</param>
        /// <param name="targetValue">One ratio, or a semicolon-separated list for IsIn/IsNotIn.</param>
        /// <param name="operatorName">The SmartLists operator name.</param>
        /// <returns>Whether the comparison matches.</returns>
        public static bool Evaluate(string? fieldValue, string? targetValue, string operatorName)
        {
            if (!TryParse(fieldValue, out var fieldWidth, out var fieldHeight))
            {
                return false;
            }

            if (operatorName is "IsIn" or "IsNotIn")
            {
                if (string.IsNullOrWhiteSpace(targetValue))
                {
                    return false;
                }

                var entries = targetValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (entries.Length == 0)
                {
                    return false;
                }

                var isIn = false;
                foreach (var entry in entries)
                {
                    if (!TryParse(entry, out var targetWidth, out var targetHeight))
                    {
                        return false;
                    }

                    if (Compare(fieldWidth, fieldHeight, targetWidth, targetHeight) == 0)
                    {
                        isIn = true;
                        break;
                    }
                }

                return operatorName == "IsIn" ? isIn : !isIn;
            }

            if (!TryParse(targetValue, out var singleTargetWidth, out var singleTargetHeight))
            {
                return false;
            }

            var comparison = Compare(fieldWidth, fieldHeight, singleTargetWidth, singleTargetHeight);
            return operatorName switch
            {
                "Equal" => comparison == 0,
                "NotEqual" => comparison != 0,
                "GreaterThan" => comparison > 0,
                "LessThan" => comparison < 0,
                "GreaterThanOrEqual" => comparison >= 0,
                "LessThanOrEqual" => comparison <= 0,
                _ => false,
            };
        }

        private static bool TryParse(string? value, out decimal width, out decimal height)
        {
            width = 0;
            height = 0;

            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var parts = value.Split(':');
            return parts.Length == 2
                && decimal.TryParse(parts[0].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out width)
                && decimal.TryParse(parts[1].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out height)
                && width > 0
                && height > 0;
        }

        private static int Compare(decimal leftWidth, decimal leftHeight, decimal rightWidth, decimal rightHeight)
        {
            try
            {
                return decimal.Compare(leftWidth * rightHeight, rightWidth * leftHeight);
            }
            catch (OverflowException)
            {
                return decimal.Compare(leftWidth / leftHeight, rightWidth / rightHeight);
            }
        }
    }
}
