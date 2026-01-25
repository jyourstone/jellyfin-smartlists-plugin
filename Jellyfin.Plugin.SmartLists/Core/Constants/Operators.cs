using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;

namespace Jellyfin.Plugin.SmartLists.Core.Constants
{
    /// <summary>
    /// Represents an operator with its value and display label.
    /// </summary>
    public record OperatorInfo(string Value, string Label);

    /// <summary>
    /// Centralized operator definitions for different field types.
    /// Delegates to FieldRegistry for field-to-operator mappings.
    /// </summary>
    public static class Operators
    {
        /// <summary>
        /// All available operators for display in the UI.
        /// </summary>
        public static readonly OperatorInfo[] AllOperators =
        [
            new OperatorInfo("Equal", "equals"),
            new OperatorInfo("NotEqual", "not equals"),
            new OperatorInfo("Contains", "contains"),
            new OperatorInfo("NotContains", "not contains"),
            new OperatorInfo("IsIn", "is in"),
            new OperatorInfo("IsNotIn", "is not in"),
            new OperatorInfo("GreaterThan", "greater than"),
            new OperatorInfo("LessThan", "less than"),
            new OperatorInfo("GreaterThanOrEqual", "greater than or equal"),
            new OperatorInfo("LessThanOrEqual", "less than or equal"),
            new OperatorInfo("MatchRegex", "matches regex (.NET syntax)"),
            new OperatorInfo("After", "after"),
            new OperatorInfo("Before", "before"),
            new OperatorInfo("NewerThan", "newer than"),
            new OperatorInfo("OlderThan", "older than"),
            new OperatorInfo("Weekday", "weekday")
        ];

        /// <summary>
        /// Gets the appropriate operators for a given field type.
        /// </summary>
        public static string[] GetOperatorsForField(string fieldName)
        {
            var operators = FieldRegistry.GetOperatorsForField(fieldName);
            return operators.Length > 0 ? operators : [.. AllOperators.Select(op => op.Value)];
        }

        /// <summary>
        /// Gets the complete field operators dictionary for all supported fields.
        /// </summary>
        public static Dictionary<string, string[]> GetFieldOperatorsDictionary()
        {
            return FieldRegistry.GetFieldOperatorsDictionary();
        }

        /// <summary>
        /// Gets a formatted string of all supported operators for a field, useful for error messages.
        /// </summary>
        public static string GetSupportedOperatorsString(string fieldName)
        {
            var operators = GetOperatorsForField(fieldName);
            return string.Join(", ", operators);
        }
    }
}
