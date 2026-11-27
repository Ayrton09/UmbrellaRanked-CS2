using System.Text.Json;
using System.Text.Json.Serialization;

namespace UmbrellaRanked.Config;

/// <summary>
/// CounterStrikeSharp does not catch config parse errors: an unknown enum string would
/// stop the plugin from loading. An unknown value becomes an undefined enum value
/// (<c>-1</c>), and OnConfigParsed warns and falls back to the documented default.
/// Names are matched case-insensitively; lists such as "Delete, Hide" are rejected.
/// </summary>
internal class TolerantEnumJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly TEnum Invalid = (TEnum)Enum.ToObject(typeof(TEnum), -1);

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var text = reader.GetString();

                // Enum.TryParse accepts "A, B" as a combination of flags.
                if (text != null &&
                    !text.Contains(',') &&
                    Enum.TryParse<TEnum>(text.Trim(), true, out var parsed) &&
                    Enum.IsDefined(parsed))
                {
                    return parsed;
                }

                return Invalid;

            case JsonTokenType.Number:
                return reader.TryGetInt32(out var number)
                    ? (TEnum)Enum.ToObject(typeof(TEnum), number)
                    : Invalid;

            default:
                reader.Skip();
                return Invalid;
        }
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(Enum.IsDefined(value) ? value.ToString() : default(TEnum).ToString());
    }
}
