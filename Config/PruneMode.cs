using System.Text.Json.Serialization;

namespace UmbrellaRanked.Config;

[JsonConverter(typeof(PruneModeJsonConverter))]
public enum PruneMode
{
    Delete,
    Hide
}

/// <summary>
/// An unknown value becomes an undefined enum value, and OnConfigParsed warns and
/// falls back to Delete.
/// </summary>
internal sealed class PruneModeJsonConverter : TolerantEnumJsonConverter<PruneMode>
{
}
