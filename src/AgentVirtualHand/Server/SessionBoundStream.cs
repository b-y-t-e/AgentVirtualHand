namespace AgentVirtualHand.Server;

/// <summary>Dostep maszyny zostal odciety albo wygasl w trakcie operacji.</summary>
public sealed class AccessClosedException() : IOException(DefaultMessage)
{
    public const string DefaultMessage = "Access closed - the machine owner must grant it in the AVH window.";
}

/// <summary>
/// Strumien pliku zwiazany z sesja: odczyt i zapis najpierw sprawdzaja, czy dostep nadal trwa.
/// Transfer plikow omija filtr sesji na loopbacku, wiec bez tego odciety upload albo download szedlby do konca.
/// Przewijanie przechodzi do srodka, zeby transfer nadal wznawial sie po zerwaniu relayu.
/// Sesje sprawdzamy najwyzej raz na <see cref="CheckInterval"/>, nie przy kazdym kawalku danych.
/// </summary>
public sealed class SessionBoundStream(Stream inner, Func<bool> isSessionOpen, TimeProvider clock) : Stream
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

    private long? _lastCheck;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        EnsureSessionOpen();
        return inner.Read(buffer, offset, count);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureSessionOpen();
        return inner.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureSessionOpen();
        inner.Write(buffer, offset, count);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureSessionOpen();
        return inner.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void EnsureSessionOpen()
    {
        var now = clock.GetTimestamp();
        if (_lastCheck is { } last && clock.GetElapsedTime(last, now) < CheckInterval) return;

        if (!isSessionOpen()) throw new AccessClosedException();
        _lastCheck = now;
    }
}
