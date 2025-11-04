using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Jfresolve.Utilities
{
    /// <summary>
    /// Utility class for working with JSON elements.
    /// Consolidates JSON parsing helpers that were duplicated across multiple files.
    /// Extracted from: JfresolveProvider.cs, JfresolvePopulator.cs
    /// </summary>
    public static class JsonHelper
    {
        /// <summary>
        /// Safely extracts a string value from a JSON element.
        /// Handles both string and numeric types by converting numbers to strings.
        /// Returns empty string if property doesn't exist or value is null.
        /// </summary>
        public static string GetJsonString(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null)
            {
                // Handle string types
                if (property.ValueKind == JsonValueKind.String)
                {
                    return property.GetString() ?? string.Empty;
                }

                // Handle numeric types (e.g., TMDB API sometimes returns numbers as strings are expected)
                if (property.ValueKind == JsonValueKind.Number)
                {
                    return property.GetRawText();
                }

                // Fallback: convert to string representation
                return property.ToString();
            }

            return string.Empty;
        }

        /// <summary>
        /// Safely extracts a double value from a JSON element.
        /// Returns 0 if property doesn't exist or value is null.
        /// </summary>
        public static double GetJsonDouble(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number)
            {
                return property.GetDouble();
            }

            return 0;
        }

        /// <summary>
        /// Safely extracts an integer value from a JSON element.
        /// Returns 0 if property doesn't exist or value is null.
        /// </summary>
        public static int GetJsonInt(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number)
            {
                return property.GetInt32();
            }

            return 0;
        }

        /// <summary>
        /// Safely extracts a boolean value from a JSON element.
        /// Returns false if property doesn't exist or value is null.
        /// </summary>
        public static bool GetJsonBool(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.True)
            {
                return property.GetBoolean();
            }

            return false;
        }

        /// <summary>
        /// Safely extracts an array of integers from a JSON element.
        /// Returns empty list if property doesn't exist or is not an array.
        /// </summary>
        public static List<int> GetJsonIntArray(JsonElement element, string propertyName)
        {
            var result = new List<int>();

            if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number)
                    {
                        result.Add(item.GetInt32());
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Safely extracts an array of strings from a JSON element.
        /// Returns empty list if property doesn't exist or is not an array.
        /// </summary>
        public static List<string> GetJsonStringArray(JsonElement element, string propertyName)
        {
            var result = new List<string>();

            if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Null)
                    {
                        var str = item.GetString();
                        if (!string.IsNullOrEmpty(str))
                        {
                            result.Add(str);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Safely extracts a JsonElement property from a JSON element.
        /// Returns null if property doesn't exist.
        /// </summary>
        public static JsonElement? GetJsonElement(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var property))
            {
                return property;
            }

            return null;
        }

        /// <summary>
        /// Safely enumerates an array property in a JSON element.
        /// Returns empty enumerator if property doesn't exist or is not an array.
        /// </summary>
        public static JsonElement.ArrayEnumerator? GetJsonArray(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array)
            {
                return property.EnumerateArray();
            }

            return null;
        }

        /// <summary>
        /// Gets the count of items in a JSON array property.
        /// Returns 0 if property doesn't exist or is not an array.
        /// </summary>
        public static int GetJsonArrayLength(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array)
            {
                return property.GetArrayLength();
            }

            return 0;
        }
    }
}
