using System.Net;
using System.Net.Sockets;

namespace ConnectivityProbe.Monitor;

/// <summary>
/// Pod adresinden ağ adı üretir. Kubernetes her cluster'a ayrı bir pod ağı verir (ör. 10.42.0.0/16) ve her node bunun bir
/// alt ağını (/24) kullanır; bu yüzden pod'lar IPv4'te /16 ağına göre gruplanır: 10.42.1.15 ve 10.42.2.16 aynı cluster,
/// 10.43.1.21 başka cluster. IPv6'da /64.
/// </summary>
public static class Network
{
    public static string? Of(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !IPAddress.TryParse(address, out var ip)) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork) return $"{bytes[0]}.{bytes[1]}.0.0/16";

        for (var i = 8; i < bytes.Length; i++) bytes[i] = 0;
        return new IPAddress(bytes) + "/64";
    }

    /// <summary>
    /// Pod'un asıl adresi: pod'un bildirdiği adres (2.1+); yoksa ilk IPv4 arayüz adresi; o da yoksa bildirimin geldiği adres.
    /// </summary>
    public static string? PrimaryOf(string? reported, IEnumerable<string> localAddresses, string? sourceIp)
    {
        if (Of(reported) != null) return reported;
        var local = localAddresses.FirstOrDefault(a => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                    ?? localAddresses.FirstOrDefault(a => Of(a) != null);
        return local ?? (Of(sourceIp) != null ? sourceIp : null);
    }
}
