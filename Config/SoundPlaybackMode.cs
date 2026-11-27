using System.Text.Json.Serialization;

namespace UmbrellaRanked.Config;

[JsonConverter(typeof(SoundPlaybackModeJsonConverter))]
public enum SoundPlaybackMode
{
    Disabled,
    ClientCommand,
    SoundEvent
}

/// <summary>
/// An unknown value becomes an undefined enum value, and OnConfigParsed warns and
/// falls back to ClientCommand.
/// </summary>
internal sealed class SoundPlaybackModeJsonConverter : TolerantEnumJsonConverter<SoundPlaybackMode>
{
}
