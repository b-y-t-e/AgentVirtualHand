using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AgentVirtualHand.Services;

public static class NetworkInfo
{
    /// <summary>Adresy IPv4 aktywnych interfejsów, z pominięciem loopbacka i wirtualnych tuneli.</summary>
    public static IReadOnlyList<string> LocalAddresses()
    {
        var result = new List<string>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(addr.Address)) continue;
                result.Add(addr.Address.ToString());
            }
        }

        // Adresy APIPA na końcu - rzadko są tym, czym chcemy się połączyć.
        return result
            .OrderBy(ip => ip.StartsWith("169.254.") ? 1 : 0)
            .ThenBy(ip => ip)
            .ToList();
    }

    public static string PrimaryAddress() => LocalAddresses().FirstOrDefault() ?? "127.0.0.1";
}
