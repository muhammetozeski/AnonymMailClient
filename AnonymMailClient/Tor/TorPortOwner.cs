using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AnonymMailClient.Tor;

public static class TorPortOwner
{
    const bool IsLogEnabled = true;

    public static int? Of(string host, int port)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!TryReadAsIPv4(host, out var address)) return null;

        try
        {
            return OwnerOnWindows(address, port);
        }
        catch (Exception ex)
        {
            Log($"Could not read owner of port {port}: {ex.Message}", LogLevel.Warning, isRun: IsLogEnabled);
            return null;
        }
    }

    static bool TryReadAsIPv4(string host, out IPAddress address)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            address = IPAddress.Loopback;
            return true;
        }

        return IPAddress.TryParse(host, out address!)
               && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    const int AfInet = 2;
    const int TcpTableOwnerPidListener = 3;
    const uint NoError = 0;
    const uint ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int addressFamily, int tableClass, int reserved);

    [SupportedOSPlatform("windows")]
    static int? OwnerOnWindows(IPAddress address, int port)
    {
        int size = 0;
        uint status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        if (status != ErrorInsufficientBuffer && status != NoError) return null;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != NoError) return null;

            int rows = Marshal.ReadInt32(buffer);
            IntPtr cursor = buffer + sizeof(int);
            int rowSize = Marshal.SizeOf<TcpRowOwnerPid>();

            for (int i = 0; i < rows; i++, cursor += rowSize)
            {
                var row = Marshal.PtrToStructure<TcpRowOwnerPid>(cursor);
                int rowPort = ((int)(row.LocalPort & 0xFF) << 8) | (int)((row.LocalPort >> 8) & 0xFF);
                if (rowPort != port) continue;

                var rowAddress = new IPAddress(row.LocalAddress);
                if (rowAddress.Equals(address) || rowAddress.Equals(IPAddress.Any))
                    return (int)row.OwningPid;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return null;
    }
}
