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
            if (!root.TryGetProperty("Type", out var typeElement))
            {
                return SmartListType.Playlist;
            }

            if (typeElement.ValueKind == JsonValueKind.String
                && Enum.TryParse<SmartListType>(typeElement.GetString(), ignoreCase: true, out var stringType))
            {
                return stringType;
            }

            if (typeElement.ValueKind == JsonValueKind.Number
                && typeElement.TryGetInt32(out var numericType))
            {
                return numericType == (int)SmartListType.Collection
                    ? SmartListType.Collection
                    : SmartListType.Playlist;
            }

            return SmartListType.Playlist;
        }
    }
}
