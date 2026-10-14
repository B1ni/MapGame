using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MapGame;

public static class LanNetwork
{
    public static string[] GetAddresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up
                && network.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .OrderByDescending(network => network.GetIPProperties().GatewayAddresses
                .Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork))
            .ThenBy(network => network.Name, StringComparer.Ordinal)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork
                && !System.Net.IPAddress.IsLoopback(address.Address)
                && !(address.Address.GetAddressBytes()[0] == 169 && address.Address.GetAddressBytes()[1] == 254))
            .Select(address => $"http://{address.Address}:5080")
            .Distinct()
            .ToArray();
    }
}
