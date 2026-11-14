using System.Net;
using System.Net.Sockets;
using NightCodeAssay.Infrastructure.Context;

namespace NightCodeAssay.Tests;

public sealed class LocalPortProcessResolverTests
{
    [Theory]
    [InlineData("http://localhost:5270/settings", true, 5270)]
    [InlineData("http://127.0.0.1:8080/api/health", true, 8080)]
    [InlineData("http://[::1]:5000/", true, 5000)]
    [InlineData("https://app.localhost:7001/dashboard", true, 7001)]
    [InlineData("https://localhost/login", true, 443)]
    [InlineData("https://example.com:8080/", false, 0)]
    [InlineData("ftp://localhost:21/files", false, 0)]
    [InlineData("not-a-url", false, 0)]
    public void TryExtractLoopbackPort_RecognizesLoopbackAndRejectsRemoteHosts(
        string url,
        bool expectedSuccess,
        int expectedPort)
    {
        var success = LocalPortProcessResolver.TryExtractLoopbackPort(url, out var port);

        Assert.Equal(expectedSuccess, success);
        Assert.Equal(expectedPort, port);
    }

    [Fact]
    public void MatchListeningPidInTable_ParsesIPv4AndIPv6TablesAndPrefersLoopback()
    {
        // Build a 3-row IPv4 table:
        // Row 0: non-LISTEN state (5 = ESTAB) on port 5270 -> ignored
        // Row 1: LISTEN on 0.0.0.0:5270 -> PID 1111
        // Row 2: LISTEN on 127.0.0.1:5270 -> PID 2222 (preferred loopback)
        var v4Table = new byte[4 + (LocalPortProcessResolver.IPv4RowSize * 3)];
        BitConverter.GetBytes(3).CopyTo(v4Table, 0);

        WriteIPv4Row(v4Table, offset: 4, state: 5, ipBytes: [127, 0, 0, 1], port: 5270, pid: 999);
        WriteIPv4Row(v4Table, offset: 4 + LocalPortProcessResolver.IPv4RowSize, state: LocalPortProcessResolver.MibTcpStateListen, ipBytes: [0, 0, 0, 0], port: 5270, pid: 1111);
        WriteIPv4Row(v4Table, offset: 4 + (LocalPortProcessResolver.IPv4RowSize * 2), state: LocalPortProcessResolver.MibTcpStateListen, ipBytes: [127, 0, 0, 1], port: 5270, pid: 2222);

        Assert.Equal(2222, LocalPortProcessResolver.MatchListeningPidInTable(v4Table, 5270, LocalPortProcessResolver.AfInet));
        Assert.Null(LocalPortProcessResolver.MatchListeningPidInTable(v4Table, 9999, LocalPortProcessResolver.AfInet));

        // Build a 1-row IPv6 table listening on ::1:7123 -> PID 4321
        var v6Table = new byte[4 + LocalPortProcessResolver.IPv6RowSize];
        BitConverter.GetBytes(1).CopyTo(v6Table, 0);
        WriteIPv6Row(v6Table, offset: 4, state: LocalPortProcessResolver.MibTcpStateListen, ipBytes: IPAddress.IPv6Loopback.GetAddressBytes(), port: 7123, pid: 4321);

        Assert.Equal(4321, LocalPortProcessResolver.MatchListeningPidInTable(v6Table, 7123, LocalPortProcessResolver.AfInet6));

        // Prefer non-kernel user PID over System PID 4, and prefer explicit loopback across IPv4 & IPv6.
        var mixedV4Table = new byte[4 + (LocalPortProcessResolver.IPv4RowSize * 2)];
        BitConverter.GetBytes(2).CopyTo(mixedV4Table, 0);
        WriteIPv4Row(mixedV4Table, offset: 4, state: LocalPortProcessResolver.MibTcpStateListen, ipBytes: [127, 0, 0, 1], port: 5000, pid: 4);
        WriteIPv4Row(mixedV4Table, offset: 4 + LocalPortProcessResolver.IPv4RowSize, state: LocalPortProcessResolver.MibTcpStateListen, ipBytes: [0, 0, 0, 0], port: 5000, pid: 3333);

        var mixedV6Table = new byte[4 + LocalPortProcessResolver.IPv6RowSize];
        BitConverter.GetBytes(1).CopyTo(mixedV6Table, 0);
        WriteIPv6Row(mixedV6Table, offset: 4, state: LocalPortProcessResolver.MibTcpStateListen, ipBytes: IPAddress.IPv6Loopback.GetAddressBytes(), port: 5000, pid: 4444);

        Assert.Equal(3333, LocalPortProcessResolver.MatchListeningPidInTable(mixedV4Table, 5000, LocalPortProcessResolver.AfInet));
        Assert.Equal(4444, LocalPortProcessResolver.SelectBestListeningPid(mixedV4Table, mixedV6Table, 5000));
    }

    [Fact]
    public void MatchListeningInodeInProcLines_ParsesLinuxProcNetTcp()
    {
        string[] procLines =
        [
            "  sl  local_address rem_address   st tx_queue:rx_queue tr:tm->when retrnsmt   uid  timeout inode",
            "   0: 00000000:1496 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 123456 1 00000000 100 0 0 10 0",
            "   1: 0100007F:1496 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 987654 1 00000000 100 0 0 10 0",
            "   2: 0200007F:1B58 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 777777 1 00000000 100 0 0 10 0",
            "   3: 00000000:1B58 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 888888 1 00000000 100 0 0 10 0",
            "   4: 0100007F:1F90 0100007F:D431 01 00000000:00000000 00:00000000 00000000  1000        0 555555 1 00000000 100 0 0 10 0"
        ];

        // 0x1496 == 5270, prefers loopback row (0100007F) with inode 987654.
        Assert.Equal(987654L, LocalPortProcessResolver.MatchListeningInodeInProcLines(procLines, 5270));
        // 0x1B58 == 7000, recognizes 127.0.0.2 (0200007F) as loopback ahead of 0.0.0.0.
        Assert.Equal(777777L, LocalPortProcessResolver.MatchListeningInodeInProcLines(procLines, 7000));
        // 0x1F90 == 8080, but state is 01 (ESTABLISHED), not 0A (LISTEN).
        Assert.Null(LocalPortProcessResolver.MatchListeningInodeInProcLines(procLines, 8080));
    }

    [Fact]
    public void TryResolveListeningPid_ResolvesLiveLoopbackTcpListener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var resolver = new LocalPortProcessResolver();
        var resolvedPid = resolver.TryResolveListeningPid(port);

        Assert.Equal(Environment.ProcessId, resolvedPid);
    }

    private static void WriteIPv4Row(byte[] buffer, int offset, int state, byte[] ipBytes, int port, uint pid)
    {
        BitConverter.GetBytes(state).CopyTo(buffer, offset);
        ipBytes.CopyTo(buffer, offset + 4);
        buffer[offset + 8] = (byte)(port >> 8);
        buffer[offset + 9] = (byte)(port & 0xFF);
        BitConverter.GetBytes(pid).CopyTo(buffer, offset + 20);
    }

    private static void WriteIPv6Row(byte[] buffer, int offset, int state, byte[] ipBytes, int port, uint pid)
    {
        ipBytes.CopyTo(buffer, offset);
        buffer[offset + 20] = (byte)(port >> 8);
        buffer[offset + 21] = (byte)(port & 0xFF);
        BitConverter.GetBytes(state).CopyTo(buffer, offset + 48);
        BitConverter.GetBytes(pid).CopyTo(buffer, offset + 52);
    }
}
