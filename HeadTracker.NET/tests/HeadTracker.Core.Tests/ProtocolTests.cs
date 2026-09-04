using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using HeadTracker.Core;
using HeadTracker.Core.Protocol;

namespace HeadTracker.Core.Tests;

[SupportedOSPlatform("windows")]
public class FreeTrackWriterTests : IDisposable
{
    private const double D2R = Math.PI / 180.0;

    private static byte[] ReadHeap()
    {
        // Independent reader view, like opentrack/freetrack clients would open.
        using var mmf = MemoryMappedFile.OpenExisting(FreeTrackWriter.HeapName, MemoryMappedFileRights.Read);
        var bytes = new byte[FreeTrackWriter.HeapSize];
        using var view = mmf.CreateViewAccessor(0, FreeTrackWriter.HeapSize, MemoryMappedFileAccess.Read);
        view.ReadArray(0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float FloatAt(byte[] heap, int offset) => BitConverter.ToSingle(heap, offset);
    private static int IntAt(byte[] heap, int offset) => BitConverter.ToInt32(heap, offset);

    [Fact]
    public void Heap_Layout_MatchesLegacyOffsets()
    {
        Assert.Equal(108, FreeTrackWriter.HeapSize);

        using var writer = new FreeTrackWriter();
        Assert.True(writer.Success);
        Assert.True(writer.Initialize());

        var heap = ReadHeap();
        Assert.Equal(1, IntAt(heap, 0));      // DataID
        Assert.Equal(100, IntAt(heap, 4));    // CamWidth
        Assert.Equal(250, IntAt(heap, 8));    // CamHeight
        Assert.Equal(0, IntAt(heap, 104));    // GameID2
        Assert.Equal(0, IntAt(heap, 96));     // table
        Assert.Equal(0, IntAt(heap, 100));
    }

    [Fact]
    public void Send_AppliesLegacyAxisRemapAndUnitConversion()
    {
        using var writer = new FreeTrackWriter();
        writer.Initialize();

        // Internal pose: yaw 30, pitch 10, roll 5 deg; T = (1, 2, 3).
        writer.Send(new Pose6D(30, 10, 5, 1, 2, 3));
        writer.Send(new Pose6D(30, 10, 5, 1, 2, 3)); // second send: same game id, no reset

        var heap = ReadHeap();

        // Stage1: tx=-Ty*100=-200, ty=-Tz*100=-300, tz=-Tx*100=-100; roll -> -5.
        // Stage2: *10 for translation, deg->rad with legacy signs.
        Assert.Equal(-2000f, FloatAt(heap, 24), 4);   // X
        Assert.Equal(-3000f, FloatAt(heap, 28), 4);   // Y
        Assert.Equal(-1000f, FloatAt(heap, 32), 4);   // Z
        Assert.Equal((float)(-30 * D2R), FloatAt(heap, 12), 6); // Yaw
        Assert.Equal((float)(-10 * D2R), FloatAt(heap, 16), 6); // Pitch
        Assert.Equal((float)(-5 * D2R), FloatAt(heap, 20), 6);  // Roll (-roll -> -5, *d2r)

        // Raw fields: same values but legacy RawPitch keeps positive sign.
        Assert.Equal((float)(-30 * D2R), FloatAt(heap, 36), 6); // RawYaw
        Assert.Equal((float)(+10 * D2R), FloatAt(heap, 40), 6); // RawPitch
        Assert.Equal((float)(-5 * D2R), FloatAt(heap, 44), 6);  // RawRoll
        Assert.Equal(-2000f, FloatAt(heap, 48), 4);             // RawX
    }

    [Fact]
    public void Send_ClampsPitchNearNinety()
    {
        using var writer = new FreeTrackWriter();
        writer.Initialize();

        writer.Send(new Pose6D(0, 90.0, 0, 0, 0, 0));
        writer.Send(new Pose6D(0, 90.0, 0, 0, 0, 0));

        var heap = ReadHeap();
        Assert.Equal((float)(-89.86 * D2R), FloatAt(heap, 16), 6);
    }

    [Fact]
    public void Send_IncrementsDataId_WhenGameUnchanged()
    {
        using var writer = new FreeTrackWriter();
        writer.Initialize();

        writer.Send(Pose6D.Zero); // game id 0 first seen: resets DataID to 0
        writer.Send(Pose6D.Zero); // +1
        writer.Send(Pose6D.Zero); // +1

        var heap = ReadHeap();
        Assert.Equal(2, IntAt(heap, 0));
    }

    [Fact]
    public void Send_LooksUpGameTable_WhenGameIdChanges()
    {
        var csv = Path.Combine(Path.GetTempPath(), $"headtracker_games_{Guid.NewGuid():N}.csv");
        // 22-char hex id: bytes AB CD EF 12 34 56 78 90 AB CD EF
        File.WriteAllText(csv,
            "No.;Game Name;Game Protocol;Supported since;Verified;By;International ID;FaceTrackNoIR ID\n" +
            "1;Test Game;FT;V161;yes;me;1234;ABCDEF1234567890ABCDEF\n");
        try
        {
            using var writer = new FreeTrackWriter(csv);
            writer.Initialize();

            // Simulate a game writing its id into the heap (offset 92).
            using (var mmf = MemoryMappedFile.OpenExisting(FreeTrackWriter.HeapName, MemoryMappedFileRights.ReadWrite))
            using (var view = mmf.CreateViewAccessor(0, FreeTrackWriter.HeapSize, MemoryMappedFileAccess.ReadWrite))
            {
                view.Write(92, 1234);
            }

            writer.Send(Pose6D.Zero);

            var heap = ReadHeap();
            Assert.Equal(1234, IntAt(heap, 104));              // GameID2 mirrors GameID
            Assert.Equal(0, IntAt(heap, 0));                   // DataID reset on game change
            // table = tmp[0..7] with the legacy sscanf shuffle
            byte[] expectedTable = [0x56, 0x34, 0x12, 0xEF, 0xCD, 0xAB, 0x90, 0x78];
            for (var i = 0; i < 8; i++)
            {
                Assert.Equal(expectedTable[i], heap[96 + i]);
            }

            Assert.Equal("Test Game", writer.ConnectedGame);
        }
        finally
        {
            File.Delete(csv);
        }
    }

    public void Dispose()
    {
        // Named kernel objects are cleaned by writer disposal; nothing global to reset.
    }
}

public class UdpSenderTests
{
    [Fact]
    public void BuildPayload_MatchesLegacyDoubleLayout()
    {
        var pose = new Pose6D(30, 10, 5, 1, 2, 3);
        var payload = UdpSender.BuildPayload(pose);

        Assert.Equal(48, payload.Length);

        var expected = new[] { -200.0, -300.0, -100.0, 30.0, 10.0, -5.0 };
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(expected[i], BitConverter.ToDouble(payload, i * 8), 10);
        }
    }

    [Fact]
    public void Send_DeliversDatagram_ToConfiguredEndpoint()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        listener.Client.ReceiveTimeout = 5000;

        using var sender = new UdpSender();
        sender.Configure("127.0.0.1", port);
        sender.Send(new Pose6D(1, 2, 3, 4, 5, 6));

        var remote = new IPEndPoint(IPAddress.Any, 0);
        var data = listener.Receive(ref remote);

        Assert.Equal(48, data.Length);
        Assert.Equal(-500.0, BitConverter.ToDouble(data, 0), 10); // -Ty*100
        Assert.Equal(-600.0, BitConverter.ToDouble(data, 8), 10); // -Tz*100
        Assert.Equal(-400.0, BitConverter.ToDouble(data, 16), 10); // -Tx*100
        Assert.Equal(1.0, BitConverter.ToDouble(data, 24), 10);   // Yaw
        Assert.Equal(2.0, BitConverter.ToDouble(data, 32), 10);   // Pitch
        Assert.Equal(-3.0, BitConverter.ToDouble(data, 40), 10);  // -Roll
    }
}

public class GameDatabaseTests
{
    private static string WriteCsv(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"headtracker_games_{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void TryGetGame_FindsById_AndShufflesTable()
    {
        var csv = WriteCsv(
            "No.;Game Name;Game Protocol;Supported since;Verified;By;International ID;FaceTrackNoIR ID",
            "1;Test Game;FT;V161;yes;me;1234;ABCDEF1234567890ABCDEF");
        try
        {
            Assert.True(GameDatabase.TryGetGame(1234, csv, out var entry));
            Assert.Equal("Test Game", entry.Name);
            Assert.Equal(
                new byte[] { 0x56, 0x34, 0x12, 0xEF, 0xCD, 0xAB, 0x90, 0x78 },
                entry.Table);
        }
        finally
        {
            File.Delete(csv);
        }
    }

    [Fact]
    public void TryGetGame_V160Protocol_KeepsZeroTable()
    {
        var csv = WriteCsv(
            "No.;Game Name;Game Protocol;Supported since;Verified;By;International ID;FaceTrackNoIR ID",
            "1;V160 Game;FT;V160;yes;me;777;ABCDEF1234567890ABCDEF");
        try
        {
            Assert.True(GameDatabase.TryGetGame(777, csv, out var entry));
            Assert.Equal("V160 Game", entry.Name);
            Assert.Equal(new byte[8], entry.Table);
        }
        finally
        {
            File.Delete(csv);
        }
    }

    [Fact]
    public void TryGetGame_UnknownId_ReturnsFalse()
    {
        var csv = WriteCsv(
            "No.;Game Name;Game Protocol;Supported since;Verified;By;International ID;FaceTrackNoIR ID",
            "1;Test Game;FT;V161;yes;me;1234;ABCDEF1234567890ABCDEF");
        try
        {
            Assert.False(GameDatabase.TryGetGame(9999, csv, out _));
        }
        finally
        {
            File.Delete(csv);
        }
    }
}
