using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MeshCore.Windows.Core;

public sealed record DiscoveryProgress(int Attempted, int Total, int Failed, bool Waiting);

public static class WifiDiscovery
{
    private static readonly byte[] Probe = "MESHCORE_DISCOVER_V1"u8.ToArray();
    private static readonly byte[] Reply = "MESHCORE_V4_COMPANION_V1"u8.ToArray();

    /// <summary>Choose a bounded scan range within the routed adapter's subnet.</summary>
    public static string SuggestedSubnet(IPAddress address, int prefix)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork || prefix is < 0 or > 32) throw new ArgumentException("Invalid IPv4 network.");
        prefix = Math.Max(prefix, 22); // Keep discovery within the supported 1,024-address limit.
        uint ip = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        uint mask = uint.MaxValue << (32 - prefix);
        byte[] network = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(network, ip & mask);
        return new IPAddress(network) + "/" + prefix;
    }

    public static IReadOnlyList<IPAddress> Hosts(string subnet)
    {
        var parts = subnet.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], out int prefix) || prefix is < 22 or > 32) throw new ArgumentException("Enter an IPv4 subnet with at most 1024 addresses.", nameof(subnet));
        uint ip = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        uint first = ip & mask, last = first | ~mask;
        if (prefix <= 30) { first++; last--; }
        var result = new List<IPAddress>();
        for (ulong n = first; n <= last; n++)
        {
            byte[] bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)n);
            result.Add(new IPAddress(bytes));
        }
        return result;
    }

    public static async Task<IReadOnlyList<IPAddress>> ScanAsync(string subnet, IProgress<DiscoveryProgress>? progress = null,
        int port = 5010, TimeSpan? responseWait = null, CancellationToken cancellationToken = default)
    {
        var hosts = Hosts(subnet);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        if (OperatingSystem.IsWindows())
            socket.Client.IOControl((IOControlCode)(-1744830452), new byte[4], null); // SIO_UDP_CONNRESET = false
        var allowed = hosts.ToHashSet();
        var found = new HashSet<IPAddress>();
        using var receiveStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task ReceiveRepliesAsync()
        {
            try
            {
                while (true)
                {
                    try
                    {
                        var result = await socket.ReceiveAsync(receiveStop.Token);
                        if (result.Buffer.AsSpan().SequenceEqual(Reply) && allowed.Contains(result.RemoteEndPoint.Address)) found.Add(result.RemoteEndPoint.Address);
                    }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset) { }
                }
            }
            catch (OperationCanceledException) when (receiveStop.IsCancellationRequested) { }
        }
        Task receiver = ReceiveRepliesAsync();
        try
        {
            int attempted = 0, failed = 0;
            foreach (var host in hosts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { await socket.SendAsync(Probe, new IPEndPoint(host, port), cancellationToken); }
                catch (SocketException) { failed++; }
                attempted++;
                progress?.Report(new DiscoveryProgress(attempted, hosts.Count, failed, false));
            }
            if (attempted > 0 && failed == attempted) throw new IOException("Could not send any discovery probes.");
            progress?.Report(new DiscoveryProgress(attempted, hosts.Count, failed, true));
            await Task.Delay(responseWait ?? TimeSpan.FromSeconds(5), cancellationToken);
        }
        finally { receiveStop.Cancel(); await receiver; }
        return found.OrderBy(ip => BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes())).ToArray();
    }
}
