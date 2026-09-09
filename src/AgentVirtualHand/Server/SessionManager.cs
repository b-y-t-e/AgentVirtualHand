using System.Security.Cryptography;
using System.Text;

namespace AgentVirtualHand.Server;

public enum AccessState
{
    /// <summary>Nikt nie jest sparowany, parowanie zamknięte.</summary>
    Locked,

    /// <summary>Okno parowania otwarte - czekamy na klienta z kodem.</summary>
    Pairing,

    /// <summary>Klient sparowany, sesja aktywna.</summary>
    Active
}

public sealed record RemoteSession(
    string Token,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string ClientAddress,
    string ClientName)
{
    public DateTimeOffset LastSeen { get; set; } = IssuedAt;
    public int RequestCount { get; set; }
}

/// <summary>
/// Cala logika bezpieczeństwa: jednorazowy kod parowania, token sesji, czas życia.
/// Stan trzymany wyłącznie w pamięci - restart aplikacji odcina dostęp.
/// </summary>
public sealed class SessionManager
{
    private static readonly TimeSpan PairingWindow = TimeSpan.FromMinutes(5);
    private const int MaxFailedAttempts = 5;

    private readonly object _lock = new();

    private string? _pairCode;
    private DateTimeOffset _pairCodeExpiresAt;
    private int _failedAttempts;
    private RemoteSession? _session;

    /// <summary>Czas trwania sesji ustawiany z GUI.</summary>
    public TimeSpan SessionDuration { get; set; } = TimeSpan.FromHours(1);

    public event Action? Changed;
    public event Action<string, string>? Audit;

    public AccessState State
    {
        get
        {
            lock (_lock)
            {
                Sweep();
                if (_session is not null) return AccessState.Active;
                return _pairCode is not null ? AccessState.Pairing : AccessState.Locked;
            }
        }
    }

    public string? PairCode
    {
        get { lock (_lock) { Sweep(); return _pairCode; } }
    }

    public DateTimeOffset PairCodeExpiresAt
    {
        get { lock (_lock) { return _pairCodeExpiresAt; } }
    }

    public RemoteSession? Session
    {
        get { lock (_lock) { Sweep(); return _session; } }
    }

    public TimeSpan? Remaining
    {
        get
        {
            lock (_lock)
            {
                Sweep();
                if (_session is null) return null;
                var left = _session.ExpiresAt - DateTimeOffset.Now;
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
    }

    /// <summary>Otwiera okno parowania i zwraca nowy kod w formacie XXXX-XXXX.</summary>
    public string StartPairing()
    {
        string code;
        DateTimeOffset expires;
        lock (_lock)
        {
            if (_session is not null)
                throw new InvalidOperationException("Sesja jest aktywna - zakończ ją przed nowym parowaniem.");

            code = GenerateCode();
            _pairCode = code;
            _pairCodeExpiresAt = DateTimeOffset.Now + PairingWindow;
            expires = _pairCodeExpiresAt;
            _failedAttempts = 0;
        }

        Audit?.Invoke("pair", $"Otwarto parowanie, kod ważny do {expires:HH:mm:ss}");
        Changed?.Invoke();
        return code;
    }

    public void CancelPairing()
    {
        lock (_lock)
        {
            if (_pairCode is null) return;
            _pairCode = null;
        }

        Audit?.Invoke("pair", "Parowanie anulowane");
        Changed?.Invoke();
    }

    /// <summary>Próba parowania. Sukces zamyka okno parowania na stałe.</summary>
    public RemoteSession? TryPair(string? code, string clientAddress, string clientName)
    {
        RemoteSession? created = null;
        string auditMessage;

        lock (_lock)
        {
            Sweep();

            if (_session is not null)
            {
                auditMessage = $"ODRZUCONO parowanie z {clientAddress}: sesja już aktywna";
            }
            else if (_pairCode is null)
            {
                auditMessage = $"ODRZUCONO parowanie z {clientAddress}: parowanie zamknięte";
            }
            else if (!FixedTimeEquals(code, _pairCode))
            {
                _failedAttempts++;
                auditMessage = $"BŁĘDNY kod od {clientAddress} (próba {_failedAttempts}/{MaxFailedAttempts})";
                if (_failedAttempts >= MaxFailedAttempts)
                {
                    _pairCode = null;
                    auditMessage += " - parowanie zablokowane";
                }
            }
            else
            {
                var now = DateTimeOffset.Now;
                created = new RemoteSession(
                    Token: GenerateToken(),
                    IssuedAt: now,
                    ExpiresAt: now + SessionDuration,
                    ClientAddress: clientAddress,
                    ClientName: string.IsNullOrWhiteSpace(clientName) ? "nieznany klient" : clientName);

                _session = created;
                _pairCode = null; // parowanie zamykane natychmiast po sukcesie
                auditMessage = $"SPAROWANO {clientAddress} ({created.ClientName}), dostęp do {created.ExpiresAt:HH:mm:ss}";
            }
        }

        Audit?.Invoke(created is null ? "deny" : "pair", auditMessage);
        Changed?.Invoke();
        return created;
    }

    /// <summary>
    /// Otwiera okno dostępu dla maszyny sparowanej przez Tailcat.Link.
    /// Kodu jednorazowego tu nie ma - tożsamość drugiej strony potwierdza samo sparowanie linku,
    /// a to okno jest świadomą decyzją operatora i tak samo wygasa.
    /// </summary>
    public RemoteSession OpenForLink(string clientName)
    {
        RemoteSession created;

        lock (_lock)
        {
            Sweep();
            if (_session is not null) return _session;

            var now = DateTimeOffset.Now;
            created = new RemoteSession(
                Token: GenerateToken(),
                IssuedAt: now,
                ExpiresAt: now + SessionDuration,
                ClientAddress: "tailcat-link",
                ClientName: string.IsNullOrWhiteSpace(clientName) ? "nieznany klient" : clientName);

            _session = created;
            _pairCode = null;
        }

        Audit?.Invoke("pair", $"OTWARTO dostęp przez link, do {created.ExpiresAt:HH:mm:ss}");
        Changed?.Invoke();
        return created;
    }

    /// <summary>Sprawdza token z nagłówka Authorization.</summary>
    public RemoteSession? Validate(string? token)
    {
        lock (_lock)
        {
            Sweep();
            if (_session is null || !FixedTimeEquals(token, _session.Token)) return null;

            _session.LastSeen = DateTimeOffset.Now;
            _session.RequestCount++;
            return _session;
        }
    }

    public void Revoke(string reason)
    {
        bool had;
        lock (_lock)
        {
            had = _session is not null || _pairCode is not null;
            _session = null;
            _pairCode = null;
        }

        if (had)
        {
            Audit?.Invoke("revoke", $"Dostęp odcięty: {reason}");
            Changed?.Invoke();
        }
    }

    /// <summary>Przedłuża aktywną sesję o podany czas.</summary>
    public void Extend(TimeSpan extra)
    {
        RemoteSession? updated;
        lock (_lock)
        {
            Sweep();
            if (_session is null) return;
            updated = _session with { ExpiresAt = _session.ExpiresAt + extra };
            updated.LastSeen = _session.LastSeen;
            updated.RequestCount = _session.RequestCount;
            _session = updated;
        }

        Audit?.Invoke("session", $"Sesja przedłużona do {updated.ExpiresAt:HH:mm:ss}");
        Changed?.Invoke();
    }

    /// <summary>Kasuje przeterminowany kod parowania i wygasza sesję. Wołane pod lockiem.</summary>
    private void Sweep()
    {
        var now = DateTimeOffset.Now;

        if (_pairCode is not null && now > _pairCodeExpiresAt)
        {
            _pairCode = null;
            Audit?.Invoke("pair", "Kod parowania wygasł");
        }

        if (_session is not null && now > _session.ExpiresAt)
        {
            _session = null;
            Audit?.Invoke("session", "Sesja wygasła - dostęp odcięty");
        }
    }

    private static string GenerateCode()
    {
        // Alfabet bez znaków mylących się przy przepisywaniu (0/O, 1/I).
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[9];
        for (var i = 0; i < 9; i++)
            chars[i] = i == 4 ? '-' : alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
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
