using System.Text.Json.Serialization;

namespace UmbrellaRanked.Config;

/// <summary>
/// TLS policy of the MySQL connection. Mirrors the MySqlConnector modes; Preferred
/// is what earlier versions always used.
/// </summary>
[JsonConverter(typeof(DatabaseSslModeJsonConverter))]
public enum DatabaseSslMode
{
    Preferred,
    None,
    Required,
    VerifyCA,
    VerifyFull
}

/// <summary>
/// An unknown value becomes an undefined enum value, and OnConfigParsed warns and
/// falls back to Preferred.
/// </summary>
internal sealed class DatabaseSslModeJsonConverter : TolerantEnumJsonConverter<DatabaseSslMode>
{
}
