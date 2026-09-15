using System.Text;

namespace AgentVirtualHand.Server;

/// <summary>
/// Tresc skryptu z zadania exec: pole 'script' albo 'scriptBase64'. Plik wspolny z hubem,
/// zeby log huba pokazywal dokladnie to, co host wykona albo odrzuci.
/// </summary>
public static class ExecScript
{
    /// <summary>Bez BOM tylko poprawny UTF-8 - niepoprawne bajty (np. ANSI cp1250) rzucaja zamiast dawac U+FFFD.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Zadanie niesie skrypt - wtedy ma pierwszenstwo przed 'command'.</summary>
    public static bool IsPresent(string? script, string? scriptBase64)
        => !string.IsNullOrEmpty(script) || !string.IsNullOrEmpty(scriptBase64);

    /// <summary>
    /// Tresc skryptu; null, gdy 'scriptBase64' nie jest poprawnym base64 albo nie jest tekstem
    /// UTF-8 / UTF-16 / UTF-32 z BOM (zepsute polskie litery, NUL-e z UTF-16 bez BOM).
    /// </summary>
    public static string? Decode(string? script, string? scriptBase64)
    {
        if (string.IsNullOrEmpty(scriptBase64)) return script;
        try { return DecodeText(Convert.FromBase64String(scriptBase64)); }
        catch (FormatException) { return null; }
    }

    /// <summary>Kodowanie z BOM (BOM jest zdejmowany), bez BOM scisle UTF-8; tekst z NUL-em nie jest skryptem.</summary>
    private static string? DecodeText(byte[] bytes)
    {
        try
        {
            var text = BomText.Decode(bytes, StrictUtf8);
            return text.Contains('\0') ? null : text;
        }
        catch (DecoderFallbackException) { return null; }
    }
}
