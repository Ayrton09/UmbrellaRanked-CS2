using System.Text.Json.Serialization;

namespace UmbrellaRanked.Config;

[JsonConverter(typeof(JsonStringEnumConverter<SoundPlaybackMode>))]
public enum SoundPlaybackMode
{
    Disabled,
    ClientCommand,
    SoundEvent
}
