using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HapticBridge.Integrations.Apollo;

/// <summary>Verifies the server side of a Windows IPv4 loopback connection before TLS starts.</summary>
internal static class ApolloLocalPeer
{
    private const int OwnerPidConnections = 4;
    private const int Established = 5;
    private const uint InsufficientBuffer = 122;
    private const int RowBytes = 6 * sizeof(int);

    internal static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, int port,
        string executable, CancellationToken cancellation)
    {
        if (context.DnsEndPoint.Port != port ||
            !string.Equals(context.DnsEndPoint.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Apollo administration requires the local host.");

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, port, cancellation).ConfigureAwait(false);
            var local = (IPEndPoint)socket.LocalEndPoint!;
            int? owner = FindPeerOwner(local, port) ?? FindMappedPeerOwner(local, port);
            if (owner is null || !string.Equals(ReadImagePath(owner.Value), executable, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Windows could not verify that the local connection belongs to installed Apollo.");
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // A dual-stack listener can appear in the IPv6 owner table even for an IPv4 client.
    private static int? FindMappedPeerOwner(IPEndPoint client, int serverPort)
    {
        const int rowBytes = 56;
        int size = 0;
        int family = (int)AddressFamily.InterNetworkV6;
        uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, OwnerPidConnections, 0);
        var address = client.Address.MapToIPv6().GetAddressBytes();
        for (int attempt = 0; attempt < 3 && result == InsufficientBuffer && size > 0; attempt++)
        {
            int capacity = size;
            IntPtr table = Marshal.AllocHGlobal(capacity);
            try
            {
                result = GetExtendedTcpTable(table, ref size, false, family, OwnerPidConnections, 0);
                if (result != 0) continue;
                int count = Marshal.ReadInt32(table);
                if (count < 0 || count > (capacity - sizeof(int)) / rowBytes) return null;
                var local = new byte[16];
                var remote = new byte[16];
                for (int index = 0; index < count; index++)
                {
                    IntPtr row = table + sizeof(int) + index * rowBytes;
                    if (Marshal.ReadInt32(row, 48) != Established ||
                        ReadPort(row, 20) != serverPort || ReadPort(row, 44) != client.Port) continue;
                    Marshal.Copy(row, local, 0, local.Length);
                    Marshal.Copy(row + 24, remote, 0, remote.Length);
                    if (local.AsSpan().SequenceEqual(address) && remote.AsSpan().SequenceEqual(address))
                        return Marshal.ReadInt32(row, 52);
                }
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        return null;
    }

    internal static int? FindPeerOwner(IPEndPoint client, int serverPort)
    {
        int size = 0;
        uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, (int)AddressFamily.InterNetwork, OwnerPidConnections, 0);
        int loopback = BitConverter.ToInt32(IPAddress.Loopback.GetAddressBytes());
        int clientAddress = BitConverter.ToInt32(client.Address.GetAddressBytes());
        for (int attempt = 0; attempt < 3 && result == InsufficientBuffer && size > 0; attempt++)
        {
            int capacity = size;
            IntPtr table = Marshal.AllocHGlobal(capacity);
            try
            {
                result = GetExtendedTcpTable(table, ref size, false, (int)AddressFamily.InterNetwork, OwnerPidConnections, 0);
                if (result == InsufficientBuffer) continue;
                if (result != 0) break;

                int count = Marshal.ReadInt32(table);
                if (count < 0 || count > (capacity - sizeof(int)) / RowBytes) return null;
                for (int index = 0; index < count; index++)
                {
                    IntPtr row = table + sizeof(int) + index * RowBytes;
                    if (Marshal.ReadInt32(row) == Established &&
                        Marshal.ReadInt32(row, 4) == loopback && ReadPort(row, 8) == serverPort &&
                        Marshal.ReadInt32(row, 12) == clientAddress && ReadPort(row, 16) == client.Port)
                        return Marshal.ReadInt32(row, 20);
                }
                return null;
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        throw new Win32Exception((int)result, "Windows could not inspect the local Apollo connection.");
    }

    private static ushort ReadPort(IntPtr row, int offset) =>
        unchecked((ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, offset)));

    internal static string? ReadImagePath(int processId)
    {
        const uint queryLimitedInformation = 0x1000;
        using var process = OpenProcess(queryLimitedInformation, false, processId);
        if (process.IsInvalid) return null;
        var path = new StringBuilder(32768);
        int length = path.Capacity;
        return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : null;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, uint reserved);

    [DllImport("kernel32.dll")]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int length);
}
