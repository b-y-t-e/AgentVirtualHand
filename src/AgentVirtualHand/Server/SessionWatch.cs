namespace AgentVirtualHand.Server;

/// <summary>
/// Token anulowania dla dlugiego transferu, ktory pada, gdy sesja, ktora go zaczela, przestaje obowiazywac
/// (odciecie przez wlasciciela, wygasniecie okna). Transfer omija filtr sesji na loopbacku, wiec bez tego
/// plik szedlby do konca mimo odebranego dostepu.
/// </summary>
public sealed class SessionWatch : IDisposable
{
    private readonly object _lock = new();
    private readonly Func<bool> _isSessionValid;
    private readonly CancellationTokenSource _cancellation;
    private readonly ITimer _timer;
    private bool _disposed;

    public SessionWatch(Func<bool> isSessionValid, TimeSpan checkInterval, TimeProvider clock, CancellationToken outer)
    {
        _isSessionValid = isSessionValid;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _timer = clock.CreateTimer(_ => CancelIfSessionEnded(), null, checkInterval, checkInterval);
    }

    public CancellationToken Token => _cancellation.Token;

    /// <summary>Czy transfer zostal przerwany dlatego, ze sesja wygasla lub zostala odcieta.</summary>
    public bool SessionEnded { get; private set; }

    private void CancelIfSessionEnded()
    {
        if (_isSessionValid()) return;

        lock (_lock)
        {
            if (_disposed) return;
            SessionEnded = true;
            _cancellation.Cancel();
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _cancellation.Dispose();
        }
    }
}
