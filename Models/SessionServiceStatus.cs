namespace UmbrellaRanked.Models;

public sealed record SessionServiceStatus(
    int ConnectedPlayers,
    int TotalSessions,
    int LoadedSessions,
    int LoadingSessions,
    int PendingSaveSessions);
