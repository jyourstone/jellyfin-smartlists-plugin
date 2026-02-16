using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using System.Linq;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SmartLists.Core.Constants;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Utilities;
using ModelExpression = Jellyfin.Plugin.SmartLists.Core.QueryEngine.Expression;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine
{
    // This is based on https://stackoverflow.com/questions/6488034/how-to-implement-a-rule-engine
    public static class Engine
    {
        // Cache for compiled regex patterns to avoid recompilation
        private static readonly ConcurrentDictionary<string, Regex> _regexCache = new();

        /// <summary>
        /// Normalizes a UserId string to "N" format (no dashes) for consistent dictionary lookups.
        /// Handles various GUID formats and converts them to the standard format used by UserPlaylists.
        /// </summary>
        /// <param name="userId">The user ID string in any valid GUID format</param>
        /// <returns>Normalized user ID in "N" format (no dashes), or original string if not a valid GUID</returns>
        /// <remarks>
        /// Note: Adding logging here would require passing ILogger through many call chains.
        /// The current approach is acceptable since invalid GUIDs will fail with clear errors downstream.
        /// </remarks>
        private static string NormalizeUserId(string userId)
        {
            if (string.IsNullOrEmpty(userId))
                return userId;

            // Try to parse as GUID and convert to "N" format (no dashes)
            if (Guid.TryParse(userId, out var guid))
            {
                return guid.ToString("N");
            }

            // If not a valid GUID, return as-is (shouldn't happen in normal operation)
            // Invalid GUIDs will fail downstream with better context than logging here
            return userId;
        }

        /// <summary>
        /// Gets or creates a compiled regex pattern from the cache.
        /// </summary>
        /// <param name="pattern">The regex pattern</param>
        /// <param name="logger">Optional logger for error reporting</param>
        /// <returns>The compiled regex</returns>
        /// <exception cref="ArgumentException">Thrown when the regex pattern is invalid</exception>
        private static Regex GetOrCreateRegex(string pattern, ILogger? logger = null)
        {
            return _regexCache.GetOrAdd(pattern, key =>
            {
                try
                {
                    logger?.LogDebug("SmartLists compiling new regex pattern: {Pattern}", key);
                    return new Regex(key, RegexOptions.Compiled | RegexOptions.None);
                }
                catch (ArgumentException ex)
                {
                    logger?.LogError(ex, "Invalid regex pattern '{Pattern}': {Message}", key, ex.Message);
                    throw new ArgumentException($"Invalid regex pattern '{key}': {ex.Message}");
                }
            });
        }

        private static System.Linq.Expressions.Expression BuildExpr<T>(ModelExpression r, ParameterExpression param, string defaultUserId, ILogger? logger = null)
        {
            // Check if this is a user-specific field that should always use method calls
            if (ModelExpression.IsUserSpecificField(r.MemberName))
            {
                // Use the specified user ID or default to playlist user
                var effectiveUserId = r.UserId ?? defaultUserId;
                if (string.IsNullOrEmpty(effectiveUserId))
                {
                    logger?.LogError("SmartLists user-specific field '{Field}' requires a valid user ID", r.MemberName);
                    throw new ArgumentException($"User-specific field '{r.MemberName}' requires a valid user ID, but no user ID was provided and no default user ID is available.");
                }

                // Normalize UserId to "N" format (no dashes) to match UserPlaylists format for consistent dictionary lookups
                var normalizedUserId = NormalizeUserId(effectiveUserId);

                // Create a new expression with all properties copied and effective user ID set
                var userSpecificExpression = new Expression(r.MemberName, r.Operator, r.TargetValue)
                {
                    UserId = normalizedUserId,
                    IncludeUnwatchedSeries = r.IncludeUnwatchedSeries,
                };

                return BuildUserSpecificExpression<T>(userSpecificExpression, param, logger);
            }

            // Special handling for Tags field with IncludeParentSeriesTags option
            if (r.MemberName == "Tags" && r.IncludeParentSeriesTags == true)
            {
                logger?.LogDebug("SmartLists building Tags expression with parent series tags inclusion");
                return BuildCombinedStringEnumerableExpression(r, param, "Tags", "ParentSeriesTags", logger);
            }

            // Special handling for Studios field with IncludeParentSeriesStudios option
            if (r.MemberName == "Studios" && r.IncludeParentSeriesStudios == true)
            {
                logger?.LogDebug("SmartLists building Studios expression with parent series studios inclusion");
                return BuildCombinedStringEnumerableExpression(r, param, "Studios", "ParentSeriesStudios", logger);
            }

            // Special handling for Genres field with IncludeParentSeriesGenres option
            if (r.MemberName == "Genres" && r.IncludeParentSeriesGenres == true)
            {
                logger?.LogDebug("SmartLists building Genres expression with parent series genres inclusion");
                return BuildCombinedStringEnumerableExpression(r, param, "Genres", "ParentSeriesGenres", logger);
            }

            // Special handling for AudioLanguages field with OnlyDefaultAudioLanguage option
            if (r.MemberName == "AudioLanguages" && r.OnlyDefaultAudioLanguage == true)
            {
                logger?.LogDebug("SmartLists building AudioLanguages expression with default language only");
                // Get the DefaultAudioLanguages property and build expression using normal flow
                var defaultAudioLanguagesProperty = System.Linq.Expressions.Expression.PropertyOrField(param, "DefaultAudioLanguages");
                var defaultAudioLanguagesType = defaultAudioLanguagesProperty.Type;
                // DefaultAudioLanguages is List<string>, same as AudioLanguages, so use the same enumerable expression builder
                var enumerableExpr = BuildEnumerableExpression(r, defaultAudioLanguagesProperty, defaultAudioLanguagesType, logger);
                if (enumerableExpr != null)
                {
                    return enumerableExpr;
                }
                // If BuildEnumerableExpression returns null, fall through to normal flow (shouldn't happen for List<string>)
            }

            // Get the property/field expression for non-user-specific fields
            var left = System.Linq.Expressions.Expression.PropertyOrField(param, r.MemberName);
            var tProp = left.Type;

            logger?.LogDebug("SmartLists BuildExpr: Field={Field}, Type={Type}, Operator={Operator}", r.MemberName, tProp.Name, r.Operator);

            // Handle different field types with specialized handlers
            // Check resolution fields first (before generic string check)
            if (tProp == typeof(string) && IsResolutionField(r.MemberName))
            {
                return BuildResolutionExpression(r, left, logger);
            }

            // Check framerate fields (nullable float type)
            if (tProp == typeof(float?) && IsFramerateField(r.MemberName))
            {
                return BuildFramerateExpression(r, left, logger);
            }

            if (tProp == typeof(string))
            {
                return BuildStringExpression(r, left, logger);
            }

            if (tProp == typeof(bool))
            {
                return BuildBooleanExpression(r, left, logger);
            }

            if (tProp == typeof(double) && IsDateField(r.MemberName))
            {
                return BuildDateExpression(r, left, logger);
            }

            if (tProp.GetInterface("IEnumerable`1") != null)
            {
                var enumerableExpr = BuildEnumerableExpression(r, left, tProp, logger);
                if (enumerableExpr != null)
                {
                    return enumerableExpr;
                }
            }

            // Handle standard .NET operators for other types
            return BuildStandardOperatorExpression(r, left, tProp, logger);
        }

        /// <summary>
        /// Builds combined expressions for a field that also checks its parent series equivalent.
        /// For positive operators (Contains, IsIn, MatchRegex): Uses OR logic - item passes if EITHER field OR parent series field match
        /// For negative operators (NotContains, IsNotIn): Uses AND logic - item passes only if BOTH field AND parent series field don't match
        /// </summary>
        /// <param name="r">The expression rule</param>
        /// <param name="param">The parameter expression</param>
        /// <param name="fieldName">The name of the field (e.g., "Tags", "Studios", "Genres")</param>
        /// <param name="parentSeriesFieldName">The name of the parent series field (e.g., "ParentSeriesTags", "ParentSeriesStudios", "ParentSeriesGenres")</param>
        /// <param name="logger">Logger instance</param>
        private static System.Linq.Expressions.Expression BuildCombinedStringEnumerableExpression(Expression r, ParameterExpression param, string fieldName, string parentSeriesFieldName, ILogger? logger)
        {
            var fieldProperty = System.Linq.Expressions.Expression.PropertyOrField(param, fieldName);
            var parentSeriesFieldProperty = System.Linq.Expressions.Expression.PropertyOrField(param, parentSeriesFieldName);

            // For Equal/NotEqual (exclusive match), we must check against the combined list.
            // Checking each list independently would give wrong results, e.g. episode Studios=["ABC"]
            // + parent series Studios=["Marvel Studios"] would incorrectly match "equals Marvel Studios"
            // because the parent list alone exclusively equals it, ignoring the episode's own studios.
            if (r.Operator == "Equal" || r.Operator == "NotEqual")
            {
                logger?.LogDebug("SmartLists building combined {Field} exclusive {Operator} against merged list", fieldName, r.Operator);
                var right = System.Linq.Expressions.Expression.Constant(r.TargetValue, typeof(string));
                var method = typeof(Engine).GetMethod("OnlyCombinedItemEquals", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (method == null) throw new InvalidOperationException("Engine.OnlyCombinedItemEquals method not found");
                var equalsCall = System.Linq.Expressions.Expression.Call(method, fieldProperty, parentSeriesFieldProperty, right);
                if (r.Operator == "Equal") return equalsCall;
                return System.Linq.Expressions.Expression.Not(equalsCall);
            }

            var fieldExpression = BuildStringEnumerableExpression(r, fieldProperty, logger);
            var parentSeriesFieldExpression = BuildStringEnumerableExpression(r, parentSeriesFieldProperty, logger);

            // Determine if this is a negative operator
            bool isNegativeOperator = r.Operator == "NotContains" || r.Operator == "IsNotIn";

            if (isNegativeOperator)
            {
                // For negative operators: Use AND logic
                // Item passes only if BOTH field don't match AND parent series field don't match
                logger?.LogDebug("SmartLists building combined {Field} expression with AND logic for negative operator {Operator}", fieldName, r.Operator);
                return System.Linq.Expressions.Expression.AndAlso(fieldExpression, parentSeriesFieldExpression);
            }
            else
            {
                // For positive operators: Use OR logic
                // Item passes if EITHER field match OR parent series field match
                logger?.LogDebug("SmartLists building combined {Field} expression with OR logic for positive operator {Operator}", fieldName, r.Operator);
                return System.Linq.Expressions.Expression.OrElse(fieldExpression, parentSeriesFieldExpression);
            }
        }

        /// <summary>
        /// Builds expressions for user-specific fields that require method calls.
        /// </summary>
        private static BinaryExpression BuildUserSpecificExpression<T>(Expression r, ParameterExpression param, ILogger? logger)
        {
            logger?.LogDebug("SmartLists BuildExpr: User-specific query for Field={Field}, UserId={UserId}, Operator={Operator}", r.MemberName, r.UserId, r.Operator);

            // Get the method to call (e.g., GetPlaybackStatusByUser)
            var methodName = r.UserSpecificField;
            if (string.IsNullOrEmpty(methodName))
            {
                logger?.LogError("SmartLists UserSpecificField is null or empty for field '{Field}'", r.MemberName);
                throw new ArgumentException($"UserSpecificField is null or empty for field '{r.MemberName}'");
            }
            var method = typeof(T).GetMethod(methodName, [typeof(string)]);

            if (method == null)
            {
                logger?.LogError("SmartLists user-specific method '{Method}' not found for field '{Field}'", methodName, r.MemberName);
                throw new ArgumentException($"User-specific method '{methodName}' not found for field '{r.MemberName}'");
            }

            // Create the method call: operand.GetIsFavoriteByUser(userId)
            // Ensure UserId is normalized for consistent dictionary lookups
            // Note: r.UserId should already be normalized and non-null from BuildExpr, but we validate defensively
            if (string.IsNullOrEmpty(r.UserId))
            {
                logger?.LogError("SmartLists BuildUserSpecificExpression: UserId is null or empty for field '{Field}'", r.MemberName);
                throw new ArgumentException($"UserId is required for user-specific field '{r.MemberName}'");
            }
            var normalizedUserId = NormalizeUserId(r.UserId);
            var methodCall = System.Linq.Expressions.Expression.Call(param, method, System.Linq.Expressions.Expression.Constant(normalizedUserId));

            // Get the return type of the method to handle different data types properly
            var returnType = method.ReturnType;
            logger?.LogDebug("SmartLists user-specific method '{Method}' returns type '{ReturnType}'", methodName, returnType.Name);

            // Handle different return types and operators appropriately
            if (returnType == typeof(bool))
            {
                return BuildUserSpecificBooleanExpression(r, methodCall, logger);
            }
            else if (returnType == typeof(int))
            {
                return BuildUserSpecificIntegerExpression(r, methodCall, logger);
            }
            else if (returnType == typeof(double) && r.MemberName == "LastPlayedDate")
            {
                return BuildUserSpecificLastPlayedDateExpression(r, methodCall, logger);
            }
            else if (returnType == typeof(string))
            {
                return BuildUserSpecificStringExpression(r, methodCall, logger);
            }
            else
            {
                logger?.LogError("SmartLists unsupported return type '{ReturnType}' for user-specific method '{Method}'", returnType.Name, methodName);
                throw new ArgumentException($"User-specific method '{methodName}' returns unsupported type '{returnType.Name}' for field '{r.MemberName}'");
            }
        }

        /// <summary>
        /// Validates and parses a boolean TargetValue for expression building.
        /// </summary>
        /// <param name="targetValue">The target value to validate and parse</param>
        /// <param name="fieldName">The field name for error reporting</param>
        /// <param name="logger">Optional logger for error reporting</param>
        /// <returns>The parsed boolean value</returns>
        /// <exception cref="ArgumentException">Thrown when the target value is invalid</exception>
        private static bool ValidateAndParseBooleanValue(string targetValue, string fieldName, ILogger? logger = null)
        {
            // Validate and parse boolean value safely
            if (string.IsNullOrWhiteSpace(targetValue))
            {
                logger?.LogError("SmartLists boolean comparison failed: TargetValue is null or empty for field '{Field}'", fieldName);
                throw new ArgumentException($"Boolean comparison requires a valid true/false value for field '{fieldName}', but got: '{targetValue}'");
            }

            // Strip quotes if present (JSON serialization may add them)
            var cleanedValue = targetValue.Trim().Trim('"').Trim('\'');

            if (!bool.TryParse(cleanedValue, out bool boolValue))
            {
                logger?.LogError("SmartLists boolean comparison failed: Invalid boolean value '{Value}' (cleaned: '{Cleaned}') for field '{Field}'", targetValue, cleanedValue, fieldName);
                throw new ArgumentException($"Invalid boolean value '{targetValue}' for field '{fieldName}'. Expected 'true' or 'false'.");
            }

            return boolValue;
        }

        /// <summary>
        /// Builds expressions for boolean user-specific fields.
        /// </summary>
        private static BinaryExpression BuildUserSpecificBooleanExpression(Expression r, System.Linq.Expressions.Expression methodCall, ILogger? logger)
        {
            if (r.Operator != "Equal" && r.Operator != "NotEqual")
            {
                logger?.LogError("SmartLists unsupported operator '{Operator}' for boolean user-specific field '{Field}'", r.Operator, r.MemberName);
                var supportedOperators = Operators.GetSupportedOperatorsString(r.MemberName);
                throw new ArgumentException($"Operator '{r.Operator}' is not supported for boolean user-specific field '{r.MemberName}'. Supported operators: {supportedOperators}");
            }

            var boolValue = ValidateAndParseBooleanValue(r.TargetValue, r.MemberName, logger);
            var right = System.Linq.Expressions.Expression.Constant(boolValue);
            return r.Operator == "Equal"
                ? System.Linq.Expressions.Expression.MakeBinary(ExpressionType.Equal, methodCall, right)
                : System.Linq.Expressions.Expression.MakeBinary(ExpressionType.NotEqual, methodCall, right);
        }

        /// <summary>
        /// Builds expressions for integer user-specific fields.
        /// </summary>
        private static BinaryExpression BuildUserSpecificIntegerExpression(Expression r, System.Linq.Expressions.Expression methodCall, ILogger? logger)
        {
            if (string.IsNullOrWhiteSpace(r.TargetValue))
            {
                logger?.LogError("SmartLists integer comparison failed: TargetValue is null or empty for field '{Field}'", r.MemberName);
                throw new ArgumentException($"Integer comparison requires a valid numeric value for field '{r.MemberName}', but got: '{r.TargetValue}'");
            }

            if (!int.TryParse(r.TargetValue, out int intValue))
            {
                logger?.LogError("SmartLists integer comparison failed: Invalid integer value '{Value}' for field '{Field}'", r.TargetValue, r.MemberName);
                throw new ArgumentException($"Invalid integer value '{r.TargetValue}' for field '{r.MemberName}'. Expected a valid number.");
            }

            var right = System.Linq.Expressions.Expression.Constant(intValue);

            // Check if the operator is a known .NET operator for integer comparison
            if (Enum.TryParse(r.Operator, out ExpressionType intBinary))
            {
                logger?.LogDebug("SmartLists {Operator} IS a built-in ExpressionType for integer field: {ExpressionType}", r.Operator, intBinary);
                return System.Linq.Expressions.Expression.MakeBinary(intBinary, methodCall, right);
            }
            else
            {
                logger?.LogError("SmartLists unsupported operator '{Operator}' for integer user-specific field '{Field}'", r.Operator, r.MemberName);
                var supportedOperators = Operators.GetSupportedOperatorsString(r.MemberName);
                throw new ArgumentException($"Operator '{r.Operator}' is not supported for integer user-specific field '{r.MemberName}'. Supported operators: {supportedOperators}");
            }
        }

        /// <summary>
        /// Builds expressions for string user-specific fields (e.g., PlaybackStatus).
        /// </summary>
        private static BinaryExpression BuildUserSpecificStringExpression(Expression r, System.Linq.Expressions.Expression methodCall, ILogger? logger)
        {
            if (r.Operator != "Equal" && r.Operator != "NotEqual")
            {
                logger?.LogError("SmartLists unsupported operator '{Operator}' for string user-specific field '{Field}'", r.Operator, r.MemberName);
                var supportedOperators = Operators.GetSupportedOperatorsString(r.MemberName);
                throw new ArgumentException($"Operator '{r.Operator}' is not supported for string user-specific field '{r.MemberName}'. Supported operators: {supportedOperators}");
            }

            if (string.IsNullOrWhiteSpace(r.TargetValue))
            {
                logger?.LogError("SmartLists string comparison failed: TargetValue is null or empty for field '{Field}'", r.MemberName);
                throw new ArgumentException($"String comparison requires a valid value for field '{r.MemberName}', but got: '{r.TargetValue}'");
            }

            // Legacy IsPlayed field support: Convert boolean values to PlaybackStatus values
            var cleanedTarget = r.TargetValue.Trim();
            if (r.MemberName == "IsPlayed")
            {
                // Strip quotes if present (JSON serialization may add them)
                // This matches the logic in ValidateAndParseBooleanValue for consistency
                var strippedValue = cleanedTarget.Trim('"').Trim('\'');
                
                // Convert "true"/"false" to "Played"/"Unplayed" for backward compatibility
                if (bool.TryParse(strippedValue, out bool boolValue))
                {
                    cleanedTarget = boolValue ? "Played" : "Unplayed";
                    logger?.LogDebug("SmartLists converted legacy IsPlayed value '{OldValue}' to PlaybackStatus value '{NewValue}'", r.TargetValue, cleanedTarget);
                }
            }

            // Use case-insensitive comparison for string fields
            var targetConstant = System.Linq.Expressions.Expression.Constant(cleanedTarget, typeof(string));
            var comparisonConstant = System.Linq.Expressions.Expression.Constant(StringComparison.OrdinalIgnoreCase);

            // Call string.Equals(methodCall, targetValue, StringComparison.OrdinalIgnoreCase)
            var equalsMethod = typeof(string).GetMethod("Equals", [typeof(string), typeof(string), typeof(StringComparison)]);
            if (equalsMethod == null)
            {
                throw new InvalidOperationException("String.Equals method not found");
            }

            var equalsCall = System.Linq.Expressions.Expression.Call(equalsMethod, methodCall, targetConstant, comparisonConstant);

            return r.Operator == "Equal"
                ? System.Linq.Expressions.Expression.Equal(equalsCall, System.Linq.Expressions.Expression.Constant(true))
                : System.Linq.Expressions.Expression.Equal(equalsCall, System.Linq.Expressions.Expression.Constant(false));
        }

        /// <summary>
        /// Builds expressions for user-specific LastPlayedDate fields with special "never played" handling.
        /// </summary>
        private static BinaryExpression BuildUserSpecificLastPlayedDateExpression(Expression r, System.Linq.Expressions.Expression methodCall, ILogger? logger)
        {
            logger?.LogDebug("SmartLists handling user-specific LastPlayedDate field {Field} with value {Value}", r.MemberName, r.TargetValue);

            // Create the "never played" check: methodCall != -1
            var neverPlayedCheck = System.Linq.Expressions.Expression.NotEqual(
                methodCall,
                System.Linq.Expressions.Expression.Constant(-1.0)
            );

            // Build the main date expression using a simplified version for method calls
            var mainExpression = BuildDateExpressionForMethodCall(r, methodCall, logger);

            // Combine: (methodCall != -1) AND (main date condition)
            return System.Linq.Expressions.Expression.AndAlso(neverPlayedCheck, mainExpression);
        }

        /// <summary>
        /// Builds date expressions for method calls (user-specific LastPlayedDate).
        /// </summary>
        private static BinaryExpression BuildDateExpressionForMethodCall(Expression r, System.Linq.Expressions.Expression methodCall, ILogger? logger)
        {
            // Handle Weekday operator
            if (r.Operator == "Weekday")
            {
                return BuildWeekdayExpressionForMethodCall(r, methodCall, logger);
            }

            // Handle relative date operators
            if (r.Operator == "NewerThan" || r.Operator == "OlderThan")
            {
                return BuildRelativeDateExpressionForMethodCall(r, methodCall, logger);
            }



            if (string.IsNullOrWhiteSpace(r.TargetValue))
            {
                logger?.LogError("SmartLists date comparison failed: TargetValue is null or empty for field '{Field}'", r.MemberName);
                throw new ArgumentException($"Date comparison requires a valid date value for field '{r.MemberName}', but got: '{r.TargetValue}'");
            }

            // Convert date string to Unix timestamp
            double targetTimestamp;
            try
            {
                targetTimestamp = ConvertDateStringToUnixTimestamp(r.TargetValue);
                logger?.LogDebug("SmartLists converted date '{DateString}' to Unix timestamp {Timestamp}", r.TargetValue, targetTimestamp);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "SmartLists date conversion failed for field '{Field}' with value '{Value}'", r.MemberName, r.TargetValue);
                throw new ArgumentException($"Invalid date format '{r.TargetValue}' for field '{r.MemberName}'. Expected format: YYYY-MM-DD");
            }

            // Handle basic date operators
            var right = System.Linq.Expressions.Expression.Constant(targetTimestamp);

            return r.Operator switch
            {
                "After" => System.Linq.Expressions.Expression.GreaterThan(methodCall, right),
                "Before" => System.Linq.Expressions.Expression.LessThan(methodCall, right),
                _ when Enum.TryParse(r.Operator, out ExpressionType dateBinary) =>
                    System.Linq.Expressions.Expression.MakeBinary(dateBinary, methodCall, right),
                _ => throw new ArgumentException($"Operator '{r.Operator}' is not currently supported for user-specific LastPlayedDate field. Supported operators: {Operators.GetSupportedOperatorsString(r.MemberName)}"),
            };
        }

        /// <summary>
        /// Builds relative date expressions for method calls (NewerThan, OlderThan).
        /// </summary>
        private static BinaryExpression BuildRelativeDateExpressionForMethodCall(Expression r, System.Linq.Expressions.Expression methodCall, ILogger? logger)
        {
            logger?.LogDebug("SmartLists handling '{Operator}' for user-specific field {Field} with value {Value}", r.Operator, r.MemberName, r.TargetValue);

            // Build Expression that calculates cutoff timestamp at runtime (prevents stale dates in rule cache)
            var cutoffTimestampExpr = BuildRelativeDateCutoffExpression(r, logger);

            if (r.Operator == "NewerThan")
            {
                // methodCall >= cutoffTimestamp (more recent than cutoff)
                return System.Linq.Expressions.Expression.GreaterThanOrEqual(methodCall, cutoffTimestampExpr);
            }
            else
            {
                // methodCall < cutoffTimestamp (older than cutoff)
                return System.Linq.Expressions.Expression.LessThan(methodCall, cutoffTimestampExpr);
            }
        }

        /// <summary>
        /// Builds expressions for Weekday operator for method calls (user-specific LastPlayedDate).
        /// </summary>
        private static BinaryExpression BuildWeekdayExpressionForMethodCall(Expression r, System.Linq.Expressions.Expression methodCall, ILogger? logger)
        {
            logger?.LogDebug("SmartLists handling 'Weekday' operator for user-specific field {Field} with value {Value}", r.MemberName, r.TargetValue);

            if (string.IsNullOrWhiteSpace(r.TargetValue))
            {
                logger?.LogError("SmartLists weekday comparison failed: TargetValue is null or empty for field '{Field}'", r.MemberName);
                throw new ArgumentException($"Weekday comparison requires a valid day of week value (0-6) for field '{r.MemberName}', but got: '{r.TargetValue}'");
            }

            // Parse target value as integer (0-6)
            if (!int.TryParse(r.TargetValue, out int targetDayOfWeek) || targetDayOfWeek < 0 || targetDayOfWeek > 6)
            {
                logger?.LogError("SmartLists weekday comparison failed: Invalid day of week value '{Value}' for field '{Field}'. Expected 0-6.", r.TargetValue, r.MemberName);
                throw new ArgumentException($"Invalid day of week value '{r.TargetValue}' for field '{r.MemberName}'. Expected integer 0-6 (0=Sunday, 6=Saturday).");
            }

            // Convert to DayOfWeek enum
            var targetDayOfWeekEnum = (DayOfWeek)targetDayOfWeek;

            // Create expression that:
            // 1. Converts Unix timestamp to DateTimeOffset in UTC
            // 2. Extracts DayOfWeek property
            // 3. Compares to target day of week

            // Get the method to convert timestamp to DateTimeOffset
            var fromUnixTimeSecondsMethod = typeof(DateTimeOffset).GetMethod("FromUnixTimeSeconds", [typeof(long)]);
            if (fromUnixTimeSecondsMethod == null)
                throw new InvalidOperationException("DateTimeOffset.FromUnixTimeSeconds method not found");

            // Convert double timestamp to long for the method call
            var timestampLong = System.Linq.Expressions.Expression.Convert(methodCall, typeof(long));

            // Call FromUnixTimeSeconds
            var dateTimeOffsetExpr = System.Linq.Expressions.Expression.Call(fromUnixTimeSecondsMethod, timestampLong);

            // Get UtcDateTime property
            var utcDateTimeProperty = typeof(DateTimeOffset).GetProperty("UtcDateTime");
            if (utcDateTimeProperty == null)
                throw new InvalidOperationException("DateTimeOffset.UtcDateTime property not found");

            var utcDateTimeExpr = System.Linq.Expressions.Expression.Property(dateTimeOffsetExpr, utcDateTimeProperty);

            // Get DayOfWeek property
            var dayOfWeekProperty = typeof(DateTime).GetProperty("DayOfWeek");
            if (dayOfWeekProperty == null)
                throw new InvalidOperationException("DateTime.DayOfWeek property not found");

            var dayOfWeekExpr = System.Linq.Expressions.Expression.Property(utcDateTimeExpr, dayOfWeekProperty);

            // Compare to target day of week
            var targetConstant = System.Linq.Expressions.Expression.Constant(targetDayOfWeekEnum);
            return System.Linq.Expressions.Expression.Equal(dayOfWeekExpr, targetConstant);
        }

        /// <summary>
        /// Parses relative date string (e.g., "3:days", "1:month") and returns cutoff timestamp.
        /// DEPRECATED: This method calculates a compile-time constant which becomes stale.
        /// Use BuildRelativeDateCutoffExpression instead for runtime calculation.
        /// </summary>
        [Obsolete("Use BuildRelativeDateCutoffExpression to generate runtime Expression instead of compile-time constant")]
        private static double ParseRelativeDateAndGetCutoffTimestamp(Expression r, ILogger? logger)
        {
            // Parse value as number:unit
            var parts = (r.TargetValue ?? "").Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int num) || num < 0)
            {
                logger?.LogError("SmartLists '{Operator}' requires value in format number:unit, got: '{Value}'", r.Operator, r.TargetValue);
                throw new ArgumentException($"'{r.Operator}' requires value in format number:unit, but got: '{r.TargetValue}'");
            }

            string unit = parts[1].ToLowerInvariant();
            DateTimeOffset cutoffDate = unit switch
            {
                "hours" => DateTimeOffset.UtcNow.AddHours(-num),
                "days" => DateTimeOffset.UtcNow.AddDays(-num),
                "weeks" => DateTimeOffset.UtcNow.AddDays(-num * 7),
                "months" => DateTimeOffset.UtcNow.AddMonths(-num),
                "years" => DateTimeOffset.UtcNow.AddYears(-num),
                _ => throw new ArgumentException($"Unknown unit '{unit}' for '{r.Operator}'"),
            };

            var cutoffTimestamp = (double)cutoffDate.ToUnixTimeSeconds();
            logger?.LogDebug("SmartLists '{Operator}' cutoff: {CutoffDate} (timestamp: {Timestamp})", r.Operator, cutoffDate, cutoffTimestamp);

            return cutoffTimestamp;
        }

        /// <summary>
        /// Builds an Expression that calculates the cutoff timestamp at runtime based on relative date string (e.g., "3:days", "1:month").
        /// This prevents "NewerThan" rules from becoming stale in the rule cache.
        /// </summary>
        private static System.Linq.Expressions.Expression BuildRelativeDateCutoffExpression(Expression r, ILogger? logger)
        {
            // Parse value as number:unit
            var parts = (r.TargetValue ?? "").Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int num) || num < 0)
            {
                logger?.LogError("SmartLists '{Operator}' requires value in format number:unit, got: '{Value}'", r.Operator, r.TargetValue);
                throw new ArgumentException($"'{r.Operator}' requires value in format number:unit, but got: '{r.TargetValue}'");
            }

            string unit = parts[1].ToLowerInvariant();

            // Get DateTimeOffset.UtcNow as an Expression
            var utcNowProperty = typeof(DateTimeOffset).GetProperty("UtcNow");
            if (utcNowProperty == null)
                throw new InvalidOperationException("DateTimeOffset.UtcNow property not found");
            var utcNowExpr = System.Linq.Expressions.Expression.Property(null, utcNowProperty);

            // Build Expression that calculates cutoff date at runtime
            System.Linq.Expressions.Expression cutoffDateExpr = unit switch
            {
                "hours" => System.Linq.Expressions.Expression.Call(utcNowExpr, typeof(DateTimeOffset).GetMethod("AddHours", new[] { typeof(double) })!, System.Linq.Expressions.Expression.Constant((double)-num)),
                "days" => System.Linq.Expressions.Expression.Call(utcNowExpr, typeof(DateTimeOffset).GetMethod("AddDays", new[] { typeof(double) })!, System.Linq.Expressions.Expression.Constant((double)-num)),
                "weeks" => System.Linq.Expressions.Expression.Call(utcNowExpr, typeof(DateTimeOffset).GetMethod("AddDays", new[] { typeof(double) })!, System.Linq.Expressions.Expression.Constant((double)(-num * 7))),
                "months" => System.Linq.Expressions.Expression.Call(utcNowExpr, typeof(DateTimeOffset).GetMethod("AddMonths", new[] { typeof(int) })!, System.Linq.Expressions.Expression.Constant(-num)),
                "years" => System.Linq.Expressions.Expression.Call(utcNowExpr, typeof(DateTimeOffset).GetMethod("AddYears", new[] { typeof(int) })!, System.Linq.Expressions.Expression.Constant(-num)),
                _ => throw new ArgumentException($"Unknown unit '{unit}' for '{r.Operator}'"),
            };

            // Convert DateTimeOffset to Unix timestamp (double)
            var toUnixTimeSecondsMethod = typeof(DateTimeOffset).GetMethod("ToUnixTimeSeconds");
            if (toUnixTimeSecondsMethod == null)
                throw new InvalidOperationException("DateTimeOffset.ToUnixTimeSeconds method not found");
            
            var unixTimeSecondsExpr = System.Linq.Expressions.Expression.Call(cutoffDateExpr, toUnixTimeSecondsMethod);
            var cutoffTimestampExpr = System.Linq.Expressions.Expression.Convert(unixTimeSecondsExpr, typeof(double));

            logger?.LogDebug("SmartLists '{Operator}' built runtime Expression for cutoff calculation: {Num} {Unit} ago", r.Operator, num, unit);

            return cutoffTimestampExpr;
        }

        /// <summary>
        /// Builds expressions for string fields.
        /// </summary>
        private static System.Linq.Expressions.Expression BuildStringExpression(Expression r, MemberExpression left, ILogger? logger)
        {
            // Enforce per-field operator whitelist for string fields
            var allowedOps = Operators.GetOperatorsForField(r.MemberName);
            if (!allowedOps.Contains(r.Operator))
            {
                logger?.LogError("SmartLists unsupported operator '{Operator}' for string field '{Field}'. Allowed: {Allowed}",
                    r.Operator, r.MemberName, string.Join(", ", allowedOps));
                var supportedOperators = Operators.GetSupportedOperatorsString(r.MemberName);
                throw new ArgumentException($"Operator '{r.Operator}' is not supported for string field '{r.MemberName}'. Supported operators: {supportedOperators}");
            }

            var right = System.Linq.Expressions.Expression.Constant(r.TargetValue, typeof(string));
            var comparison = System.Linq.Expressions.Expression.Constant(StringComparison.OrdinalIgnoreCase);

            switch (r.Operator)
            {
                case "Equal":
                    var equalsMethod = typeof(string).GetMethod("Equals", [typeof(string), typeof(StringComparison)]);
                    if (equalsMethod == null) throw new InvalidOperationException("String.Equals method not found");
                    return System.Linq.Expressions.Expression.Call(left, equalsMethod, right, comparison);
                case "NotEqual":
                    var notEqualsMethod = typeof(string).GetMethod("Equals", [typeof(string), typeof(StringComparison)]);
                    if (notEqualsMethod == null) throw new InvalidOperationException("String.Equals method not found");
                    var equalsCall = System.Linq.Expressions.Expression.Call(left, notEqualsMethod, right, comparison);
                    return System.Linq.Expressions.Expression.Not(equalsCall);
                case "Contains":
                    var containsMethod = typeof(string).GetMethod("Contains", [typeof(string), typeof(StringComparison)]);
                    if (containsMethod == null) throw new InvalidOperationException("String.Contains method not found");
                    return System.Linq.Expressions.Expression.Call(left, containsMethod, right, comparison);
                case "NotContains":
                    var notContainsMethod = typeof(string).GetMethod("Contains", [typeof(string), typeof(StringComparison)]);
                    if (notContainsMethod == null) throw new InvalidOperationException("String.Contains method not found");
                    var containsCall = System.Linq.Expressions.Expression.Call(left, notContainsMethod, right, comparison);
                    return System.Linq.Expressions.Expression.Not(containsCall);
                case "MatchRegex":
                    logger?.LogDebug("SmartLists applying single string MatchRegex to {Field}", r.MemberName);
                    var regex = GetOrCreateRegex(r.TargetValue, logger);
                    var method = typeof(Regex).GetMethod("IsMatch", [typeof(string)]);
                    if (method == null) throw new InvalidOperationException("Regex.IsMatch method not found");
                    var regexConstant = System.Linq.Expressions.Expression.Constant(regex);
                    return System.Linq.Expressions.Expression.Call(regexConstant, method, left);
                case "IsIn":
                    logger?.LogDebug("SmartLists applying string IsIn to {Field} with value '{Value}'", r.MemberName, r.TargetValue);
                    var isInMethod = typeof(Engine).GetMethod("StringIsInList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (isInMethod == null) throw new InvalidOperationException("Engine.StringIsInList method not found");
                    var targetValueConstant = System.Linq.Expressions.Expression.Constant(r.TargetValue, typeof(string));
                    return System.Linq.Expressions.Expression.Call(isInMethod, left, targetValueConstant);
                case "IsNotIn":
                    logger?.LogDebug("SmartLists applying string IsNotIn to {Field} with value '{Value}'", r.MemberName, r.TargetValue);
                    var isNotInMethod = typeof(Engine).GetMethod("StringIsInList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (isNotInMethod == null) throw new InvalidOperationException("Engine.StringIsInList method not found");
                    var targetValueConstant2 = System.Linq.Expressions.Expression.Constant(r.TargetValue, typeof(string));
                    var isNotInCall = System.Linq.Expressions.Expression.Call(isNotInMethod, left, targetValueConstant2);
                    return System.Linq.Expressions.Expression.Not(isNotInCall);
                default:
                    logger?.LogError("SmartLists unsupported string operator '{Operator}' for field '{Field}'", r.Operator, r.MemberName);
                    throw new ArgumentException($"Operator '{r.Operator}' is not supported for string field '{r.MemberName}'");
            }
        }

        /// <summary>
        /// Builds expressions for boolean fields.
        /// </summary>
        private static BinaryExpression BuildBooleanExpression(Expression r, MemberExpression left, ILogger? logger)
        {
            if (r.Operator != "Equal" && r.Operator != "NotEqual")
            {
                logger?.LogError("SmartLists unsupported operator '{Operator}' for boolean field '{Field}'", r.Operator, r.MemberName);
                var supportedOperators = Operators.GetSupportedOperatorsString(r.MemberName);
                throw new ArgumentException($"Operator '{r.Operator}' is not supported for boolean field '{r.MemberName}'. Supported operators: {supportedOperators}");
            }

            var boolValue = ValidateAndParseBooleanValue(r.TargetValue, r.MemberName, logger);
            var right = System.Linq.Expressions.Expression.Constant(boolValue);
            return r.Operator == "Equal"
                ? System.Linq.Expressions.Expression.MakeBinary(ExpressionType.Equal, left, right)
                : System.Linq.Expressions.Expression.MakeBinary(ExpressionType.NotEqual, left, right);
        }

        /// <summary>
        /// Builds expressions for date fields (stored as Unix timestamps).
        /// </summary>
        private static BinaryExpression BuildDateExpression(Expression r, MemberExpression left, ILogger? logger)
        {
            logger?.LogDebug("SmartLists handling date field {Field} with value {Value}", r.MemberName, r.TargetValue);

            // Special handling for LastPlayedDate: exclude items that have never been played (value = -1)
            if (r.MemberName == "LastPlayedDate")
            {
                var neverPlayedCheck = System.Linq.Expressions.Expression.NotEqual(
                    left,
                    System.Linq.Expressions.Expression.Constant(-1.0)
                );

                // Build the main date expression using the standard logic below
                var mainExpression = BuildStandardDateExpression(r, left, logger);

                // Combine: (LastPlayedDate != -1) AND (main date condition)
                return System.Linq.Expressions.Expression.AndAlso(neverPlayedCheck, mainExpression);
            }

            return BuildStandardDateExpression(r, left, logger);
        }

        /// <summary>
        /// Builds expressions for resolution fields that support both equality and numeric comparisons.
        /// </summary>
        private static BinaryExpression BuildResolutionExpression(Expression r, MemberExpression left, ILogger? logger)
        {
            logger?.LogDebug("SmartLists handling resolution field {Field} with value {Value}", r.MemberName, r.TargetValue);

            // Enforce per-field operator whitelist for resolution fields
            var allowedOps = Operators.GetOperatorsForField(r.MemberName);
            if (!allowedOps.Contains(r.Operator))
            {
                logger?.LogError("SmartLists unsupported operator '{Operator}' for resolution field '{Field}'. Allowed: {Allowed}",
                    r.Operator, r.MemberName, string.Join(", ", allowedOps));
                var supportedOperators = Operators.GetSupportedOperatorsString(r.MemberName);
                throw new ArgumentException($"Operator '{r.Operator}' is not supported for resolution field '{r.MemberName}'. Supported operators: {supportedOperators}");
            }

            // Get the numeric height value for the target resolution
            var targetHeight = ResolutionTypes.GetHeightForResolution(r.TargetValue);
            if (targetHeight == -1)
            {
                logger?.LogError("SmartLists resolution comparison failed: Invalid resolution value '{Value}' for field '{Field}'", r.TargetValue, r.MemberName);
                throw new ArgumentException($"Invalid resolution value '{r.TargetValue}' for field '{r.MemberName}'. Expected one of: {string.Join(", ", ResolutionTypes.GetAllValues())}");
            }

            // For all resolution comparisons, we need to ensure the resolution field is not null/empty
            // and that it's a valid resolution (height > 0)
            var resolutionHeightMethod = typeof(ResolutionTypes).GetMethod("GetHeightForResolution", [typeof(string)]);
            if (resolutionHeightMethod == null) throw new InvalidOperationException("ResolutionTypes.GetHeightForResolution method not found");
            var resolutionHeightCall = System.Linq.Expressions.Expression.Call(
                resolutionHeightMethod,
                left
            );

            var targetHeightConstant = System.Linq.Expressions.Expression.Constant(targetHeight);
            var zeroConstant = System.Linq.Expressions.Expression.Constant(0);

            // First, ensure the resolution is valid (not null/empty and height > 0)
            var isValidResolution = System.Linq.Expressions.Expression.GreaterThan(resolutionHeightCall, zeroConstant);

            // Handle different operators with validity check
            BinaryExpression comparisonExpression = r.Operator switch
            {
                "Equal" => System.Linq.Expressions.Expression.Equal(resolutionHeightCall, targetHeightConstant),
                "NotEqual" => System.Linq.Expressions.Expression.NotEqual(resolutionHeightCall, targetHeightConstant),
                "GreaterThan" => System.Linq.Expressions.Expression.GreaterThan(resolutionHeightCall, targetHeightConstant),
                "LessThan" => System.Linq.Expressions.Expression.LessThan(resolutionHeightCall, targetHeightConstant),
                "GreaterThanOrEqual" => System.Linq.Expressions.Expression.GreaterThanOrEqual(resolutionHeightCall, targetHeightConstant),
                "LessThanOrEqual" => System.Linq.Expressions.Expression.LessThanOrEqual(resolutionHeightCall, targetHeightConstant),
                _ => throw new ArgumentException($"Operator '{r.Operator}' is not supported for resolution field '{r.MemberName}'. Supported operators: {string.Join(", ", allowedOps)}"),
            };

            // Combine: resolution must be valid AND meet the comparison criteria
            return System.Linq.Expressions.Expression.AndAlso(isValidResolution, comparisonExpression);
        }

        /// <summary>
        /// Builds expressions for framerate fields that support numeric comparisons with null handling.
        /// Items with null framerate are ignored (filtered out).
        /// </summary>
        private static BinaryExpression BuildFramerateExpression(Expression r, MemberExpression left, ILogger? logger)
        {
            logger?.LogDebug("SmartLists handling framerate field {Field} with value {Value}", r.MemberName, r.TargetValue);

            // Enforce per-field operator whitelist for framerate fields
            var allowedOps = Operators.GetOperatorsForField(r.MemberName);
            if (!allowedOps.Contains(r.Operator))
            {
                logger?.LogError("SmartLists unsupported operator '{Operator}' for framerate field '{Field}'. Allowed: {Allowed}",
                    r.Operator, r.MemberName, string.Join(", ", allowedOps));
                var supportedOperators = Operators.GetSupportedOperatorsString(r.MemberName);
                throw new ArgumentException($"Operator '{r.Operator}' is not supported for framerate field '{r.MemberName}'. Supported operators: {supportedOperators}");
            }

            // Parse target value as float using culture-invariant parsing
            if (!float.TryParse(r.TargetValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var targetValue))
            {
                logger?.LogError("SmartLists framerate comparison failed: Invalid numeric value '{Value}' for field '{Field}'", r.TargetValue, r.MemberName);
                throw new ArgumentException($"Invalid numeric value '{r.TargetValue}' for field '{r.MemberName}'. Expected a decimal number.");
            }

            // For all framerate comparisons, we need to ensure the framerate field is not null
            // This means items with null framerate will be ignored
            var hasValueCheck = System.Linq.Expressions.Expression.Property(left, "HasValue");
            var valueProperty = System.Linq.Expressions.Expression.Property(left, "Value");
            var targetConstant = System.Linq.Expressions.Expression.Constant(targetValue);

            // Handle different operators with null check
            BinaryExpression comparisonExpression = r.Operator switch
            {
                "Equal" => System.Linq.Expressions.Expression.Equal(valueProperty, targetConstant),
                "NotEqual" => System.Linq.Expressions.Expression.NotEqual(valueProperty, targetConstant),
                "GreaterThan" => System.Linq.Expressions.Expression.GreaterThan(valueProperty, targetConstant),
                "LessThan" => System.Linq.Expressions.Expression.LessThan(valueProperty, targetConstant),
                "GreaterThanOrEqual" => System.Linq.Expressions.Expression.GreaterThanOrEqual(valueProperty, targetConstant),
                "LessThanOrEqual" => System.Linq.Expressions.Expression.LessThanOrEqual(valueProperty, targetConstant),
                _ => throw new ArgumentException($"Operator '{r.Operator}' is not supported for framerate field '{r.MemberName}'. Supported operators: {string.Join(", ", allowedOps)}"),
            };

            // Combine: framerate must have a value (not null) AND meet the comparison criteria
            return System.Linq.Expressions.Expression.AndAlso(hasValueCheck, comparisonExpression);
        }

        /// <summary>
        /// Builds standard date expressions without special handling for never-played items.
        /// </summary>
        private static BinaryExpression BuildStandardDateExpression(Expression r, MemberExpression left, ILogger? logger)
        {
            // Handle Weekday operator
            if (r.Operator == "Weekday")
            {
                return BuildWeekdayExpression(r, left, logger);
            }

            // Handle NewerThan and OlderThan operators first
            if (r.Operator == "NewerThan" || r.Operator == "OlderThan")
            {
                return BuildRelativeDateExpression(r, left, logger);
            }

            if (string.IsNullOrWhiteSpace(r.TargetValue))
            {
                logger?.LogError("SmartLists date comparison failed: TargetValue is null or empty for field '{Field}'", r.MemberName);
                throw new ArgumentException($"Date comparison requires a valid date value for field '{r.MemberName}', but got: '{r.TargetValue}'");
            }

            // Convert date string to Unix timestamp
            double targetTimestamp;
            try
            {
                targetTimestamp = ConvertDateStringToUnixTimestamp(r.TargetValue);
                logger?.LogDebug("SmartLists converted date '{DateString}' to Unix timestamp {Timestamp}", r.TargetValue, targetTimestamp);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "SmartLists date conversion failed for field '{Field}' with value '{Value}'", r.MemberName, r.TargetValue);
                throw new ArgumentException($"Invalid date format '{r.TargetValue}' for field '{r.MemberName}'. Expected format: YYYY-MM-DD");
            }

            // Handle date equality specially - compare date ranges instead of exact timestamps
            if (r.Operator == "Equal")
            {
                return BuildDateEqualityExpression(r, left, logger);
            }
            else if (r.Operator == "NotEqual")
            {
                return BuildDateInequalityExpression(r, left, logger);
            }

            else if (r.Operator == "After")
            {
                logger?.LogDebug("SmartLists 'After' operator for date field {Field} with timestamp {Timestamp}", r.MemberName, targetTimestamp);
                var right = System.Linq.Expressions.Expression.Constant(targetTimestamp);
                return System.Linq.Expressions.Expression.GreaterThan(left, right);
            }
            else if (r.Operator == "Before")
            {
                logger?.LogDebug("SmartLists 'Before' operator for date field {Field} with timestamp {Timestamp}", r.MemberName, targetTimestamp);
                var right = System.Linq.Expressions.Expression.Constant(targetTimestamp);
                return System.Linq.Expressions.Expression.LessThan(left, right);
            }
            else
            {
                // For other operators (legacy .NET ExpressionType), use the exact timestamp comparison
                if (Enum.TryParse(r.Operator, out ExpressionType dateBinary))
                {
                    logger?.LogDebug("SmartLists {Operator} IS a built-in ExpressionType for date field: {ExpressionType}", r.Operator, dateBinary);
                    var right = System.Linq.Expressions.Expression.Constant(targetTimestamp);
                    return System.Linq.Expressions.Expression.MakeBinary(dateBinary, left, right);
                }
                else
                {
                    logger?.LogError("SmartLists unsupported date operator '{Operator}' for field '{Field}'", r.Operator, r.MemberName);
                    var supportedOperators = Operators.GetSupportedOperatorsString(r.MemberName);
                    throw new ArgumentException($"Operator '{r.Operator}' is not supported for date field '{r.MemberName}'. Supported operators: {supportedOperators}");
                }
            }
        }

        /// <summary>
        /// Builds expressions for relative date operators (NewerThan, OlderThan).
        /// </summary>
        private static BinaryExpression BuildRelativeDateExpression(Expression r, MemberExpression left, ILogger? logger)
        {
            logger?.LogDebug("SmartLists handling '{Operator}' for field {Field} with value {Value}", r.Operator, r.MemberName, r.TargetValue);

            // Build Expression that calculates cutoff timestamp at runtime (prevents stale dates in rule cache)
            var cutoffTimestampExpr = BuildRelativeDateCutoffExpression(r, logger);

            if (r.Operator == "NewerThan")
            {
                // operand.DateCreated >= cutoffTimestamp
                return System.Linq.Expressions.Expression.GreaterThanOrEqual(left, cutoffTimestampExpr);
            }
            else
            {
                // operand.DateCreated < cutoffTimestamp
                return System.Linq.Expressions.Expression.LessThan(left, cutoffTimestampExpr);
            }
        }

        /// <summary>
        /// Builds expressions for Weekday operator (filters by day of week).
        /// </summary>
        private static BinaryExpression BuildWeekdayExpression(Expression r, MemberExpression left, ILogger? logger)
        {
            logger?.LogDebug("SmartLists handling 'Weekday' operator for field {Field} with value {Value}", r.MemberName, r.TargetValue);

            if (string.IsNullOrWhiteSpace(r.TargetValue))
            {
                logger?.LogError("SmartLists weekday comparison failed: TargetValue is null or empty for field '{Field}'", r.MemberName);
                throw new ArgumentException($"Weekday comparison requires a valid day of week value (0-6) for field '{r.MemberName}', but got: '{r.TargetValue}'");
            }

            // Parse target value as integer (0-6)
            if (!int.TryParse(r.TargetValue, out int targetDayOfWeek) || targetDayOfWeek < 0 || targetDayOfWeek > 6)
            {
                logger?.LogError("SmartLists weekday comparison failed: Invalid day of week value '{Value}' for field '{Field}'. Expected 0-6.", r.TargetValue, r.MemberName);
                throw new ArgumentException($"Invalid day of week value '{r.TargetValue}' for field '{r.MemberName}'. Expected integer 0-6 (0=Sunday, 6=Saturday).");
            }

            // Convert to DayOfWeek enum
            var targetDayOfWeekEnum = (DayOfWeek)targetDayOfWeek;

            // Create expression that:
            // 1. Converts Unix timestamp to DateTimeOffset in UTC
            // 2. Extracts DayOfWeek property
            // 3. Compares to target day of week

            // Get the method to convert timestamp to DateTimeOffset
            var fromUnixTimeSecondsMethod = typeof(DateTimeOffset).GetMethod("FromUnixTimeSeconds", [typeof(long)]);
            if (fromUnixTimeSecondsMethod == null)
                throw new InvalidOperationException("DateTimeOffset.FromUnixTimeSeconds method not found");

            // Convert double timestamp to long for the method call
            var timestampLong = System.Linq.Expressions.Expression.Convert(left, typeof(long));

            // Call FromUnixTimeSeconds
            var dateTimeOffsetExpr = System.Linq.Expressions.Expression.Call(fromUnixTimeSecondsMethod, timestampLong);

            // Get UtcDateTime property
            var utcDateTimeProperty = typeof(DateTimeOffset).GetProperty("UtcDateTime");
            if (utcDateTimeProperty == null)
                throw new InvalidOperationException("DateTimeOffset.UtcDateTime property not found");

            var utcDateTimeExpr = System.Linq.Expressions.Expression.Property(dateTimeOffsetExpr, utcDateTimeProperty);

            // Get DayOfWeek property
            var dayOfWeekProperty = typeof(DateTime).GetProperty("DayOfWeek");
            if (dayOfWeekProperty == null)
                throw new InvalidOperationException("DateTime.DayOfWeek property not found");

            var dayOfWeekExpr = System.Linq.Expressions.Expression.Property(utcDateTimeExpr, dayOfWeekProperty);

            // Compare to target day of week
            var targetConstant = System.Linq.Expressions.Expression.Constant(targetDayOfWeekEnum);
            return System.Linq.Expressions.Expression.Equal(dayOfWeekExpr, targetConstant);
        }

        /// <summary>
        /// Builds expressions for date equality (comparing date ranges).
        /// </summary>
        private static BinaryExpression BuildDateEqualityExpression(Expression r, MemberExpression left, ILogger? logger)
        {
            logger?.LogDebug("SmartLists handling date equality for field {Field} with date {Date}", r.MemberName, r.TargetValue);

            // For equality, we need to check if the date falls within the target day
            // Convert the target date to start and end of day timestamps using UTC
            if (!DateTime.TryParseExact(r.TargetValue, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime targetDate))
            {
                logger?.LogError("SmartLists date equality failed: Invalid date format '{Value}' for field '{Field}'", r.TargetValue, r.MemberName);
                throw new ArgumentException($"Invalid date format '{r.TargetValue}' for field '{r.MemberName}'. Expected format: YYYY-MM-DD");
            }

            var startOfDay = (double)new DateTimeOffset(targetDate, TimeSpan.Zero).ToUnixTimeSeconds();
            var endOfDay = (double)new DateTimeOffset(targetDate.AddDays(1), TimeSpan.Zero).ToUnixTimeSeconds();

            logger?.LogDebug("SmartLists date equality range: {StartOfDay} to {EndOfDay} (exclusive)", startOfDay, endOfDay);

            // Create expression: operand.DateCreated >= startOfDay && operand.DateCreated < endOfDay
            var startConstant = System.Linq.Expressions.Expression.Constant(startOfDay);
            var endConstant = System.Linq.Expressions.Expression.Constant(endOfDay);

            var greaterThanOrEqual = System.Linq.Expressions.Expression.GreaterThanOrEqual(left, startConstant);
            var lessThan = System.Linq.Expressions.Expression.LessThan(left, endConstant);

            return System.Linq.Expressions.Expression.AndAlso(greaterThanOrEqual, lessThan);
        }

        /// <summary>
        /// Builds expressions for date inequality (outside date ranges).
        /// </summary>
        private static BinaryExpression BuildDateInequalityExpression(Expression r, MemberExpression left, ILogger? logger)
        {
            logger?.LogDebug("SmartLists handling date inequality for field {Field} with date {Date}", r.MemberName, r.TargetValue);

            // For inequality, we need to check if the date is outside the target day
            if (!DateTime.TryParseExact(r.TargetValue, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime targetDate))
            {
                logger?.LogError("SmartLists date inequality failed: Invalid date format '{Value}' for field '{Field}'", r.TargetValue, r.MemberName);
                throw new ArgumentException($"Invalid date format '{r.TargetValue}' for field '{r.MemberName}'. Expected format: YYYY-MM-DD");
            }

            var startOfDay = (double)new DateTimeOffset(targetDate, TimeSpan.Zero).ToUnixTimeSeconds();
            var endOfDay = (double)new DateTimeOffset(targetDate.AddDays(1), TimeSpan.Zero).ToUnixTimeSeconds();

            logger?.LogDebug("SmartLists date inequality range: < {StartOfDay} or >= {EndOfDay}", startOfDay, endOfDay);

            // Create expression: operand.DateCreated < startOfDay || operand.DateCreated >= endOfDay
            var startConstant = System.Linq.Expressions.Expression.Constant(startOfDay);
            var endConstant = System.Linq.Expressions.Expression.Constant(endOfDay);

            var lessThan = System.Linq.Expressions.Expression.LessThan(left, startConstant);
            var greaterThanOrEqual = System.Linq.Expressions.Expression.GreaterThanOrEqual(left, endConstant);

            return System.Linq.Expressions.Expression.OrElse(lessThan, greaterThanOrEqual);
        }



        /// <summary>
        /// Builds expressions for IEnumerable fields (collections).
        /// </summary>
        private static System.Linq.Expressions.Expression? BuildEnumerableExpression(Expression r, MemberExpression left, Type tProp, ILogger? logger)
        {
            var ienumerable = tProp.GetInterface("IEnumerable`1");
            logger?.LogDebug("SmartLists field {Field}: Type={Type}, IEnumerable={IsEnumerable}, Operator={Operator}",
                r.MemberName, tProp.Name, ienumerable != null, r.Operator);

            if (ienumerable == null)
            {
                logger?.LogDebug("SmartLists field {Field} is not IEnumerable", r.MemberName);
                return null;
            }

            if (ienumerable.GetGenericArguments()[0] == typeof(string))
            {
                return BuildStringEnumerableExpression(r, left, logger);
            }
            else
            {
                return BuildGenericEnumerableExpression(r, left, ienumerable, logger);
            }
        }

        /// <summary>
        /// Builds expressions for string IEnumerable fields.
        /// </summary>
        private static System.Linq.Expressions.Expression BuildStringEnumerableExpression(Expression r, MemberExpression left, ILogger? logger)
        {
            if (r.Operator == "Equal" || r.Operator == "NotEqual")
            {
                logger?.LogDebug("SmartLists applying exclusive {Operator} to {Field} with value '{Value}'", r.Operator, r.MemberName, r.TargetValue);
                var right = System.Linq.Expressions.Expression.Constant(r.TargetValue, typeof(string));

                // Equal/NotEqual on list fields means "exclusively equals" - the list must contain
                // ONLY this value and no others. Use special methods for Collections/Playlists
                // that handle prefix/suffix stripping.
                System.Reflection.MethodInfo? method;
                if (r.MemberName == "Collections")
                {
                    method = typeof(Engine).GetMethod("OnlyCollectionEquals", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (method == null) throw new InvalidOperationException("Engine.OnlyCollectionEquals method not found");
                }
                else if (r.MemberName == "Playlists")
                {
                    method = typeof(Engine).GetMethod("OnlyPlaylistEquals", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (method == null) throw new InvalidOperationException("Engine.OnlyPlaylistEquals method not found");
                }
                else
                {
                    method = typeof(Engine).GetMethod("OnlyItemEquals", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (method == null) throw new InvalidOperationException("Engine.OnlyItemEquals method not found");
                }

                var equalsCall = System.Linq.Expressions.Expression.Call(method, left, right);
                if (r.Operator == "Equal") return equalsCall;
                if (r.Operator == "NotEqual") return System.Linq.Expressions.Expression.Not(equalsCall);
            }

            // All multi-valued fields (including Collections and Playlists) support the full operator set
            // Support all operators including NotEqual, NotContains and IsNotIn
            if (r.Operator == "Contains" || r.Operator == "NotContains")
            {
                var right = System.Linq.Expressions.Expression.Constant(r.TargetValue, typeof(string));
                var method = typeof(Engine).GetMethod("AnyItemContains", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (method == null) throw new InvalidOperationException("Engine.AnyItemContains method not found");
                var containsCall = System.Linq.Expressions.Expression.Call(method, left, right);
                if (r.Operator == "Contains") return containsCall;
                if (r.Operator == "NotContains") return System.Linq.Expressions.Expression.Not(containsCall);
            }

            if (r.Operator == "IsIn")
            {
                logger?.LogDebug("SmartLists applying collection IsIn to {Field} with value '{Value}'", r.MemberName, r.TargetValue);
                var right = System.Linq.Expressions.Expression.Constant(r.TargetValue, typeof(string));
                var method = typeof(Engine).GetMethod("AnyItemIsInList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (method == null)
                {
                    logger?.LogError("SmartLists AnyItemIsInList method not found!");
                    throw new InvalidOperationException("AnyItemIsInList method not found");
                }
                return System.Linq.Expressions.Expression.Call(method, left, right);
            }

            if (r.Operator == "IsNotIn")
            {
                logger?.LogDebug("SmartLists applying collection IsNotIn to {Field} with value '{Value}'", r.MemberName, r.TargetValue);
                var right = System.Linq.Expressions.Expression.Constant(r.TargetValue, typeof(string));
                var method = typeof(Engine).GetMethod("AnyItemIsInList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (method == null)
                {
                    logger?.LogError("SmartLists AnyItemIsInList method not found!");
                    throw new InvalidOperationException("AnyItemIsInList method not found");
                }
                var isNotInCall = System.Linq.Expressions.Expression.Call(method, left, right);
                return System.Linq.Expressions.Expression.Not(isNotInCall);
            }

            if (r.Operator == "MatchRegex")
            {
                var right = System.Linq.Expressions.Expression.Constant(r.TargetValue, typeof(string));
                var method = typeof(Engine).GetMethod("AnyRegexMatch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (method == null)
                {
                    logger?.LogError("SmartLists AnyRegexMatch method not found!");
                    throw new InvalidOperationException("AnyRegexMatch method not found");
                }
                logger?.LogDebug("SmartLists building regex expression for field: {Field}, pattern: {Pattern}", r.MemberName, r.TargetValue);
                return System.Linq.Expressions.Expression.Call(method, left, right);
            }

            var supportedOperators = Operators.GetOperatorsForField(r.MemberName);
            var supportedOperatorsString = string.Join(", ", supportedOperators);
            logger?.LogError("SmartLists unsupported operator '{Operator}' for string IEnumerable field '{Field}'", r.Operator, r.MemberName);
            throw new ArgumentException($"Operator '{r.Operator}' is not supported for string IEnumerable field '{r.MemberName}'. Supported operators: {supportedOperatorsString}");
        }



        /// <summary>
        /// Builds expressions for generic IEnumerable fields.
        /// </summary>
        private static System.Linq.Expressions.Expression BuildGenericEnumerableExpression(Expression r, MemberExpression left, Type ienumerable, ILogger? logger)
        {
            if (r.Operator == "Contains" || r.Operator == "NotContains")
            {
                var genericType = ienumerable.GetGenericArguments()[0];
                var convertedRight = System.Linq.Expressions.Expression.Constant(Convert.ChangeType(r.TargetValue, genericType, System.Globalization.CultureInfo.InvariantCulture));
                var method = typeof(Enumerable).GetMethods()
                    .First(m => m.Name == "Contains" && m.GetParameters().Length == 2)
                    .MakeGenericMethod(genericType);

                var call = System.Linq.Expressions.Expression.Call(method, left, convertedRight);
                if (r.Operator == "Contains") return call;
                if (r.Operator == "NotContains") return System.Linq.Expressions.Expression.Not(call);
            }

            var supportedOperators = Operators.GetOperatorsForField(r.MemberName);
            var supportedOperatorsString = string.Join(", ", supportedOperators);
            logger?.LogError("SmartLists unsupported operator '{Operator}' for generic IEnumerable field '{Field}'", r.Operator, r.MemberName);
            throw new ArgumentException($"Operator '{r.Operator}' is not supported for generic IEnumerable field '{r.MemberName}'. Supported operators: {supportedOperatorsString}");
        }

        /// <summary>
        /// Builds expressions using standard .NET operators for other field types.
        /// </summary>
        private static BinaryExpression BuildStandardOperatorExpression(Expression r, MemberExpression left, Type tProp, ILogger? logger)
        {
            // Check if the operator is a known .NET operator
            logger?.LogDebug("SmartLists checking if {Operator} is a built-in .NET ExpressionType", r.Operator);
            if (Enum.TryParse(r.Operator, out ExpressionType tBinary))
            {
                logger?.LogDebug("SmartLists {Operator} IS a built-in ExpressionType: {ExpressionType}", r.Operator, tBinary);
                var right = System.Linq.Expressions.Expression.Constant(Convert.ChangeType(r.TargetValue, tProp, System.Globalization.CultureInfo.InvariantCulture));
                // use a binary operation, e.g. 'Equal' -> 'u.Age == 15'
                return System.Linq.Expressions.Expression.MakeBinary(tBinary, left, right);
            }

            // All supported operators have been handled explicitly above
            // If we reach here, the operator is not supported for this field type
            logger?.LogError("SmartLists unsupported operator '{Operator}' for field '{Field}' of type '{Type}'", r.Operator, r.MemberName, tProp.Name);
            var supportedOperators = Operators.GetSupportedOperatorsString(r.MemberName);
            throw new ArgumentException($"Operator '{r.Operator}' is not supported for field '{r.MemberName}' of type '{tProp.Name}'. Supported operators: {supportedOperators}");
        }

        /// <summary>
        /// Checks if a field name is a date field that needs special handling.
        /// </summary>
        /// <param name="fieldName">The field name to check</param>
        /// <returns>True if it's a date field, false otherwise</returns>
        private static bool IsDateField(string fieldName)
        {
            return FieldRegistry.IsDateField(fieldName);
        }

        /// <summary>
        /// Checks if a field name is a resolution field that needs special handling.
        /// </summary>
        /// <param name="fieldName">The field name to check</param>
        /// <returns>True if it's a resolution field, false otherwise</returns>
        private static bool IsResolutionField(string fieldName)
        {
            return FieldRegistry.IsResolutionField(fieldName);
        }

        private static bool IsFramerateField(string fieldName)
        {
            return FieldRegistry.IsFramerateField(fieldName);
        }

        /// <summary>
        /// Converts a date string (YYYY-MM-DD) to Unix timestamp.
        /// </summary>
        /// <param name="dateString">The date string to convert</param>
        /// <returns>Unix timestamp in seconds</returns>
        /// <exception cref="ArgumentException">Thrown when the date string is invalid</exception>
        private static double ConvertDateStringToUnixTimestamp(string dateString)
        {
            if (string.IsNullOrWhiteSpace(dateString))
            {
                throw new ArgumentException("Date string cannot be null or empty");
            }

            try
            {
                // Parse the date string as YYYY-MM-DD format
                if (DateTime.TryParseExact(dateString, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime parsedDate))
                {
                    // Convert to Unix timestamp using UTC to ensure consistency with other date operations
                    return new DateTimeOffset(parsedDate, TimeSpan.Zero).ToUnixTimeSeconds();
                }
                else
                {
                    throw new ArgumentException($"Invalid date format: {dateString}. Expected format: YYYY-MM-DD");
                }
            }
            catch (Exception ex) when (ex is not ArgumentException)
            {
                throw new ArgumentException($"Failed to parse date string '{dateString}': {ex.Message}", ex);
            }
        }

        public static Func<T, bool> CompileRule<T>(Expression r, string defaultUserId, ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(r);

            var paramUser = System.Linq.Expressions.Expression.Parameter(typeof(T));
            var expr = BuildExpr<T>(r, paramUser, defaultUserId, logger);
            return System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(expr, paramUser).Compile();
        }

        internal static bool AnyItemContains(IEnumerable<string> list, string value)
        {
            if (list == null) return false;
            return list.Any(s => s != null && s.Contains(value, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool AnyItemEquals(IEnumerable<string> list, string value)
        {
            return AnyEqualsWithOptionalStripping(list, value, stripPrefixSuffix: false);
        }

        /// <summary>
        /// Checks if any item in a list equals the target value, with optional prefix/suffix stripping.
        /// Used for Collections and Playlists fields where items may have prefix/suffix applied.
        /// This handles cases where items have prefix/suffix applied but users enter base name.
        /// </summary>
        /// <param name="list">The list of items to check</param>
        /// <param name="value">The target value to match</param>
        /// <param name="stripPrefixSuffix">Whether to also check stripped versions (without prefix/suffix)</param>
        /// <returns>True if any item matches, false otherwise</returns>
        private static bool AnyEqualsWithOptionalStripping(IEnumerable<string> list, string value, bool stripPrefixSuffix)
        {
            if (list == null) return false;
            
            if (stripPrefixSuffix)
            {
                return list.Any(s => 
                    s != null && 
                    (s.Equals(value, StringComparison.OrdinalIgnoreCase) ||
                     (NameFormatter.StripPrefixAndSuffix(s) is string stripped && stripped.Equals(value, StringComparison.OrdinalIgnoreCase))));
            }
            else
            {
                return list.Any(s => s != null && s.Equals(value, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Checks if any item in the collection list equals the target value.
        /// For Collections field, this also checks items without prefix/suffix.
        /// This handles cases where collections have prefix/suffix applied but users enter base name.
        /// </summary>
        internal static bool AnyCollectionEquals(IEnumerable<string> list, string value)
        {
            return AnyEqualsWithOptionalStripping(list, value, stripPrefixSuffix: true);
        }

        /// <summary>
        /// Checks if any item in the playlist list equals the target value.
        /// For Playlists field, this also checks items without prefix/suffix.
        /// This handles cases where playlists have prefix/suffix applied but users enter base name.
        /// </summary>
        internal static bool AnyPlaylistEquals(IEnumerable<string> list, string value)
        {
            return AnyEqualsWithOptionalStripping(list, value, stripPrefixSuffix: true);
        }

        /// <summary>
        /// Checks if the list contains exactly one item and that item equals the target value.
        /// Used for Equal/NotEqual on list fields where "equals" means "exclusively contains this value".
        /// </summary>
        internal static bool OnlyItemEquals(IEnumerable<string> list, string value)
        {
            return OnlyEqualsWithOptionalStripping(list, value, stripPrefixSuffix: false);
        }

        /// <summary>
        /// Exclusive equals for Collections field, with prefix/suffix stripping support.
        /// </summary>
        internal static bool OnlyCollectionEquals(IEnumerable<string> list, string value)
        {
            return OnlyEqualsWithOptionalStripping(list, value, stripPrefixSuffix: true);
        }

        /// <summary>
        /// Exclusive equals for Playlists field, with prefix/suffix stripping support.
        /// </summary>
        internal static bool OnlyPlaylistEquals(IEnumerable<string> list, string value)
        {
            return OnlyEqualsWithOptionalStripping(list, value, stripPrefixSuffix: true);
        }

        /// <summary>
        /// Checks if the list contains exactly one item and that item equals the target value,
        /// with optional prefix/suffix stripping for Collections/Playlists fields.
        /// </summary>
        private static bool OnlyEqualsWithOptionalStripping(IEnumerable<string> list, string value, bool stripPrefixSuffix)
        {
            if (list == null) return false;

            var items = list.Where(s => !string.IsNullOrEmpty(s)).Take(2).ToList();
            if (items.Count != 1) return false;

            var item = items[0];
            if (item.Equals(value, StringComparison.OrdinalIgnoreCase)) return true;

            if (stripPrefixSuffix && NameFormatter.StripPrefixAndSuffix(item) is string stripped)
            {
                return stripped.Equals(value, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        /// <summary>
        /// Checks if the combined (deduplicated) entries from both lists contain exactly one
        /// distinct value and that value equals the target. Used for Equal/NotEqual on
        /// Tags/Studios/Genres when "Include parent series" is enabled.
        /// </summary>
        private static bool OnlyCombinedItemEquals(IEnumerable<string> list, IEnumerable<string> parentList, string value)
        {
            var items = (list ?? Enumerable.Empty<string>())
                .Concat(parentList ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToList();

            return items.Count == 1 && items[0].Equals(value, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool AnyRegexMatch(IEnumerable<string> list, string pattern)
        {
            if (list == null) return false;

            try
            {
                var regex = GetOrCreateRegex(pattern);
                
                // Convert to list to check if empty and iterate
                var listItems = list.ToList();
                
                // If the list is empty, check if the regex matches an empty string
                // This handles cases like ^$ which should match items with no tags/genres/etc.
                if (listItems.Count == 0)
                {
                    return regex.IsMatch(string.Empty);
                }
                
                // Otherwise, check if any item in the list matches the regex
                return listItems.Any(s => s != null && regex.IsMatch(s));
            }
            catch (ArgumentException ex)
            {
                // Preserve the original error details while providing context
                throw new ArgumentException($"Invalid regex pattern '{pattern}': {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                // For other unexpected errors, preserve the original exception details
                throw new ArgumentException($"Regex pattern '{pattern}' caused an error: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Helper method for string IsIn operator - checks if a single string contains any item from a semicolon-separated list.
        /// </summary>
        /// <param name="fieldValue">The field value to check</param>
        /// <param name="targetList">Semicolon-separated list of values to check against</param>
        /// <returns>True if the field value contains any item in the target list</returns>
        internal static bool StringIsInList(string fieldValue, string targetList)
        {
            if (string.IsNullOrEmpty(fieldValue) || string.IsNullOrEmpty(targetList))
                return false;

            // Split by semicolon, trim whitespace, and filter out empty strings
            var listItems = targetList.Split(';', StringSplitOptions.RemoveEmptyEntries)
                                     .Select(item => item.Trim())
                                     .Where(item => !string.IsNullOrEmpty(item))
                                     .ToList();

            if (listItems.Count == 0)
                return false;

            // Check if fieldValue contains any item in the list (case insensitive, partial matching)
            return listItems.Any(item => fieldValue.Contains(item, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Helper method for collection IsIn operator - checks if any item in a collection contains any item from a semicolon-separated list.
        /// </summary>
        /// <param name="collection">The collection of strings to check</param>
        /// <param name="targetList">Semicolon-separated list of values to check against</param>
        /// <returns>True if any item in the collection contains any item in the target list</returns>
        internal static bool AnyItemIsInList(IEnumerable<string> collection, string targetList)
        {
            if (collection == null || string.IsNullOrEmpty(targetList))
                return false;

            // Split by semicolon, trim whitespace, and filter out empty strings
            var listItems = targetList.Split(';', StringSplitOptions.RemoveEmptyEntries)
                                     .Select(item => item.Trim())
                                     .Where(item => !string.IsNullOrEmpty(item))
                                     .ToList();

            if (listItems.Count == 0)
                return false;

            // Check if any item in the collection contains any item in the target list (case insensitive, partial matching)
            return collection.Any(collectionItem =>
                collectionItem != null &&
                listItems.Any(targetItem =>
                    collectionItem.Contains(targetItem, StringComparison.OrdinalIgnoreCase)));
        }
        public static List<ExpressionSet> FixRuleSets(List<ExpressionSet> rulesets)
        {
            return rulesets;
        }

        public static ExpressionSet FixRules(ExpressionSet rules)
        {
            return rules;
        }
    }
}