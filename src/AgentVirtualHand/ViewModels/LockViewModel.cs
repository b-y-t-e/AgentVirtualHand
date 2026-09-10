using System.ComponentModel;
using System.Runtime.CompilerServices;
using AgentVirtualHand.Services;
using Avalonia.Media;
using Avalonia.Threading;

namespace AgentVirtualHand.ViewModels;

/// <summary>
/// Blokada okna: pierwsze uruchomienie wymusza ustawienie hasła, a po 30 sekundach
/// bez ruchu myszy i klawiatury treść okna znika i wraca dopiero po podaniu hasła.
/// Nieudana próba kosztuje 10 sekund - tyle, żeby zgadywanie po omacku nie miało sensu.
/// </summary>
public sealed class LockViewModel : INotifyPropertyChanged
{
    public static TimeSpan IdleTimeout { get; } = TimeSpan.FromSeconds(30);
    public static TimeSpan PenaltyAfterFailure { get; } = TimeSpan.FromSeconds(10);

    /// <summary>Na ile sekund przed blokadą pokazać ciche ostrzeżenie o zbliżającym się zamknięciu.</summary>
    public static int LockWarningSeconds { get; } = 10;

    private readonly PasswordGate _gate;
    private readonly TimeProvider _clock;
    private readonly DispatcherTimer? _timer;

    private DateTimeOffset _lastActivity;
    private DateTimeOffset? _penaltyUntil;

    private bool _isLocked;
    private string _password = "";
    private string _confirmation = "";
    private string _message = "";
    private int _penaltySeconds;
    private int _secondsUntilLock;

    /// <param name="clock">Wstrzykiwany zegar - dzięki niemu odliczanie da się przetestować bez czekania.</param>
    /// <param name="runTimer">Fałsz w testach: wtedy <see cref="Tick"/> woła się ręcznie.</param>
    public LockViewModel(string storageDirectory, TimeProvider? clock = null, bool runTimer = true)
    {
        _gate = new PasswordGate(storageDirectory);
        _clock = clock ?? TimeProvider.System;
        _lastActivity = _clock.GetUtcNow();

        NeedsSetup = !_gate.IsConfigured;
        _isLocked = true;
        _message = NeedsSetup
            ? "Set a password to unlock this window."
            : "Window locked. Enter the password.";

        if (!runTimer) return;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>Pierwsze uruchomienie - zamiast odblokowania prosimy o ustawienie hasła.</summary>
    public bool NeedsSetup { get; private set; }

    public bool IsLocked
    {
        get => _isLocked;
        private set
        {
            if (!Set(ref _isLocked, value)) return;
            OnPropertyChanged(nameof(IsUnlocked));
        }
    }

    public bool IsUnlocked => !IsLocked;

    public string Password
    {
        get => _password;
        set => Set(ref _password, value);
    }

    public string Confirmation
    {
        get => _confirmation;
        set => Set(ref _confirmation, value);
    }

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public string ActionText => NeedsSetup ? "Set password" : "Unlock";

    /// <summary>Ile sekund kary zostało po nieudanej próbie; 0 gdy można próbować.</summary>
    public int PenaltySeconds
    {
        get => _penaltySeconds;
        private set
        {
            if (!Set(ref _penaltySeconds, value)) return;
            OnPropertyChanged(nameof(CanSubmit));
            OnPropertyChanged(nameof(HasPenalty));
        }
    }

    public bool HasPenalty => PenaltySeconds > 0;
    public bool CanSubmit => PenaltySeconds == 0;

    /// <summary>Sekundy do automatycznej blokady - liczone tylko w ostatnich chwilach bezczynności.</summary>
    public int SecondsUntilLock
    {
        get => _secondsUntilLock;
        private set
        {
            if (!Set(ref _secondsUntilLock, value)) return;
            OnPropertyChanged(nameof(ShowLockCountdown));
            OnPropertyChanged(nameof(LockCountdownText));
            OnPropertyChanged(nameof(LockCountdownAccent));
        }
    }

    /// <summary>Licznik widoczny przez caly czas, gdy okno jest odblokowane.</summary>
    public bool ShowLockCountdown => !IsLocked;

    public string LockCountdownText
    {
        get
        {
            var s = SecondsUntilLock;
            return s >= 60 ? $"lock in {s / 60}:{s % 60:00}" : $"lock in {s} s";
        }
    }

    /// <summary>Wyciszony przez wiekszosc czasu, ostrzegawczy w ostatnich sekundach.</summary>
    public IBrush LockCountdownAccent =>
        Brush.Parse(SecondsUntilLock <= LockWarningSeconds ? "#FFB454" : "#5C6474");

    /// <summary>Ruch myszą albo klawisz - liczy się jako obecność przy komputerze.</summary>
    public void NoteActivity() => _lastActivity = _clock.GetUtcNow();

    public void Lock()
    {
        if (IsLocked) return;

        Password = "";
        Confirmation = "";
        Message = "Window locked after 30 seconds idle. Enter the password.";
        IsLocked = true;
        SecondsUntilLock = 0;
    }

    /// <summary>Wspólny przycisk: przy pierwszym uruchomieniu ustawia hasło, później odblokowuje.</summary>
    public void Submit()
    {
        if (!CanSubmit) return;

        if (NeedsSetup)
        {
            SetPassword();
            return;
        }

        if (_gate.Verify(Password))
        {
            Password = "";
            Message = "";
            NoteActivity();
            IsLocked = false;
            SecondsUntilLock = (int)IdleTimeout.TotalSeconds;
            return;
        }

        Password = "";
        _penaltyUntil = _clock.GetUtcNow() + PenaltyAfterFailure;
        PenaltySeconds = (int)PenaltyAfterFailure.TotalSeconds;
        Message = "Wrong password.";
    }

    private void SetPassword()
    {
        if (Password != Confirmation)
        {
            Message = "Passwords do not match.";
            return;
        }

        try
        {
            _gate.Set(Password);
        }
        catch (ArgumentException ex)
        {
            Message = ex.Message;
            return;
        }

        Password = "";
        Confirmation = "";
        Message = "";
        NeedsSetup = false;
        OnPropertyChanged(nameof(NeedsSetup));
        OnPropertyChanged(nameof(ActionText));

        NoteActivity();
        IsLocked = false;
        SecondsUntilLock = (int)IdleTimeout.TotalSeconds;
    }

    /// <summary>Krok zegara: odlicza karę i pilnuje bezczynności. Woła go zegar okna co sekundę.</summary>
    public void Tick()
    {
        if (_penaltyUntil is { } until)
        {
            var left = (int)Math.Ceiling((until - _clock.GetUtcNow()).TotalSeconds);
            if (left <= 0)
            {
                _penaltyUntil = null;
                PenaltySeconds = 0;
                Message = "You can try again.";
            }
            else
            {
                PenaltySeconds = left;
                Message = $"Wrong password. Next try in {left} s.";
            }
        }

        if (IsLocked)
        {
            SecondsUntilLock = 0;
            return;
        }

        var idle = _clock.GetUtcNow() - _lastActivity;
        if (idle >= IdleTimeout)
        {
            Lock();
            return;
        }

        var untilLock = (int)Math.Ceiling((IdleTimeout - idle).TotalSeconds);
        SecondsUntilLock = Math.Max(untilLock, 0);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
