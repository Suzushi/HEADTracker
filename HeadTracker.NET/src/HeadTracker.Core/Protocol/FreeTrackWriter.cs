using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace HeadTracker.Core.Protocol;

/// <summary>
/// Writer side of the freetrack/TrackIR shared memory protocol.
///
/// Byte-level replica of the legacy FTHeap layout (fttypes.h):
///   FTData (92 bytes): DataID u32 @0, CamWidth i32 @4, CamHeight i32 @8,
///     Yaw/Pitch/Roll/X/Y/Z f32 @12..32, RawYaw..RawZ f32 @36..56,
///     X1/Y1..X4/Y4 f32 @60..88,
///   then GameID i32 @92, table u8[8] @96, GameID2 i32 @104. Total 108 bytes.
///
/// Field writes use interlocked 32-bit exchanges exactly like the legacy
/// InterlockedExchange store(), so readers that lock the "FT_Mutext" mutex
/// (opentrack, freetrack clients) see consistent fields.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class FreeTrackWriter : IDisposable
{
    public const string HeapName = "FT_SharedMem";
    public const string MutexName = "FT_Mutext"; // legacy typo, kept for compatibility
    public const int HeapSize = 108;

    // FTData field offsets
    private const int OffDataId = 0;
    private const int OffCamWidth = 4;
    private const int OffCamHeight = 8;
    private const int OffYaw = 12;
    private const int OffPitch = 16;
    private const int OffRoll = 20;
    private const int OffX = 24;
    private const int OffY = 28;
    private const int OffZ = 32;
    private const int OffRawYaw = 36;
    private const int OffRawPitch = 40;
    private const int OffRawRoll = 44;
    private const int OffRawX = 48;
    private const int OffRawY = 52;
    private const int OffRawZ = 56;
    // X1..Y4 live at 60..88 and are not written by the tracker.
    private const int OffGameId = 92;
    private const int OffTable = 96;
    private const int OffGameId2 = 104;

    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly Mutex? _mutex; // created for protocol compat; legacy writer does not lock
    private readonly byte* _ptr;
    private readonly string? _gameDatabaseCsv;

    private int _lastGameId = -1;

    public bool Success { get; }
    public string ConnectedGame { get; private set; } = "";

    public FreeTrackWriter(string? gameDatabaseCsv = null)
    {
        _gameDatabaseCsv = gameDatabaseCsv;

        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? view = null;
        Mutex? mutex = null;
        byte* ptr = null;

        try
        {
            mutex = new Mutex(false, MutexName);
            mmf = MemoryMappedFile.CreateOrOpen(HeapName, HeapSize, MemoryMappedFileAccess.ReadWrite);
            view = mmf.CreateViewAccessor(0, HeapSize, MemoryMappedFileAccess.ReadWrite);
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            Success = true;
        }
        catch (Exception)
        {
            view?.Dispose();
            mmf?.Dispose();
            mutex?.Dispose();
            Success = false;
        }

        _mmf = mmf;
        _view = view;
        _mutex = mutex;
        _ptr = ptr;
    }

    /// <summary>Mirrors freetrack::initialize(): header fields + zeroed game block.</summary>
    public bool Initialize()
    {
        if (!Success)
        {
            return false;
        }

        StoreInt(OffDataId, 1);
        StoreInt(OffCamWidth, 100);
        StoreInt(OffCamHeight, 250);
        StoreInt(OffGameId2, 0);
        StoreInt(OffTable, 0);
        StoreInt(OffTable + 4, 0);
        return true;
    }

    /// <summary>
    /// Publish one pose. Applies both legacy conversion stages:
    /// the PoseDataSender axis remap (roll inverted, T swapped and scaled by 100)
    /// followed by freetrack::pose() unit conversion (deg->rad, x10, signs).
    /// </summary>
    public void Send(in Pose6D pose)
    {
        if (!Success)
        {
            return;
        }

        const double d2r = Math.PI / 180.0;

        // Stage 1: legacy PoseDataSender::on_pose6d_data freetrack branch.
        var tx = -pose.Ty * 100.0;
        var ty = -pose.Tz * 100.0;
        var tz = -pose.Tx * 100.0;
        var yawDeg = pose.Yaw;
        var pitchDeg = pose.Pitch;
        var rollDeg = -pose.Roll;

        // Stage 2: legacy freetrack::pose().
        var yaw = (float)(-yawDeg * d2r);
        var roll = (float)(rollDeg * d2r);

        // HACK: Falcon BMS makes a "bump" if pitch crosses 90 degrees.
        var isCrossing90 = Math.Abs(pitchDeg - 90.0) < 0.15;
        var pitch = (float)(-d2r * (isCrossing90 ? 89.86 : pitchDeg));

        StoreFloat(OffX, (float)(tx * 10.0));
        StoreFloat(OffY, (float)(ty * 10.0));
        StoreFloat(OffZ, (float)(tz * 10.0));
        StoreFloat(OffYaw, yaw);
        StoreFloat(OffPitch, pitch);
        StoreFloat(OffRoll, roll);

        // Raw values use the same data array; legacy signs differ for pitch.
        StoreFloat(OffRawYaw, (float)(-yawDeg * d2r));
        StoreFloat(OffRawPitch, (float)(pitchDeg * d2r));
        StoreFloat(OffRawRoll, (float)(rollDeg * d2r));
        StoreFloat(OffRawX, (float)(tx * 10.0));
        StoreFloat(OffRawY, (float)(ty * 10.0));
        StoreFloat(OffRawZ, (float)(tz * 10.0));

        var gameId = LoadInt(OffGameId);
        if (gameId != _lastGameId)
        {
            OnGameChanged(gameId);
        }
        else
        {
            Interlocked.Increment(ref *(int*)(_ptr + OffDataId));
        }
    }

    private void OnGameChanged(int gameId)
    {
        var table = new byte[8];
        var gameName = "";

        if (_gameDatabaseCsv != null &&
            GameDatabase.TryGetGame(gameId, _gameDatabaseCsv, out var entry))
        {
            table = entry.Table;
            gameName = entry.Name;
        }

        fixed (byte* tablePtr = table)
        {
            StoreInt(OffTable, *(int*)tablePtr);
            StoreInt(OffTable + 4, *(int*)(tablePtr + 4));
        }

        StoreInt(OffGameId2, gameId);
        StoreInt(OffDataId, 0);

        _lastGameId = gameId;
        ConnectedGame = gameName.Length > 0 ? gameName : "Unknown game";
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StoreFloat(int offset, float value)
        => Interlocked.Exchange(ref *(int*)(_ptr + offset), BitConverter.SingleToInt32Bits(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StoreInt(int offset, int value)
        => Interlocked.Exchange(ref *(int*)(_ptr + offset), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int LoadInt(int offset)
        => Interlocked.CompareExchange(ref *(int*)(_ptr + offset), 0, 0);

    public void Dispose()
    {
        if (_ptr != null && _view != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
        }

        _view?.Dispose();
        _mmf?.Dispose();
        _mutex?.Dispose();
    }
}
