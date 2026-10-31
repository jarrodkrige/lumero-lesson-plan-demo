using System.Net;
using System.Net.Sockets;

namespace LessonPlanner.Infrastructure.Retrieval;

internal static class PublicAddressPolicy
{
    private static readonly IPNetwork[] RejectedV4 =
    [
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"), IPNetwork.Parse("192.0.2.0/24"), IPNetwork.Parse("192.31.196.0/24"),
        IPNetwork.Parse("192.52.193.0/24"), IPNetwork.Parse("192.88.99.0/24"), IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("192.175.48.0/24"), IPNetwork.Parse("198.18.0.0/15"), IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"), IPNetwork.Parse("224.0.0.0/4"), IPNetwork.Parse("240.0.0.0/4")
    ];

    private static readonly IPNetwork GlobalUnicastV6 = IPNetwork.Parse("2000::/3");
    private static readonly IPNetwork[] RejectedV6 =
    [
        IPNetwork.Parse("2001::/23"), IPNetwork.Parse("2001:db8::/32"), IPNetwork.Parse("2002::/16"),
        IPNetwork.Parse("2620:4f:8000::/48"), IPNetwork.Parse("3fff::/20")
    ];

    internal static bool IsPublic(IPAddress address) => address.AddressFamily switch
    {
        AddressFamily.InterNetwork => !RejectedV4.Any(network => network.Contains(address)),
        AddressFamily.InterNetworkV6 => address.ScopeId == 0 && !address.IsIPv4MappedToIPv6 &&
            GlobalUnicastV6.Contains(address) && !RejectedV6.Any(network => network.Contains(address)),
        _ => false
    };
}
