using System.Net;
using System.Net.Sockets;

namespace HeadTracker.Core.Protocol;

/// <summary>
/// Legacy-compatible UDP pose output: one little-endian double[6] datagram
/// (48 bytes) per pose, replicating PoseDataSender::send_data_udp:
///   [ -Ty*100, -Tz*100, -Tx*100, Yaw, Pitch, -Roll ].
/// </summary>
public sealed class UdpSender : IDisposable
{
    private readonly UdpClient _client = new();
    private IPEndPoint _endpoint = new(IPAddress.Loopback, 4242);

    public void Configure(string host, int port)
    {
        if (!IPAddress.TryParse(host, out var address))
        {
            var entries = Dns.GetHostAddresses(host);
            address = entries.Length > 0 ? entries[0] : IPAddress.Loopback;
        }

        _endpoint = new IPEndPoint(address, port);
    }

    public void Send(in Pose6D pose)
    {
        Span<byte> buffer = stackalloc byte[6 * sizeof(double)];
        WriteDouble(buffer, 0, -pose.Ty * 100.0);
        WriteDouble(buffer, 1, -pose.Tz * 100.0);
        WriteDouble(buffer, 2, -pose.Tx * 100.0);
        WriteDouble(buffer, 3, pose.Yaw);
        WriteDouble(buffer, 4, pose.Pitch);
        WriteDouble(buffer, 5, -pose.Roll);

        _client.Send(buffer, _endpoint);
    }

    /// <summary>Produces the raw 48-byte payload; exposed for regression tests.</summary>
    public static byte[] BuildPayload(in Pose6D pose)
    {
        var buffer = new byte[6 * sizeof(double)];
        WriteDouble(buffer, 0, -pose.Ty * 100.0);
        WriteDouble(buffer, 1, -pose.Tz * 100.0);
        WriteDouble(buffer, 2, -pose.Tx * 100.0);
        WriteDouble(buffer, 3, pose.Yaw);
        WriteDouble(buffer, 4, pose.Pitch);
        WriteDouble(buffer, 5, -pose.Roll);
        return buffer;
    }

    private static void WriteDouble(Span<byte> buffer, int index, double value)
        => BitConverter.TryWriteBytes(buffer.Slice(index * sizeof(double), sizeof(double)), value);

    public void Dispose() => _client.Dispose();
}
