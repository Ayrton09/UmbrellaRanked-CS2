using System.Text.Json.Serialization;

namespace UmbrellaRanked.Config;

[JsonConverter(typeof(RankingModeJsonConverter))]
public enum RankingMode
{
    Points,
    Kda
}

/// <summary>
/// An unknown value becomes an undefined enum value, and OnConfigParsed warns and
/// falls back to Points.
/// </summary>
internal sealed class RankingModeJsonConverter : TolerantEnumJsonConverter<RankingMode>
{
}
