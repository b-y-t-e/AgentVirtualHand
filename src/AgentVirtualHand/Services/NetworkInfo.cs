using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AgentVirtualHand.Services;

/// <summary>Adres, pod którym maszyna jest osiągalna, wraz z nazwą interfejsu.</summary>
public sealed record LocalAddress(string Address, string Interface)
{
    /// <summary>To, co widać na liście wyboru w GUI.</summary>
    public override string ToString() => $"{Address}  ·  {Interface}";
}

public static class NetworkInfo
{
    /// <summary>
    /// Adresy IPv4 aktywnych interfejsów, bez loopbacka.
    /// Tunele (Tailscale, VPN) sa uwzglednione - czesto to wlasnie nimi laczy sie druga maszyna.
    /// </summary>
    public static IReadOnlyList<LocalAddress> LocalAddresses()
    {
        var result = new List<LocalAddress>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(addr.Address)) continue;
                result.Add(new LocalAddress(addr.Address.ToString(), nic.Name));
            }
        }

        return result.OrderBy(a => Rank(a.Address)).ThenBy(a => a.Address).ToList();
    }

    /// <summary>
    /// Kolejnosc na liscie: najpierw zwykla siec lokalna, potem sieci nakladkowe typu Tailscale,
    /// dalej reszta, a na koncu adresy APIPA - te prawie nigdy nie sa tym, czego szukamy.
    /// </summary>
    private static int Rank(string ip) => ip switch
    {
        _ when ip.StartsWith("169.254.") => 4,
        _ when ip.StartsWith("192.168.") => 0,
        _ when ip.StartsWith("10.") => 0,
        _ when IsPrivate172(ip) => 0,
        _ when IsCarrierGrade(ip) => 1,   // 100.64/10 - m.in. Tailscale
        _ => 2,
    };

    private static bool IsPrivate172(string ip)
    {
        var parts = ip.Split('.');
        return parts.Length == 4 && parts[0] == "172"
            && int.TryParse(parts[1], out var second) && second is >= 16 and <= 31;
    }

    private static bool IsCarrierGrade(string ip)
    {
        var parts = ip.Split('.');
        return parts.Length == 4 && parts[0] == "100"
            && int.TryParse(parts[1], out var second) && second is >= 64 and <= 127;
    }

    public static string PrimaryAddress() => LocalAddresses().FirstOrDefault()?.Address ?? "127.0.0.1";
}
