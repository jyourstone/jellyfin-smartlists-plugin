using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;

namespace Jellyfin.Plugin.SmartLists.Utilities
{
    /// <summary>
    /// Provides input validation methods to prevent security vulnerabilities
    /// including injection attacks, XSS, path traversal, and ReDoS attacks.
    /// </summary>
    public static class InputValidator
    {
        // Constants for validation limits
        private const int MaxNameLength = 500;
        private const int MaxStringValueLength = 2000;
        private const int MaxRegexPatternLength = 1000;
        private const int MaxFieldNameLength = 100;
        private const int MaxOperatorLength = 50;
        private const int MaxExpressionSetsCount = 100;
        private const int MaxExpressionsPerSet = 100;
        private const int MaxMediaTypesCount = 50;
        private const int MaxSchedulesCount = 100;

        // Dangerous patterns
        private static readonly string[] SqlInjectionPatterns = new[]
        {
            @"(\b(SELECT|INSERT|UPDATE|DELETE|DROP|CREATE|ALTER|EXEC|EXECUTE|UNION|DECLARE|CAST|CONVERT)\b)",
            @"(--|;|\/\*|\*\/|xp_|sp_)",
            @"('(\s*OR\s+|\s*AND\s+)?'?\s*=\s*')",
        };

        private static readonly string[] XssPatterns = new[]
        {
            @"<script[^>]*>.*?</script>",
            @"javascript:",
            @"on\w+\s*=",
            @"<iframe",
            @"<object",
            @"<embed",
        };

        private static readonly string[] PathTraversalPatterns = new[]
        {
            @"\.\./",
            @"\.\.\\",
            @"%2e%2e/",
            @"%2e%2e\\",
        };

        // Dangerous characters for file names
        private static readonly char[] DangerousFileNameChars = new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*', '\0' };

        /// <summary>
        /// Validates a smart list name.
        /// </summary>
        public static SmartListValidationResult ValidateName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return SmartListValidationResult.Failure("List name cannot be empty");
            }

            if (name.Length > MaxNameLength)
            {
                return SmartListValidationResult.Failure($"List name cannot exceed {MaxNameLength} characters");
            }

            // Check for path traversal attempts
            if (ContainsPathTraversal(name))
            {
                return SmartListValidationResult.Failure("List name contains invalid characters");
            }

            // Check for control characters (except normal whitespace)
            if (name.Any(c => char.IsControl(c) && c != '\t' && c != '\n' && c != '\r'))
            {
                return SmartListValidationResult.Failure("List name contains invalid control characters");
            }

            // Check for potentially dangerous characters that could cause issues with file systems
            if (name.Any(c => DangerousFileNameChars.Contains(c)))
            {
                return SmartListValidationResult.Failure("List name contains invalid characters: < > : \" / \\ | ? *");
            }

            return SmartListValidationResult.Success();
        }

        /// <summary>
        /// Validates a string value (used in expressions).
        /// </summary>
        public static SmartListValidationResult ValidateStringValue(string? value, string fieldName = "Value")
        {
            if (value == null)
            {
                return SmartListValidationResult.Success(); // Null values are allowed
            }

            if (value.Length > MaxStringValueLength)
            {
                return SmartListValidationResult.Failure($"{fieldName} cannot exceed {MaxStringValueLength} characters");
            }

            // Check for SQL injection patterns
            if (ContainsSqlInjection(value))
            {
                return SmartListValidationResult.Failure($"{fieldName} contains potentially dangerous SQL patterns");
            }

            // Check for XSS patterns
            if (ContainsXss(value))
            {
                return SmartListValidationResult.Failure($"{fieldName} contains potentially dangerous script patterns");
            }

            return SmartListValidationResult.Success();
        }

        /// <summary>
        /// Validates a regex pattern to prevent ReDoS attacks.
        /// </summary>
        public static SmartListValidationResult ValidateRegexPattern(string? pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return SmartListValidationResult.Failure("Regex pattern cannot be empty");
            }

            if (pattern.Length > MaxRegexPatternLength)
            {
                return SmartListValidationResult.Failure($"Regex pattern cannot exceed {MaxRegexPatternLength} characters");
            }

            // Try to compile the regex with a timeout to detect ReDoS vulnerabilities
            try
            {
                var regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
                // Test with a simple string to ensure it compiles correctly
                _ = regex.IsMatch("test");
            }
            catch (ArgumentException ex)
            {
                return SmartListValidationResult.Failure($"Invalid regex pattern: {ex.Message}");
            }
            catch (RegexMatchTimeoutException)
            {
                return SmartListValidationResult.Failure("Regex pattern is too complex and may cause performance issues");
            }

            return SmartListValidationResult.Success();
        }

        /// <summary>
        /// Validates a field name.
        /// </summary>
        public static SmartListValidationResult ValidateFieldName(string? fieldName)
        {
            if (string.IsNullOrWhiteSpace(fieldName))
            {
                return SmartListValidationResult.Failure("Field name cannot be empty");
            }

            if (fieldName.Length > MaxFieldNameLength)
            {
                return SmartListValidationResult.Failure($"Field name cannot exceed {MaxFieldNameLength} characters");
            }

            // Field names should only contain alphanumeric characters and underscores
            if (!Regex.IsMatch(fieldName, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
            {
                return SmartListValidationResult.Failure("Field name must contain only letters, numbers, and underscores, and start with a letter or underscore");
            }

            return SmartListValidationResult.Success();
        }

        /// <summary>
        /// Validates an operator string.
        /// </summary>
        public static SmartListValidationResult ValidateOperator(string? operatorValue)
        {
            if (string.IsNullOrWhiteSpace(operatorValue))
            {
                return SmartListValidationResult.Failure("Operator cannot be empty");
            }

            if (operatorValue.Length > MaxOperatorLength)
            {
                return SmartListValidationResult.Failure($"Operator cannot exceed {MaxOperatorLength} characters");
            }

            // Operators should be simple strings without special characters
            if (!Regex.IsMatch(operatorValue, @"^[a-zA-Z0-9_\-\s]+$"))
            {
                return SmartListValidationResult.Failure("Operator contains invalid characters");
            }

            return SmartListValidationResult.Success();
        }

        /// <summary>
        /// Validates an integer value within a specified range.
        /// </summary>
        public static SmartListValidationResult ValidateInteger(int? value, int? min = null, int? max = null, string fieldName = "Value")
        {
            if (value == null)
            {
                return SmartListValidationResult.Success(); // Null values are allowed
            }

            if (min.HasValue && value < min.Value)
            {
                return SmartListValidationResult.Failure($"{fieldName} must be at least {min.Value}");
            }

            if (max.HasValue && value > max.Value)
            {
                return SmartListValidationResult.Failure($"{fieldName} cannot exceed {max.Value}");
            }

            return SmartListValidationResult.Success();
        }

        /// <summary>
        /// Validates a complete smart list DTO.
        /// </summary>
        public static SmartListValidationResult ValidateSmartList(SmartListDto? list)
        {
            if (list == null)
            {
                return SmartListValidationResult.Failure("List data is required");
            }

            // Validate name
            var nameResult = ValidateName(list.Name);
            if (!nameResult.IsValid)
            {
                return nameResult;
            }

            // Validate media types count
            if (list.MediaTypes != null && list.MediaTypes.Count > MaxMediaTypesCount)
            {
                return SmartListValidationResult.Failure($"Cannot select more than {MaxMediaTypesCount} media types");
            }

            // Validate media types values
            if (list.MediaTypes != null && list.MediaTypes.Count > 0)
            {
                var validMediaTypes = Core.Constants.MediaTypes.All;
                var invalidMediaTypes = list.MediaTypes
                    .Where(mt => !validMediaTypes.Contains(mt))
                    .ToList();

                if (invalidMediaTypes.Count > 0)
                {
                    return SmartListValidationResult.Failure($"Invalid media type(s): {string.Join(", ", invalidMediaTypes)}. Allowed types: {string.Join(", ", validMediaTypes)}");
                }
            }

            // Validate expression sets
            if (list.ExpressionSets != null)
            {
                if (list.ExpressionSets.Count > MaxExpressionSetsCount)
                {
                    return SmartListValidationResult.Failure($"Cannot have more than {MaxExpressionSetsCount} rule groups");
                }

                var expressionResult = ValidateExpressionSets(list.ExpressionSets);
                if (!expressionResult.IsValid)
                {
                    return expressionResult;
                }
            }

            // Validate numeric fields
            var maxItemsResult = ValidateInteger(list.MaxItems, min: 0, max: 100000, fieldName: "MaxItems");
            if (!maxItemsResult.IsValid)
            {
                return maxItemsResult;
            }

            var maxPlayTimeResult = ValidateInteger(list.MaxPlayTimeMinutes, min: 0, max: 525600, fieldName: "MaxPlayTimeMinutes"); // Max 1 year in minutes
            if (!maxPlayTimeResult.IsValid)
            {
                return maxPlayTimeResult;
            }

            // Validate schedules count
            if (list.Schedules != null && list.Schedules.Count > MaxSchedulesCount)
            {
                return SmartListValidationResult.Failure($"Cannot have more than {MaxSchedulesCount} schedules");
            }

            if (list.VisibilitySchedules != null && list.VisibilitySchedules.Count > MaxSchedulesCount)
            {
                return SmartListValidationResult.Failure($"Cannot have more than {MaxSchedulesCount} visibility schedules");
            }

            return SmartListValidationResult.Success();
        }

        /// <summary>
        /// Validates expression sets (rule groups).
        /// </summary>
        private static SmartListValidationResult ValidateExpressionSets(List<ExpressionSet> expressionSets)
        {
            foreach (var expressionSet in expressionSets)
            {
                if (expressionSet.Expressions == null)
                {
                    continue;
                }

                if (expressionSet.Expressions.Count > MaxExpressionsPerSet)
                {
                    return SmartListValidationResult.Failure($"Cannot have more than {MaxExpressionsPerSet} rules in a single group");
                }

                foreach (var expression in expressionSet.Expressions)
                {
                    var result = ValidateExpression(expression);
                    if (!result.IsValid)
                    {
                        return result;
                    }
                }
            }

            return SmartListValidationResult.Success();
        }

        /// <summary>
        /// Validates a single expression (rule).
        /// </summary>
        private static SmartListValidationResult ValidateExpression(Expression expression)
        {
            // Validate field name
            var fieldResult = ValidateFieldName(expression.MemberName);
            if (!fieldResult.IsValid)
            {
                return fieldResult;
            }

            // Validate operator
            var operatorResult = ValidateOperator(expression.Operator);
            if (!operatorResult.IsValid)
            {
                return operatorResult;
            }

            // Validate target value based on operator
            if (expression.Operator?.Contains("Regex", StringComparison.OrdinalIgnoreCase) == true)
            {
                return ValidateRegexPattern(expression.TargetValue);
            }
            else
            {
                return ValidateStringValue(expression.TargetValue, fieldName: "Rule value");
            }
        }

        /// <summary>
        /// Checks if a string contains SQL injection patterns.
        /// </summary>
        private static bool ContainsSqlInjection(string value)
        {
            foreach (var pattern in SqlInjectionPatterns)
            {
                try
                {
                    if (Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                    {
                        return true;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // If the regex times out, treat it as potentially dangerous
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if a string contains XSS patterns.
        /// </summary>
        private static bool ContainsXss(string value)
        {
            foreach (var pattern in XssPatterns)
            {
                try
                {
                    if (Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                    {
                        return true;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // If the regex times out, treat it as potentially dangerous
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if a string contains path traversal patterns.
        /// </summary>
        private static bool ContainsPathTraversal(string value)
        {
            foreach (var pattern in PathTraversalPatterns)
            {
                try
                {
                    if (Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                    {
                        return true;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // If the regex times out, treat it as potentially dangerous
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Represents the result of a validation operation.
    /// </summary>
    public class SmartListValidationResult
    {
        public bool IsValid { get; }
        public string? ErrorMessage { get; }

        private SmartListValidationResult(bool isValid, string? errorMessage = null)
        {
            IsValid = isValid;
            ErrorMessage = errorMessage;
        }

        public static SmartListValidationResult Success() => new SmartListValidationResult(true);

        public static SmartListValidationResult Failure(string errorMessage) => new SmartListValidationResult(false, errorMessage);
    }
}
