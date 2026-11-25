using System.Text.Json;
using System.Text.Json.Serialization;
using FastEnumUtility;

namespace KSP.Retooled.Launcher.Common.Converters;

/// <summary>
/// Platform Json converter, reads/writes unknown platforms as an empty string
/// </summary>
internal sealed class PlatformJsonConverter : JsonConverter<Platform>
{
    /// <inheritdoc />
    public override Platform Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.Null) return Platform.UNKNOWN;
        if (reader.TokenType is not JsonTokenType.String) throw new JsonException("String expected");

        string? value = reader.GetString();
        if (string.IsNullOrEmpty(value)) return Platform.UNKNOWN;

        return FastEnum.TryParse(value, true, out Platform platform) ? platform : Platform.UNKNOWN;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Platform value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.IsDefined() && value is not Platform.UNKNOWN ? value.Name : string.Empty);
    }
}
