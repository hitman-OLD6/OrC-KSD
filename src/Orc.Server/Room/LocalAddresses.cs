using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Orc.Mcp.Room;

namespace Orc.Server.Rooms;

/// <summary>
/// 可连接地址枚举（多人支持 S8b）：非回环 IPv4 ＋ 实际监听端口。
/// <para>房主 UI 负责展示/复制；协议不猜"哪个网卡正确"。</para>
/// </summary>
public static class LocalAddresses
{
    /// <summary>收集当前机器的可连接地址。</summary>
    public static IReadOnlyList<RoomListenAddress> Collect(int port)
    {
        if (port <= 0)
        {
            return [];
        }

        var addresses = new List<RoomListenAddress>();
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up
                    || network.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork
                        || IPAddress.IsLoopback(unicast.Address))
                    {
                        continue;
                    }

                    addresses.Add(new RoomListenAddress(unicast.Address.ToString(), port));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // 枚举失败＝无候选地址（客户端仍可走 loopback）。
        }

        return addresses;
    }
}
