using System.Text.Json.Serialization;

namespace UmbrellaRanked.Config;

[JsonConverter(typeof(JsonStringEnumConverter<DatabaseMode>))]
public enum DatabaseMode
{
    MySql,
    Sqlite
}
