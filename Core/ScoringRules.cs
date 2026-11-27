namespace UmbrellaRanked.Core;

public static class ScoringRules
{
    /// <summary>
    /// With <c>BotKillPoints</c> off, a kill or assist on a bot still counts in the
    /// statistics but is worth no points.
    /// </summary>
    public static int ApplyBotRule(int points, bool victimIsBot, bool botKillPoints)
    {
        return victimIsBot && !botKillPoints ? 0 : points;
    }
}
