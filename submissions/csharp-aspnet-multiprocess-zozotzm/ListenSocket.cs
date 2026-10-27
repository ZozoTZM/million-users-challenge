using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

// Kestrel's documented hook for a custom listening socket. SO_REUSEPORT lets several Kestrel
// processes bind the same PORT/EXTRA_PORT and have the kernel spread new connections over them.
static class ListenSocket
{
    private const int SolSocket = 1;
    private const int SoReusePort = 15; // Linux

    public static Socket Create(EndPoint endpoint)
    {
        var ip = (IPEndPoint)endpoint;
        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        if (OperatingSystem.IsLinux())
        {
            Span<byte> one = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(one, 1);
            socket.SetRawSocketOption(SolSocket, SoReusePort, one);
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.Address.Equals(IPAddress.IPv6Any))
            socket.DualMode = true;

        socket.Bind(ip);
        return socket;
    }
}
