using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AgentVirtualHand.Services;

/// <summary>
/// Testuje warianty bindowania gniazda w procesie aplikacji.
/// Potrzebne, bo ten sam port potrafi sie zbindowac przez TcpListener,
/// a odmowic dostepu (WSAEACCES) przy gnieździe tworzonym tak jak robi to Kestrel.
/// </summary>
public static class SocketProbe
{
    public static string Diagnose(int port)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"--- test gniazd w procesie, port {port} ---");

        Check(sb, "TcpListener(Any)", () =>
        {
            var l = new TcpListener(IPAddress.Any, port);
            l.Start();
            return new Closer(l.Stop);
        });

        Check(sb, "TcpListener(IPv6Any)", () =>
        {
            var l = new TcpListener(IPAddress.IPv6Any, port);
            l.Start();
            return new Closer(l.Stop);
        });

        Check(sb, "Socket IPv4 domyslny (jak Kestrel)", () => Raw(AddressFamily.InterNetwork, IPAddress.Any, port, null));
        Check(sb, "Socket IPv4 ExclusiveAddressUse=true", () => Raw(AddressFamily.InterNetwork, IPAddress.Any, port, s => s.ExclusiveAddressUse = true));
        Check(sb, "Socket IPv4 ExclusiveAddressUse=false", () => Raw(AddressFamily.InterNetwork, IPAddress.Any, port, s => s.ExclusiveAddressUse = false));
        Check(sb, "Socket IPv4 ReuseAddress", () => Raw(AddressFamily.InterNetwork, IPAddress.Any, port,
            s => s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true)));
        Check(sb, "Socket IPv6 DualMode", () => Raw(AddressFamily.InterNetworkV6, IPAddress.IPv6Any, port, s => s.DualMode = true));
        Check(sb, "Socket IPv6 bez DualMode", () => Raw(AddressFamily.InterNetworkV6, IPAddress.IPv6Any, port, null));
        Check(sb, "Socket IPv4 loopback 127.0.0.1", () => Raw(AddressFamily.InterNetwork, IPAddress.Loopback, port, null));

        var lan = NetworkInfo.PrimaryAddress();
        Check(sb, $"Socket IPv4 konkretny adres {lan}", () => Raw(AddressFamily.InterNetwork, IPAddress.Parse(lan), port, null));

        Check(sb, "Socket IPv4 port 0 (dowolny wolny)", () => Raw(AddressFamily.InterNetwork, IPAddress.Any, 0, null));

        return sb.ToString();
    }

    private static Closer Raw(AddressFamily family, IPAddress address, int port, Action<Socket>? configure)
    {
        var s = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            configure?.Invoke(s);
            s.Bind(new IPEndPoint(address, port));
            s.Listen(16);
            return new Closer(s.Dispose);
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    private static void Check(StringBuilder sb, string name, Func<Closer> attempt)
    {
        Closer? closer = null;
        try
        {
            closer = attempt();
            sb.AppendLine($"  {name,-42} -> OK");
        }
        catch (SocketException ex)
        {
            sb.AppendLine($"  {name,-42} -> FAIL {ex.SocketErrorCode} ({ex.ErrorCode})");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  {name,-42} -> FAIL {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { closer?.Dispose(); } catch { /* nieistotne w diagnostyce */ }
        }
    }

    private sealed class Closer(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
