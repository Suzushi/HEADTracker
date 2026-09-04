using System.Runtime.Versioning;
using HeadTracker.Core.Configuration;
using HeadTracker.Core.Protocol;

namespace HeadTracker.Core;

/// <summary>
/// Fan-out of a tracked pose to every enabled output protocol.
/// Owns the freetrack shared-memory writer and the UDP sender, configured
/// from <see cref="TrackerSettings"/>. Call <see cref="Publish"/> once per
/// pipeline frame; the class is safe to drive from a single pipeline thread.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PosePublisher : IDisposable
{
    private readonly FreeTrackWriter? _freeTrack;
    private readonly UdpSender? _udp;

    public bool FreeTrackActive { get; }
    public bool UdpActive { get; }
    public string ConnectedGame => _freeTrack?.ConnectedGame ?? "";

    public PosePublisher(TrackerSettings settings, string? gameDatabaseCsv = null)
    {
        if (settings.UseFt || settings.UseNpclient)
        {
            _freeTrack = new FreeTrackWriter(gameDatabaseCsv);
            if (_freeTrack.Initialize())
            {
                FreeTrackActive = true;
            }
            else
            {
                _freeTrack.Dispose();
                _freeTrack = null;
            }
        }

        if (settings.SendPosedataUdp)
        {
            _udp = new UdpSender();
            _udp.Configure(settings.UdpHost, settings.Port);
            UdpActive = true;
        }
    }

    public void Publish(in Pose6D pose)
    {
        _freeTrack?.Send(pose);
        _udp?.Send(pose);
    }

    public void Dispose()
    {
        _freeTrack?.Dispose();
        _udp?.Dispose();
    }
}
