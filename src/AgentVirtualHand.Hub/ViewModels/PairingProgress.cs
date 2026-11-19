using System.ComponentModel;

namespace AgentVirtualHand.Hub.ViewModels;

/// <summary>Stan trwającego parowania: etap, upływający czas na tle limitu i możliwość przerwania.</summary>
/// <param name="time">Zegar i ticker - w checku podmieniany na sztuczny.</param>
/// <param name="timeout">Limit parowania pokazywany jako "X s of limit".</param>
/// <param name="firstPhase">Etap pokazywany od razu, zanim dotrze pierwszy raport.</param>
/// <param name="post">Przenosi tik timera na wątek okna.</param>
public sealed class PairingProgress(TimeProvider time, TimeSpan timeout, string firstPhase, Action<Action> post)
    : INotifyPropertyChanged
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private CancellationTokenSource? _cts;
    private ITimer? _ticker;
    private DateTimeOffset _started;
    private string _phase = "";
    private string _name = "";

    public bool IsActive => _cts is not null;
    public bool IsIdle => _cts is null;

    public string Title => $"Pairing with \"{_name}\"";

    public string Text
    {
        get
        {
            var elapsed = (int)(time.GetUtcNow() - _started).TotalSeconds;
            var limit = (int)timeout.TotalSeconds;
            return $"{_phase}...   {elapsed} s of {limit}";
        }
    }

    public void Start(string name, CancellationTokenSource cts)
    {
        _cts = cts;
        _name = name;
        _started = time.GetUtcNow();
        _phase = firstPhase;
        _ticker?.Dispose();
        _ticker = time.CreateTimer(_ => post(() => Notify(nameof(Text))), null, TickInterval, TickInterval);
        NotifyAll();
    }

    public void Stop()
    {
        _cts = null;
        _ticker?.Dispose();
        _ticker = null;
        NotifyAll();
    }

    public void Cancel() => _cts?.Cancel();

    public void ReportPhase(string phase)
    {
        _phase = phase;
        Notify(nameof(Text));
    }

    private void NotifyAll()
    {
        Notify(nameof(IsActive));
        Notify(nameof(IsIdle));
        Notify(nameof(Title));
        Notify(nameof(Text));
    }

    private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public event PropertyChangedEventHandler? PropertyChanged;
}
