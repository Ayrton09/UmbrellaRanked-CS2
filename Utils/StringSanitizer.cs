namespace UmbrellaRanked.Utils;

public static class StringSanitizer
{
    private const int MaxPlayerNameLength = 64;

    public static string SanitizePlayerName(string? name)
    {
        var raw = name ?? string.Empty;
        var builder = new System.Text.StringBuilder(raw.Length);

        foreach (var character in raw)
        {
            // Drop control characters (including color/format bytes and \n\r\t),
            // replacing them with a space so words don't get glued together.
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        var sanitized = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "Unknown";
        }

        // The name columns are VARCHAR(64); a longer name would otherwise fail
        // the whole save under MySQL strict mode.
        return sanitized.Length > MaxPlayerNameLength
            ? sanitized[..MaxPlayerNameLength].Trim()
            : sanitized;
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

    public static string SanitizeSoundClientCommandArgument(string? sample)
    {
        var value = sample ?? string.Empty;
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (var character in value)
        {
            // Drop characters that could close the quoted playvol argument (")
            // or chain extra console commands (;), plus any control bytes. The
            // value is admin-controlled today, but this keeps it safe if the
            // sound ever comes from an untrusted source.
            if (character is '"' or ';' || char.IsControl(character))
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
