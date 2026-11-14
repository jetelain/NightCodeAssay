using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NightCodeAssay.Infrastructure.Context;

/// <summary>
/// Resolves a loopback TCP port (such as <c>http://localhost:5270</c>) to the listening local
/// process ID using zero-process-spawning, NativeAOT-safe OS network tables.
/// </summary>
public sealed class LocalPortProcessResolver
{
    internal const uint AfInet = 2;
    internal const uint AfInet6 = 23;

    private const uint TcpTableOwnerPidListener = 3;
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;

    internal const int MibTcpStateListen = 2;
    internal const int IPv4RowSize = 24;
    internal const int IPv6RowSize = 56;

    private static readonly TimeSpan LinuxScanBudget = TimeSpan.FromMilliseconds(200);

    public static bool TryExtractLoopbackPort(string? url, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.DnsSafeHost;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var isLoopback = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

        if (!isLoopback || uri.Port is <= 0 or > 65535)
        {
            return false;
        }

        port = uri.Port;
        return true;
    }

    public int? TryResolveListeningPid(int port)
    {
        if (port is <= 0 or > 65535)
        {
            return null;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsGetListeningPid(port);
            }

            if (OperatingSystem.IsLinux())
            {
                return LinuxGetListeningPid(port);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int? WindowsGetListeningPid(int localPort)
    {
        var ipv4Table = WindowsReadTcpTable(AfInet);
        var ipv6Table = WindowsReadTcpTable(AfInet6);
        return SelectBestListeningPid(ipv4Table, ipv6Table, localPort);
    }

    [SupportedOSPlatform("windows")]
    private static byte[]? WindowsReadTcpTable(uint ipVersion)
    {
        uint tableSize = 0;
        var result = GetExtendedTcpTable(null, ref tableSize, false, ipVersion, TcpTableOwnerPidListener, 0);
        if (result != ErrorInsufficientBuffer || tableSize < 4)
        {
            return null;
        }

        byte[]? buffer = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            buffer = new byte[tableSize];
            result = GetExtendedTcpTable(buffer, ref tableSize, false, ipVersion, TcpTableOwnerPidListener, 0);
            if (result == NoError)
            {
                return buffer;
            }

            if (result != ErrorInsufficientBuffer || tableSize < 4)
            {
                return null;
            }
        }

        return null;
    }

    internal static int? SelectBestListeningPid(byte[]? ipv4Table, byte[]? ipv6Table, int localPort)
    {
        var v4 = ipv4Table is not null ? MatchListeningCandidateInTable(ipv4Table, localPort, AfInet) : null;
        var v6 = ipv6Table is not null ? MatchListeningCandidateInTable(ipv6Table, localPort, AfInet6) : null;

        if (v4 is null)
        {
            return v6?.Pid;
        }

        if (v6 is null)
        {
            return v4.Value.Pid;
        }

        return v6.Value.Rank > v4.Value.Rank ? v6.Value.Pid : v4.Value.Pid;
    }

    /// <summary>
    /// Parses a raw <c>GetExtendedTcpTable</c> buffer and returns the listening owning PID for
    /// <paramref name="localPort"/>, preferring explicit loopback and non-kernel (<c>PID != 4</c>) bindings.
    /// </summary>
    internal static int? MatchListeningPidInTable(byte[] table, int localPort, uint ipVersion) =>
        MatchListeningCandidateInTable(table, localPort, ipVersion)?.Pid;

    private static (int Pid, int Rank)? MatchListeningCandidateInTable(byte[] table, int localPort, uint ipVersion)
    {
        if (table is null || table.Length < 4 || localPort is <= 0 or > 65535)
        {
            return null;
        }

        try
        {
            var rowSize = ipVersion == AfInet6 ? IPv6RowSize : IPv4RowSize;
            var numEntries = BitConverter.ToInt32(table, 0);
            if (numEntries <= 0)
            {
                return null;
            }

            var offset = 4;
            int? bestPid = null;
            var bestRank = -1;

            for (var i = 0; i < numEntries; i++, offset += rowSize)
            {
                if (offset + rowSize > table.Length)
                {
                    break;
                }

                int state, localPortField, owningPid;
                bool isLoopbackAddr;

                if (ipVersion == AfInet6)
                {
                    localPortField = ReadNetworkOrderPort(table, offset + 20);
                    state = BitConverter.ToInt32(table, offset + 48);
                    owningPid = (int)ReadUInt32(table, offset + 52);
                    isLoopbackAddr = IsIPv6Loopback(table.AsSpan(offset, 16));
                }
                else
                {
                    localPortField = ReadNetworkOrderPort(table, offset + 8);
                    state = BitConverter.ToInt32(table, offset);
                    owningPid = (int)ReadUInt32(table, offset + 20);
                    isLoopbackAddr = table[offset + 4] == 127;
                }

                if (state != MibTcpStateListen || localPortField != localPort || owningPid <= 0)
                {
                    continue;
                }

                var rank = (owningPid != 4 ? 2 : 0) + (isLoopbackAddr ? 1 : 0);
                if (rank > bestRank)
                {
                    bestRank = rank;
                    bestPid = owningPid;
                    if (bestRank == 3)
                    {
                        return (bestPid.Value, bestRank);
                    }
                }
            }

            return bestPid.HasValue ? (bestPid.Value, bestRank) : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? LinuxGetListeningPid(int localPort)
    {
        var v4 = LinuxFindListeningInode("/proc/net/tcp", localPort);
        var v6 = LinuxFindListeningInode("/proc/net/tcp6", localPort);

        long? inode;
        if (v4 is null)
        {
            inode = v6?.Inode;
        }
        else if (v6 is null)
        {
            inode = v4.Value.Inode;
        }
        else
        {
            inode = v6.Value.IsLoopback && !v4.Value.IsLoopback ? v6.Value.Inode : v4.Value.Inode;
        }

        return inode is null ? null : LinuxFindPidWithInode(inode.Value);
    }

    private static (long Inode, bool IsLoopback)? LinuxFindListeningInode(string procNetPath, int localPort)
    {
        try
        {
            if (!File.Exists(procNetPath))
            {
                return null;
            }

            return MatchListeningInodeCandidateInProcLines(File.ReadLines(procNetPath), localPort);
        }
        catch
        {
            return null;
        }
    }

    internal static long? MatchListeningInodeInProcLines(IEnumerable<string> lines, int localPort) =>
        MatchListeningInodeCandidateInProcLines(lines, localPort)?.Inode;

    private static (long Inode, bool IsLoopback)? MatchListeningInodeCandidateInProcLines(IEnumerable<string> lines, int localPort)
    {
        if (lines is null || localPort is <= 0 or > 65535)
        {
            return null;
        }

        long? fallbackInode = null;

        foreach (var line in lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 10 || !string.Equals(parts[3], "0A", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var colon = parts[1].IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var addrHex = parts[1][..colon];
            var portHex = parts[1][(colon + 1)..];
            if (!int.TryParse(portHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedPort) ||
                parsedPort != localPort)
            {
                continue;
            }

            if (!long.TryParse(parts[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var inode) ||
                inode <= 0)
            {
                continue;
            }

            if (IsProcHexLoopback(addrHex))
            {
                return (inode, true);
            }

            fallbackInode ??= inode;
        }

        return fallbackInode.HasValue ? (fallbackInode.Value, false) : null;
    }

    private static int? LinuxFindPidWithInode(long inode)
    {
        var deadline = DateTime.UtcNow + LinuxScanBudget;
        var fdTarget = $"socket:[{inode}]";
        var fdsChecked = 0;

        string[] pidDirs;
        try
        {
            pidDirs = Directory.EnumerateDirectories("/proc").ToArray();
        }
        catch
        {
            return null;
        }

        foreach (var pidDir in pidDirs)
        {
            var name = Path.GetFileName(pidDir);
            if (!IsAllDigits(name.AsSpan()))
            {
                continue;
            }

            var fdDir = Path.Combine(pidDir, "fd");
            string[] fds;
            try
            {
                fds = Directory.EnumerateFileSystemEntries(fdDir).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var fd in fds)
            {
                if ((++fdsChecked & 0xFF) == 0 && DateTime.UtcNow > deadline)
                {
                    return null;
                }

                string? linkTarget;
                try
                {
                    linkTarget = new FileInfo(fd).LinkTarget;
                }
                catch
                {
                    continue;
                }

                if (string.Equals(linkTarget, fdTarget, StringComparison.Ordinal) &&
                    int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                {
                    return pid;
                }
            }
        }

        return null;
    }

    private static bool IsProcHexLoopback(string addrHex)
    {
        if (addrHex.Length == 8 &&
            addrHex.EndsWith("7F", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(addrHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            return true;
        }

        if (addrHex.Length == 32)
        {
            if (addrHex.Equals("00000000000000000000000001000000", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (addrHex.StartsWith("0000000000000000FFFF0000", StringComparison.OrdinalIgnoreCase) &&
                addrHex.EndsWith("7F", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIPv6Loopback(ReadOnlySpan<byte> addr)
    {
        if (addr.Length != 16)
        {
            return false;
        }

        var isPureV6Loopback = true;
        for (var i = 0; i < 15; i++)
        {
            if (addr[i] != 0)
            {
                isPureV6Loopback = false;
                break;
            }
        }

        if (isPureV6Loopback && addr[15] == 1)
        {
            return true;
        }

        for (var i = 0; i < 10; i++)
        {
            if (addr[i] != 0)
            {
                return false;
            }
        }

        return addr[10] == 0xFF && addr[11] == 0xFF && addr[12] == 127;
    }

    private static bool IsAllDigits(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty)
        {
            return false;
        }

        foreach (var ch in span)
        {
            if (ch is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static int ReadNetworkOrderPort(byte[] buffer, int offset) =>
        (buffer[offset] << 8) | buffer[offset + 1];

    private static uint ReadUInt32(byte[] buffer, int offset) =>
        (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        byte[]? tcpTable,
        ref uint tableSize,
        [MarshalAs(UnmanagedType.Bool)] bool sort,
        uint ipVersion,
        uint tableClass,
        uint reserved);
}
