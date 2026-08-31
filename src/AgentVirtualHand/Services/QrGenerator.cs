using Avalonia.Media.Imaging;
using QRCoder;

namespace AgentVirtualHand.Services;

public static class QrGenerator
{
    /// <summary>Renderuje kod QR jako bitmapę gotową do wyświetlenia w Avalonii.</summary>
    public static Bitmap Create(string payload, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);

        // Ciemne tło pod jasnymi modułami - pasuje do motywu aplikacji i skanuje się tak samo dobrze.
        var bytes = png.GetGraphic(
            pixelsPerModule,
            darkColorRgba: [0x0E, 0x11, 0x16, 0xFF],
            lightColorRgba: [0xE6, 0xE9, 0xEF, 0xFF]);

        using var stream = new MemoryStream(bytes);
        return new Bitmap(stream);
    }
}
