using System.Text;

namespace AgentVirtualHand.Server;

/// <summary>
/// Jedno miejsce rozpoznawania kodowania tekstu po BOM (UTF-8, UTF-16LE/BE, UTF-32). BOM jest zdejmowany,
/// wiec wynik nie zawiera U+FEFF ani NUL-i po kazdym znaku. Plik wspolny z hubem.
/// </summary>
public static class BomText
{
    /// <summary>Dekoduje bajty kodowaniem z BOM, a bez BOM - <paramref name="encodingWithoutBom"/>.</summary>
    public static string Decode(byte[] bytes, Encoding encodingWithoutBom)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), encodingWithoutBom, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
