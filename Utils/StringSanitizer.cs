namespace UmbrellaRanked.Utils;

public static class StringSanitizer
{
    public static string SanitizePlayerName(string? name)
    {
        var sanitized = (name ?? string.Empty)
            .Trim()
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Replace('\t', ' ');

        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown" : sanitized;
    }

    public static string NormalizeOptionalSoundResource(string? resourcePath)
    {
        var value = (resourcePath ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.StartsWith("sound/", StringComparison.OrdinalIgnoreCase)
            ? value
            : $"sound/{value}";
    }

    public static string NormalizeSoundSample(string? sample)
    {
        var value = (sample ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.StartsWith("sound/", StringComparison.OrdinalIgnoreCase)
            ? value["sound/".Length..]
            : value;
    }
}
