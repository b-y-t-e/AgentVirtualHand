using System.ComponentModel;
using System.Runtime.CompilerServices;
using AgentVirtualHand.Services;
using Avalonia.Media;
using Avalonia.Threading;

namespace AgentVirtualHand.ViewModels;

/// <summary>
/// Window lock. When on, first launch forces setting a password and the window locks
/// after 30 seconds of no mouse/keyboard, returning only after the password. A wrong
/// attempt costs 10 seconds. The lock can be turned off entirely (persisted), in which
/// case the window opens straight to its content and never locks.
/// </summary>
public sealed class LockViewModel : INotifyPropertyChanged
{
    public static TimeSpan IdleTimeout { get; } = TimeSpan.FromSeconds(30);
    public static TimeSpan PenaltyAfterFailure { get; } = TimeSpan.FromSeconds(10);

    /// <summary>How many seconds before the lock to show the quiet countdown warning.</summary>
    public static int LockWarningSeconds { get; } = 10;

    private readonly PasswordGate _gate;
    private readonly TimeProvider _clock;
    private readonly DispatcherTimer? _timer;
    private readonly string _disabledMarker;

    private DateTimeOffset _lastActivity;
    private DateTimeOffset? _penaltyUntil;

    private bool _autoLock;
    private bool _isLocked;
    private string _password = "";
    private string _confirmation = "";
    private string _message = "";
    private int _penaltySeconds;
    private int _secondsUntilLock;

    /// <param name="clock">Injected clock so the countdown can be tested without waiting.</param>
    /// <param name="runTimer">False in tests: then <see cref="Tick"/> is called by hand.</param>
    public LockViewModel(string storageDirectory, TimeProvider? clock = null, bool runTimer = true)
    {
        _gate = new PasswordGate(storageDirectory);
        _clock = clock ?? TimeProvider.System;
        _lastActivity = _clock.GetUtcNow();
        _disabledMarker = Path.Combine(storageDirectory, "autolock.off");
        _autoLock = !MarkerExists();

        if (!_autoLock)
        {
            // Lock turned off: open straight to content, never lock.
            _isLocked = false;
            NeedsSetup = false;
        }
        else
        {
            NeedsSetup = !_gate.IsConfigured;
            _isLocked = true;
            _message = NeedsSetup ? "Set a password to unlock this window." : "Window locked. Enter the password.";
        }

        _secondsUntilLock = (int)IdleTimeout.TotalSeconds;

        if (!runTimer) return;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>First launch - ask to set a password instead of unlocking.</summary>
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

    /// <summary>Whether the window locks itself on idle. Off = never locks, no password prompt.</summary>
    public bool AutoLockEnabled
    {
        get => _autoLock;
        private set
        {
            if (!Set(ref _autoLock, value)) return;
            OnPropertyChanged(nameof(AutoLockText));
            OnPropertyChanged(nameof(AutoLockAccent));
            OnPropertyChanged(nameof(ShowLockCountdown));
        }
    }

    public string AutoLockText => AutoLockEnabled ? "auto-lock on" : "auto-lock off";

    public IBrush AutoLockAccent => Brush.Parse(AutoLockEnabled ? "#63D19B" : "#5C6474");

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

    /// <summary>Seconds of penalty left after a wrong attempt; 0 when a new try is allowed.</summary>
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

    /// <summary>Countdown shown whenever the window is unlocked and auto-lock is on.</summary>
    public bool ShowLockCountdown => AutoLockEnabled && !IsLocked;

    public string LockCountdownText
    {
        get
        {
            var s = SecondsUntilLock;
            return s >= 60 ? $"lock in {s / 60}:{s % 60:00}" : $"lock in {s} s";
        }
    }

    /// <summary>Muted most of the time, warning in the last seconds.</summary>
    public IBrush LockCountdownAccent =>
        Brush.Parse(SecondsUntilLock <= LockWarningSeconds ? "#FFB454" : "#5C6474");

    /// <summary>Mouse move or key press - counts as presence at the computer.</summary>
    public void NoteActivity() => _lastActivity = _clock.GetUtcNow();

    /// <summary>Turns auto-lock on or off and remembers the choice.</summary>
    public void ToggleAutoLock()
    {
        if (AutoLockEnabled)
        {
            WriteMarker(true);       // disabled
            AutoLockEnabled = false;
            IsLocked = false;
            Message = "";
            return;
        }

        WriteMarker(false);          // enabled
        AutoLockEnabled = true;
        NoteActivity();

        // Enabling the lock requires a password: if none is set, ask for one now.
        if (!_gate.IsConfigured)
        {
            NeedsSetup = true;
            OnPropertyChanged(nameof(NeedsSetup));
            OnPropertyChanged(nameof(ActionText));
            Lock();
        }
    }

    public void Lock()
    {
        if (IsLocked) return;

        Password = "";
        Confirmation = "";
        Message = NeedsSetup ? "Set a password to unlock this window." : "Window locked. Enter the password.";
        IsLocked = true;
        SecondsUntilLock = 0;
    }

    /// <summary>Shared button: sets the password on first run, unlocks afterwards.</summary>
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

    /// <summary>Clock step: counts down the penalty and watches idle. Called every second.</summary>
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

        if (!AutoLockEnabled || IsLocked)
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

    private bool MarkerExists()
    {
        try { return File.Exists(_disabledMarker); }
        catch { return false; }
    }

    private void WriteMarker(bool disabled)
    {
        try
        {
            if (disabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_disabledMarker)!);
                File.WriteAllText(_disabledMarker, "auto-lock disabled by the user");
            }
            else if (File.Exists(_disabledMarker))
            {
                File.Delete(_disabledMarker);
            }
        }
        catch
        {
            // Failing to persist the choice must not break the app; it just won't stick.
        }
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
