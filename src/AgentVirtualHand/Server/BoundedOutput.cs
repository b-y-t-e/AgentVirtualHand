using System.Text;

namespace AgentVirtualHand.Server;

/// <summary>
/// Wyjscie jednego potoku z gornym limitem znakow. Po przekroczeniu limitu dalszy tekst jest pomijany
/// (<see cref="Truncated"/>), a po <see cref="Detach"/> bufor jest zwalniany i nic juz nie zapisuje -
/// wnuk trzymajacy potok (np. "srv &amp;") moze pisac dalej, ale nikt tego nie czyta, wiec nie gromadzimy.
/// Bezpieczne watkowo: potok pisze z innego watku niz odczyt w API.
/// </summary>
public sealed class BoundedOutput(int maxChars)
{
    private readonly StringBuilder _text = new();
    private readonly object _lock = new();
    private bool _truncated;
    private bool _detached;

    public bool IsDetached
    {
        get { lock (_lock) return _detached; }
    }

    /// <summary>Pelna linia - dopisuje separator.</summary>
    public void AppendLine(string line) => Append(line, endsLine: true);

    /// <summary>Kawalek zbyt dlugiej linii - bez separatora, zeby tekst zostal taki jak w potoku.</summary>
    public void AppendPartial(string text) => Append(text, endsLine: false);

    private void Append(string text, bool endsLine)
    {
        lock (_lock)
        {
            if (_truncated || _detached) return;

            var remaining = maxChars - _text.Length;
            var separator = endsLine ? Environment.NewLine.Length : 0;
            if (text.Length + separator <= remaining)
            {
                _text.Append(text);
                if (endsLine) _text.AppendLine();
                return;
            }

            var count = Math.Min(text.Length, remaining);
            if (count > 0 && char.IsHighSurrogate(text[count - 1])) count--;
            _text.Append(text.AsSpan(0, count));
            _truncated = true;
        }
    }

    /// <summary>Tekst od <paramref name="offset"/> (w znakach), nowy offset i czy cos pominieto.</summary>
    public (string Text, int Offset, bool Truncated) ReadFrom(int offset)
    {
        lock (_lock)
        {
            var start = offset < 0 || offset > _text.Length ? 0 : offset;
            return (_text.ToString(start, _text.Length - start), _text.Length, _truncated);
        }
    }

    public (string Text, bool Truncated) Snapshot()
    {
        lock (_lock) return (_text.ToString(), _truncated);
    }

    /// <summary>Wynik nie jest juz potrzebny: zwalnia bufor i odtad pomija wszystko, co jeszcze przyjdzie.</summary>
    public void Detach()
    {
        lock (_lock)
        {
            _detached = true;
            _text.Clear();
            _text.Capacity = 0;
        }
    }
}
