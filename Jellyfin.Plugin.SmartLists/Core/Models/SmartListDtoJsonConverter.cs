using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.SmartLists.Core.Enums;

namespace Jellyfin.Plugin.SmartLists.Core.Models
{
    /// <summary>
    /// Converts the abstract SmartListDto using the existing public Type property.
    /// </summary>
    public class SmartListDtoJsonConverter : JsonConverter<SmartListDto>
    {
        public override SmartListDto? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var listType = GetListType(root);
            var json = root.GetRawText();

            return listType == SmartListType.Collection
                ? JsonSerializer.Deserialize<SmartCollectionDto>(json, options)
                : JsonSerializer.Deserialize<SmartPlaylistDto>(json, options);
        }

        public override void Write(Utf8JsonWriter writer, SmartListDto value, JsonSerializerOptions options)
        {
            switch (value)
            {
                case SmartCollectionDto collection:
                    JsonSerializer.Serialize(writer, collection, options);
                    break;
                case SmartPlaylistDto playlist:
                    JsonSerializer.Serialize(writer, playlist, options);
                    break;
                default:
                    throw new JsonException($"Unsupported smart list type: {value.GetType().FullName}");
            }
        }

        private static SmartListType GetListType(JsonElement root)
        {
            if (!TryGetProperty(root, "Type", out var typeElement))
            {
                throw new JsonException("Smart list JSON is missing required Type property.");
            }

            if (typeElement.ValueKind == JsonValueKind.String
                && Enum.TryParse<SmartListType>(typeElement.GetString(), ignoreCase: true, out var stringType))
            {
                if (!Enum.IsDefined(stringType))
                {
                    throw new JsonException($"Smart list Type value '{typeElement.GetString()}' is not supported.");
                }

                return stringType;
            }

            if (typeElement.ValueKind == JsonValueKind.Number
                && typeElement.TryGetInt32(out var numericType))
            {
                var listType = (SmartListType)numericType;
                if (!Enum.IsDefined(listType))
                {
                    throw new JsonException($"Smart list Type value '{numericType}' is not supported.");
                }

                return listType;
            }

            throw new JsonException("Smart list Type must be either a string or numeric SmartListType value.");
        }

        private static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement value)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }
    }
}
