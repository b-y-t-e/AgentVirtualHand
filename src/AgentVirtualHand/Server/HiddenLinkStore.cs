using Tailcat.Link.Storage;

namespace AgentVirtualHand.Server;

/// <summary>
/// Trzyma stan parowania Tailcata pod nieczytelną nazwą pliku (avh.link.json) zamiast
/// domyślnej, która zawierała pełną nazwę produktu. Tożsamość parowania w sieci zostaje
/// bez zmian - podmieniamy tylko nazwę pliku na dysku, żeby nie zdradzała, do czego służy
/// narzędzie.
/// </summary>
public sealed class HiddenLinkStore : ILinkStore
{
    /// <summary>Neutralny klucz decydujący tylko o nazwie pliku - nie o tożsamości parowania.</summary>
    private const string FileKey = "avh";

    private readonly FileLinkStore _inner = new(
        FileLinkStore.DefaultRoot(), SecretProtector.ForCurrentPlatform());

    // Klucz pliku jest staly niezaleznie od nazwy aplikacji - ta zostaje tozsamoscia parowania.
    public Task<LinkState> LoadAsync(string appName, CancellationToken cancellationToken = default)
        => _inner.LoadAsync(FileKey, cancellationToken);

    public Task SaveAsync(string appName, LinkState state, CancellationToken cancellationToken = default)
        => _inner.SaveAsync(FileKey, state, cancellationToken);

    public Task DeleteAsync(string appName, CancellationToken cancellationToken = default)
        => _inner.DeleteAsync(FileKey, cancellationToken);
}
