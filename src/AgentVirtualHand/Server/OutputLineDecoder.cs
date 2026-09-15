using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentVirtualHand.Server;

/// <summary>
/// Dzieli surowe bajty jednego potoku procesu na linie tekstu. Kodowanie (UTF-8 albo UTF-16LE) wybieramy
/// na poczatku kazdej linii, a linie tniemy dopiero w tym kodowaniu - natywne programy (cmd /u, wsl.exe,
/// sqlcmd -u) pisza UTF-16, a znak UTF-16 moze zawierac bajt 0A/0D, wiec ciecie po bajtach psuloby tekst.
/// Z gotowej linii zdejmujemy BOM i NUL. Instancja ma stan, wiec jedna na potok.
/// </summary>
public sealed class OutputLineDecoder(Action<string> onLine)
{
    private const byte LineFeed = 0x0A;
    private const byte CarriageReturn = 0x0D;
    private const int SampleLength = 8;

    private readonly List<byte> _pending = [];
    private Encoding _lastEncoding = Encoding.UTF8;
    private bool _lastLineEndedWithUtf8Terminator;

    // Linia konczy sie na LF albo na samym CR (paski postepu curl/pip). LF po CR (CRLF) nie jest nowa pusta
    // linia - zjadamy go na poczatku nastepnej, takze gdy przyszedl dopiero w kolejnym kawalku.
    private bool _skipLineFeedAfterCarriageReturn;

    // Dokad niedokonczona pierwsza linia z _pending zostala juz przeszukana (i w jakim kodowaniu) -
    // dluga linia bez LF (zminifikowany JSON, base64) nie jest skanowana od poczatku przy kazdym kawalku.
    private int _scannedUpTo;
    private Encoding? _scannedEncoding;

    /// <summary>Czyta potok do konca i przekazuje kolejne linie; blad odczytu (np. zabity proces) konczy potok.</summary>
    public static async Task PumpAsync(Stream stream, Action<string> onLine)
    {
        var decoder = new OutputLineDecoder(onLine);
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
                decoder.Append(buffer.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* potok zamkniety */ }
        decoder.Complete();
    }

    public void Append(ReadOnlySpan<byte> chunk)
    {
        _pending.AddRange(chunk);
        EmitCompleteLines(endOfStream: false);
    }

    /// <summary>Oddaje ostatnia linie bez znaku konca linii.</summary>
    public void Complete() => EmitCompleteLines(endOfStream: true);

    private void EmitCompleteLines(bool endOfStream)
    {
        var bytes = CollectionsMarshal.AsSpan(_pending);
        var consumed = 0;
        while (consumed < bytes.Length)
        {
            var rest = bytes[consumed..];
            if (IsOrphanedUtf16LineFeedTail(rest))
            {
                SwitchToUtf16AfterOrphanedTail();
                consumed++;
                continue;
            }
            if (_skipLineFeedAfterCarriageReturn)
            {
                if (!endOfStream && rest.Length < LineFeedWidth(_lastEncoding)) break;
                _skipLineFeedAfterCarriageReturn = false;
                consumed += LeadingLineFeedLength(rest, _lastEncoding);
                continue;
            }
            if (!endOfStream && !HasEnoughToChooseEncoding(rest)) break;

            var encoding = ChooseEncoding(rest);
            var length = LineLength(rest, encoding, ResumeScanAt(encoding));
            if (length < 0 && !endOfStream)
            {
                RememberScanned(rest.Length, encoding);
                break;
            }
            if (length < 0) length = rest.Length;

            _scannedEncoding = null;
            _lastEncoding = encoding;
            _lastLineEndedWithUtf8Terminator = encoding != Encoding.Unicode && IsTerminator(rest[length - 1]);
            _skipLineFeedAfterCarriageReturn = EndsWithCarriageReturn(rest[..length], encoding);
            onLine(Clean(encoding.GetString(rest[..length])));
            consumed += length;
        }
        _pending.RemoveRange(0, consumed);
    }

    /// <summary>
    /// Linia UTF-16 bez bajtow 00 w probce (np. same polskie litery) zostala wzieta za UTF-8 i ucieta po samym 0A/0D -
    /// jej 00 zaczyna nastepna linie. To dowod, ze strumien jest w UTF-16; sam NUL i tak bylby usuniety z tekstu.
    /// </summary>
    private bool IsOrphanedUtf16LineFeedTail(ReadOnlySpan<byte> rest)
        => _lastLineEndedWithUtf8Terminator && rest[0] == 0;

    private void SwitchToUtf16AfterOrphanedTail()
    {
        _lastEncoding = Encoding.Unicode;
        _lastLineEndedWithUtf8Terminator = false;
    }

    private static bool IsTerminator(byte value) => value is LineFeed or CarriageReturn;

    private static int LineFeedWidth(Encoding encoding) => encoding == Encoding.Unicode ? 2 : 1;

    private static bool EndsWithCarriageReturn(ReadOnlySpan<byte> line, Encoding encoding)
        => encoding == Encoding.Unicode
            ? line.EndsWith((ReadOnlySpan<byte>)[CarriageReturn, 0])
            : line[^1] == CarriageReturn;

    /// <summary>Dlugosc LF na poczatku <paramref name="rest"/> w danym kodowaniu albo 0, gdy go tam nie ma.</summary>
    private static int LeadingLineFeedLength(ReadOnlySpan<byte> rest, Encoding encoding)
    {
        ReadOnlySpan<byte> lineFeed = encoding == Encoding.Unicode ? [LineFeed, 0] : [LineFeed];
        return rest.StartsWith(lineFeed) ? lineFeed.Length : 0;
    }

    /// <summary>
    /// LF/CR na ostatnim bajcie moze byc pierwsza polowa UTF-16 "0A 00", wiec sam nie wystarcza - chyba ze to
    /// krotka linia UTF-8 ("Ready\n", "OK\r\n", "50%\r"): bez bajtow 00 i po linii UTF-8, wtedy oddajemy ja od razu.
    /// </summary>
    private bool HasEnoughToChooseEncoding(ReadOnlySpan<byte> rest)
        => rest.Length >= SampleLength || rest[..^1].ContainsAny(LineFeed, CarriageReturn) || IsShortUtf8Line(rest);

    private bool IsShortUtf8Line(ReadOnlySpan<byte> rest)
        => _lastEncoding != Encoding.Unicode && IsTerminator(rest[^1]) && !rest.Contains((byte)0);

    private Encoding ChooseEncoding(ReadOnlySpan<byte> rest)
    {
        var sample = rest[..Math.Min(rest.Length, SampleLength)];
        if (sample.StartsWith(Utf16ByteOrderMark)) return Encoding.Unicode;
        if (sample.StartsWith(Encoding.UTF8.Preamble)) return Encoding.UTF8;
        if (LooksLikeUtf16Ascii(sample) || LooksLikeUtf16NonAscii(sample)) return Encoding.Unicode;

        // Po linii UTF-16 zostajemy przy nim, dopoki probka nie jest poprawnym UTF-8 - np. sam tekst CJK nie ma bajtow 00.
        var continuesUtf16 = _lastEncoding == Encoding.Unicode && (HasNulAtOddPosition(sample) || !IsValidUtf8Prefix(sample));
        return continuesUtf16 ? Encoding.Unicode : Encoding.UTF8;
    }

    private static ReadOnlySpan<byte> Utf16ByteOrderMark => [0xFF, 0xFE];

    /// <summary>Tekst ASCII w UTF-16LE: kazdy znak to bajt niezerowy i 00. Pojedyncze NUL-e z UTF-8 (find -print0) tego nie spelniaja.</summary>
    private static bool LooksLikeUtf16Ascii(ReadOnlySpan<byte> sample)
    {
        if (sample.Length < 2) return false;
        for (var i = 0; i + 1 < sample.Length; i += 2)
            if (sample[i] == 0 || sample[i + 1] != 0) return false;
        return true;
    }

    /// <summary>
    /// Polskie litery w UTF-16LE ("Łódź" = 41 01 F3 00 ..., "Łańcuch" = 41 01 61 00 44 01 ...): bajty 00 na pozycjach
    /// nieparzystych, a obok nich niepoprawny UTF-8 albo same starsze bajty znakow spoza ASCII (01-1F), ktore w tekscie
    /// UTF-8 bylyby znakami sterujacymi. Poprawny UTF-8 z NUL-ami (find -print0) tego nie spelnia.
    /// </summary>
    private static bool LooksLikeUtf16NonAscii(ReadOnlySpan<byte> sample)
        => HasNulAtOddPosition(sample) && (!IsValidUtf8Prefix(sample) || HasOnlyUtf16HighBytesAtOddPositions(sample));

    /// <summary>
    /// Kazda pelna para to znak U+0000-U+1FFF inny niz NUL: starszy bajt 00 albo znak sterujacy, ktorego tekst UTF-8
    /// nie zawiera. TAB/LF/CR i ESC (kolory ANSI) sa wykluczone, bo zwykle wyjscie UTF-8 ma je miedzy literami.
    /// </summary>
    private static bool HasOnlyUtf16HighBytesAtOddPositions(ReadOnlySpan<byte> sample)
    {
        for (var i = 0; i + 1 < sample.Length; i += 2)
        {
            var high = sample[i + 1];
            if (high == 0 ? sample[i] == 0 : !IsUtf16HighByteUnlikelyInUtf8(high)) return false;
        }
        return true;
    }

    private static bool IsUtf16HighByteUnlikelyInUtf8(byte value)
        => value < 0x20 && value is not (byte)'\t' and not LineFeed and not CarriageReturn and not 0x1B;

    private static bool HasNulAtOddPosition(ReadOnlySpan<byte> sample)
    {
        for (var i = 1; i < sample.Length; i += 2)
            if (sample[i] == 0) return true;
        return false;
    }

    /// <summary>Poprawny UTF-8, przy czym ostatni znak moze byc uciety na granicy probki.</summary>
    private static bool IsValidUtf8Prefix(ReadOnlySpan<byte> sample)
    {
        while (!sample.IsEmpty)
        {
            var status = Rune.DecodeFromUtf8(sample, out _, out var consumed);
            if (status == OperationStatus.NeedMoreData) return true;
            if (status != OperationStatus.Done) return false;
            sample = sample[consumed..];
        }
        return true;
    }

    /// <summary>Wznowienie skanu ma sens tylko w tym samym kodowaniu, w ktorym linie przeszukano.</summary>
    private int ResumeScanAt(Encoding encoding) => encoding == _scannedEncoding ? _scannedUpTo : 0;

    /// <summary>W UTF-16 sprawdzone sa pelne pary bajtow, wiec nastepny skan zaczyna od parzystej pozycji.</summary>
    private void RememberScanned(int length, Encoding encoding)
    {
        _scannedEncoding = encoding;
        _scannedUpTo = encoding == Encoding.Unicode ? length & ~1 : length;
    }

    /// <summary>
    /// Dlugosc linii razem z LF albo CR, -1 gdy linia jeszcze sie nie skonczyla. Szuka od <paramref name="start"/>,
    /// bo wczesniejsze bajty juz sprawdzono. W UTF-16 terminator to wyrownana para 0A 00 / 0D 00.
    /// </summary>
    private static int LineLength(ReadOnlySpan<byte> rest, Encoding encoding, int start)
    {
        if (encoding != Encoding.Unicode)
        {
            var index = rest[start..].IndexOfAny(LineFeed, CarriageReturn);
            return index < 0 ? -1 : start + index + 1;
        }

        for (var i = start; i + 1 < rest.Length; i += 2)
            if (IsTerminator(rest[i]) && rest[i + 1] == 0) return i + 2;
        return -1;
    }

    private const char ByteOrderMarkChar = '\uFEFF';
    private const char NulChar = '\0';

    private static string Clean(string line)
    {
        var withoutTerminator = line.EndsWith('\n') || line.EndsWith('\r') ? line[..^1] : line;
        return withoutTerminator.Replace(ByteOrderMarkChar.ToString(), "").Replace(NulChar.ToString(), "");
    }
}
