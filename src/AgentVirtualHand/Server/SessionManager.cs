using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace AgentVirtualHand.Server;

/// <summary>Dostęp jednej sparowanej maszyny: własny token i własne okno czasowe.</summary>
public sealed record RemoteSession(
    string Token,
    string PeerKey,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string ClientName)
{
    public DateTimeOffset LastSeen { get; set; } = IssuedAt;
    public int RequestCount { get; set; }
}

/// <summary>
/// Kto ma teraz dostęp do tej maszyny. Każdy sparowany klient dostaje osobną sesję,
/// osobny token i osobne okno czasowe - odcięcie jednego nie rusza pozostałych.
/// Stan żyje wyłącznie w pamięci, więc restart aplikacji odcina wszystkich.
/// </summary>
public sealed class SessionManager
{
    private readonly object _lock = new();
    private readonly Dictionary<string, RemoteSession> _byPeer = [];

    /// <summary>Długość okna dostępu ustawiana suwakiem w oknie.</summary>
    public TimeSpan SessionDuration { get; set; } = TimeSpan.FromHours(1);

    public event Action? Changed;
    public event Action<string, string>? Audit;

    public IReadOnlyList<RemoteSession> Sessions
    {
        get { lock (_lock) { Sweep(); return _byPeer.Values.ToList(); } }
    }

    public bool HasAnySession
    {
        get { lock (_lock) { Sweep(); return _byPeer.Count > 0; } }
    }

    public RemoteSession? ForPeer(string peerKey)
    {
        lock (_lock)
        {
            Sweep();
            return _byPeer.GetValueOrDefault(peerKey);
        }
    }

    public TimeSpan? RemainingFor(string peerKey)
    {
        var session = ForPeer(peerKey);
        if (session is null) return null;

        var left = session.ExpiresAt - DateTimeOffset.Now;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>
    /// Otwiera okno dostępu dla sparowanej maszyny. Sparowanie potwierdza tożsamość,
    /// ale wpuszczenie jej jest osobną decyzją operatora i tak samo wygasa.
    /// </summary>
    public RemoteSession Open(string peerKey, string clientName)
    {
        RemoteSession created;

        lock (_lock)
        {
            Sweep();
            if (_byPeer.TryGetValue(peerKey, out var existing)) return existing;

            var now = DateTimeOffset.Now;
            created = new RemoteSession(
                Token: GenerateToken(),
                PeerKey: peerKey,
                IssuedAt: now,
                ExpiresAt: now + SessionDuration,
                ClientName: string.IsNullOrWhiteSpace(clientName) ? "nieznany klient" : clientName);

            _byPeer[peerKey] = created;
        }

        Audit?.Invoke("pair", $"OTWARTO dostęp dla {created.ClientName}, do {created.ExpiresAt:HH:mm:ss}");
        Changed?.Invoke();
        return created;
    }

    /// <summary>Sprawdza token z nagłówka Authorization. Jeden token = jedna maszyna.</summary>
    public RemoteSession? Validate(string? token)
    {
        lock (_lock)
        {
            Sweep();

            foreach (var session in _byPeer.Values)
            {
                if (!FixedTimeEquals(token, session.Token)) continue;

                session.LastSeen = DateTimeOffset.Now;
                session.RequestCount++;
                return session;
            }

            return null;
        }
    }

    public void Revoke(string peerKey, string reason)
    {
        RemoteSession? removed;
        lock (_lock)
        {
            _byPeer.Remove(peerKey, out removed);
        }

        if (removed is null) return;

        Audit?.Invoke("revoke", $"Dostęp odcięty dla {removed.ClientName}: {reason}");
        Changed?.Invoke();
    }

    public void RevokeAll(string reason)
    {
        int count;
        lock (_lock)
        {
            count = _byPeer.Count;
            _byPeer.Clear();
        }

        if (count == 0) return;

        Audit?.Invoke("revoke", $"Dostęp odcięty dla wszystkich maszyn ({count}): {reason}");
        Changed?.Invoke();
    }

    public void Extend(string peerKey, TimeSpan extra)
    {
        RemoteSession? updated = null;

        lock (_lock)
        {
            Sweep();
            if (!_byPeer.TryGetValue(peerKey, out var session)) return;

            updated = session with { ExpiresAt = session.ExpiresAt + extra };
            updated.LastSeen = session.LastSeen;
            updated.RequestCount = session.RequestCount;
            _byPeer[peerKey] = updated;
        }

        Audit?.Invoke("session", $"Dostęp dla {updated.ClientName} przedłużony do {updated.ExpiresAt:HH:mm:ss}");
        Changed?.Invoke();
    }

    /// <summary>Kasuje wygasłe sesje. Wołane pod lockiem.</summary>
    private void Sweep()
    {
        var now = DateTimeOffset.Now;
        List<RemoteSession>? expired = null;

        foreach (var session in _byPeer.Values)
        {
            if (now <= session.ExpiresAt) continue;
            (expired ??= []).Add(session);
        }

        if (expired is null) return;

        foreach (var session in expired)
        {
            _byPeer.Remove(session.PeerKey);
            Audit?.Invoke("session", $"Okno dostępu dla {session.ClientName} wygasło");
        }
    }

    private static string GenerateToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>Porownanie odporne na atak czasowy, niezależne od długości wejścia.</summary>
    private static bool FixedTimeEquals(string? candidate, string expected)
    {
        if (candidate is null) return false;

        var a = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
