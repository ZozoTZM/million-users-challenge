using System;
using System.Runtime.InteropServices;

namespace Feed;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct EpollEvent
{
    public uint Events;
    public ulong Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RLimit
{
    public ulong Current;
    public ulong Maximum;
}

internal static unsafe class Native
{
    private const string Libc = "libc";

    public const uint EpollIn = 0x001;
    public const uint EpollOut = 0x004;
    public const uint EpollErr = 0x008;
    public const uint EpollHup = 0x010;
    public const uint EpollRdhup = 0x2000;

    public const int EpollCtlAdd = 1;
    public const int EpollCtlDel = 2;
    public const int EpollCtlMod = 3;

    public const int MsgNoSignal = 0x4000;
    public const int TcpNoDelayOption = 1;
    public const int TcpDeferAcceptOption = 9;
    public const int IpProtoTcp = 6;
    public const int SolSocket = 1;
    public const int SoReusePort = 15;
    public const int RlimitNoFile = 7;

    public const int Eintr = 4;
    public const int Eagain = 11;
    public const int EwouldBlock = 11;
    public const int EinProgress = 115;

    [DllImport(Libc, SetLastError = true)]
    public static extern int epoll_create1(int flags);

    [DllImport(Libc, SetLastError = true)]
    public static extern int epoll_ctl(int epfd, int op, int fd, ref EpollEvent ev);

    [DllImport(Libc, SetLastError = true)]
    public static extern int epoll_wait(int epfd, EpollEvent* events, int maxEvents, int timeout);

    [DllImport(Libc, SetLastError = true)]
    public static extern int accept4(int sockfd, IntPtr addr, IntPtr addrlen, int flags);

    [DllImport(Libc, SetLastError = true)]
    public static extern int close(int fd);

    [DllImport(Libc, SetLastError = true)]
    public static extern int shutdown(int fd, int how);

    [DllImport(Libc, SetLastError = true)]
    public static extern int setsockopt(int fd, int level, int option, ref int value, int length);

    [DllImport(Libc, SetLastError = true)]
    public static extern int getrlimit(int resource, out RLimit limit);

    [DllImport(Libc, SetLastError = true)]
    private static extern int recv(int fd, byte* buffer, int length, int flags);

    [DllImport(Libc, SetLastError = true)]
    private static extern int send(int fd, byte* buffer, int length, int flags);

    public static int Recv(int fd, byte[] buffer, int offset, int length)
    {
        fixed (byte* p = &buffer[offset])
        {
            return recv(fd, p, length, 0);
        }
    }

    public static int Send(int fd, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return 0;
        }

        fixed (byte* p = data)
        {
            return send(fd, p, data.Length, MsgNoSignal);
        }
    }

    public static int LastError => Marshal.GetLastPInvokeError();

    public static bool WouldBlock(int error) => error == Eagain || error == EwouldBlock;

    public static void SetTcpNoDelay(int fd)
    {
        int one = 1;
        setsockopt(fd, IpProtoTcp, TcpNoDelayOption, ref one, sizeof(int));
    }

    public static void SetTcpDeferAccept(int fd)
    {
        int one = 1;
        setsockopt(fd, IpProtoTcp, TcpDeferAcceptOption, ref one, sizeof(int));
    }

    public static int MaxFiles()
    {
        if (getrlimit(RlimitNoFile, out RLimit limit) == 0 && limit.Current > 0 &&
            limit.Current < int.MaxValue)
        {
            return (int)limit.Current;
        }

        return 1 << 20;
    }
}
