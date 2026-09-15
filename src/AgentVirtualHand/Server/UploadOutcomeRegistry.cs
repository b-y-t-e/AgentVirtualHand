using System.Collections.Concurrent;

namespace AgentVirtualHand.Server;

internal sealed record UploadRecord(string Path, long Size);

/// <summary>Wynik uploadu, na ktory moze czekac "upload-finish" - zadanie i transfer przychodza w dowolnej kolejnosci.</summary>
internal sealed class UploadOutcome
{
    private readonly TaskCompletionSource<UploadRecord> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<UploadRecord> Result => _result.Task;

    /// <summary>Kiedy wynik sie rozstrzygnal - od tej chwili, nie od startu dlugiego transferu, liczy sie jego wiek.</summary>
    public DateTimeOffset? SettledAt { get; private set; }

    public void Succeed(UploadRecord record)
    {
        SettledAt = DateTimeOffset.UtcNow;
        _result.TrySetResult(record);
    }

    public void Fail(Exception error)
    {
        SettledAt = DateTimeOffset.UtcNow;
        _result.TrySetException(error);
    }

    public bool IsUnclaimedSince(DateTimeOffset cutoff) => SettledAt is { } settled && settled < cutoff;
}

/// <summary>Wyniki uploadow strumieniowych po korelacyjnym id, dopoki hub ich nie odbierze.</summary>
internal sealed class UploadOutcomeRegistry
{
    private static readonly TimeSpan UnclaimedLifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, UploadOutcome> _outcomes = new();

    public UploadOutcome For(string id)
    {
        PruneUnclaimed();
        return _outcomes.GetOrAdd(id, _ => new UploadOutcome());
    }

    public void Forget(string id) => _outcomes.TryRemove(id, out _);

    private void PruneUnclaimed()
    {
        var cutoff = DateTimeOffset.UtcNow - UnclaimedLifetime;
        // Tylko rozstrzygniete wyniki, ktorych nikt nie odebral - trwajacy dlugi upload zostaje.
        foreach (var (id, outcome) in _outcomes)
            if (outcome.IsUnclaimedSince(cutoff)) Forget(id);
    }
}
