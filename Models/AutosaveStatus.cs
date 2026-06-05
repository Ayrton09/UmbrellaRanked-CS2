namespace UmbrellaRanked.Models;

public sealed record AutosaveStatus(
    bool IsRunning,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc,
    string LastError);
