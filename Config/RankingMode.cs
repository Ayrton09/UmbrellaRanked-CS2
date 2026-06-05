using System.Text.Json.Serialization;

namespace UmbrellaRanked.Config;

[JsonConverter(typeof(JsonStringEnumConverter<RankingMode>))]
public enum RankingMode
{
    Points,
    Kda
}
