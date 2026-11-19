using System.ComponentModel;
using System.Runtime.CompilerServices;
using AgentVirtualHand.Services;

namespace AgentVirtualHand.ViewModels;

/// <summary>
/// Przycisk "Restart to update" - wspólny dla huba i assist (plik linkowany do avh-assist).
/// Okno podaje tylko, jak zamknąć swoje połączenia; kolejność (czekanie na pakiet, zamknięcie,
/// instalacja) i teksty są w jednym miejscu.
/// </summary>
public sealed class UpdatePrompt : INotifyPropertyChanged
{
    private readonly IAppUpdates _updates;
    private bool _restarting;

    public UpdatePrompt(IAppUpdates updates)
    {
        _updates = updates;
        _updates.UpdateReady += () =>
        {
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(Text));
            if (_updates.ReadyVersion is { } version) Ready?.Invoke(version);
        };
    }

    /// <summary>Np. "v0.1.4" - do paska okna.</summary>
    public string VersionText => $"v{_updates.CurrentVersion}";

    public bool HasUpdate => _updates.ReadyVersion is not null;

    public bool CanRestart => !_restarting;

    public string Text => _restarting ? "Installing update..." : $"Restart to update to {_updates.ReadyVersion}";

    /// <summary>Nowa wersja pobrana (na wątku UI) - okno może to zapisać w logu.</summary>
    public event Action<string>? Ready;

    /// <summary>Instalacja się nie udała; połączenia mogły już zostać zamknięte.</summary>
    public event Action<string>? Failed;

    /// <summary>
    /// Zamyka połączenia okna (na wątku UI, dopiero gdy pakiet jest gotowy), instaluje i uruchamia
    /// nową wersję. Okno w tym czasie odpowiada; przycisk jest wyłączony.
    /// </summary>
    public async Task RestartAsync(Action shutdown)
    {
        if (_restarting || !HasUpdate) return;

        SetRestarting(true);
        try
        {
            await _updates.ApplyAndRestartAsync(shutdown);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex.Message);
        }
        finally
        {
            // Po udanej instalacji proces już nie żyje; tu dochodzimy tylko, gdy coś poszło nie tak.
            SetRestarting(false);
        }
    }

    private void SetRestarting(bool value)
    {
        _restarting = value;
        OnPropertyChanged(nameof(CanRestart));
        OnPropertyChanged(nameof(Text));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
