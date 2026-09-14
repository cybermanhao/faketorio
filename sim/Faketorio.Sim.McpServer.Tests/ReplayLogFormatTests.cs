using System.Text.Json;
using Faketorio.Sim.Commands;
using Faketorio.Sim.McpServer;

namespace Faketorio.Sim.McpServer.Tests;

public class ReplayLogFormatTests
{
    [Fact]
    public void Serialize_EmptyLog_ProducesValidJsonWithSeedAndDataDir()
    {
        string json = ReplayLogFormat.Serialize(Array.Empty<LogEntry>(), seed: 99, dataDir: "data/base");

        using var doc = JsonDocument.Parse(json);   // 不抛异常就是合法 JSON
        Assert.Equal(99, doc.RootElement.GetProperty("seed").GetInt64());
        Assert.Equal("data/base", doc.RootElement.GetProperty("dataDir").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public void Serialize_CommandEntry_RoundTripsAllFields()
    {
        var command = new Command { Type = CommandType.PlaceEntity, ProtoId = 7, X = 3, Y = -4, Rotation = 2, Count = 0 };
        var entries = new[] { new LogEntry(IsStep: false, Command: command, StepTicks: 0) };

        string json = ReplayLogFormat.Serialize(entries, seed: 1, dataDir: "data/base");
        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("entries")[0];

        Assert.False(entry.GetProperty("isStep").GetBoolean());
        var cmd = entry.GetProperty("command");
        Assert.Equal((int)CommandType.PlaceEntity, cmd.GetProperty("type").GetInt32());
        Assert.Equal(7, cmd.GetProperty("protoId").GetInt32());
        Assert.Equal(3, cmd.GetProperty("x").GetInt32());
        Assert.Equal(-4, cmd.GetProperty("y").GetInt32());
        Assert.Equal(2, cmd.GetProperty("rotation").GetInt32());
        Assert.Equal(0, cmd.GetProperty("count").GetInt32());
    }

    [Fact]
    public void Serialize_StepEntry_HasIsStepTrueAndStepTicks()
    {
        var entries = new[] { new LogEntry(IsStep: true, Command: default, StepTicks: 42) };

        string json = ReplayLogFormat.Serialize(entries, seed: 1, dataDir: "data/base");
        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("entries")[0];

        Assert.True(entry.GetProperty("isStep").GetBoolean());
        Assert.Equal(42, entry.GetProperty("stepTicks").GetInt32());
    }

    [Fact]
    public void Serialize_MultipleEntries_PreservesOrder()
    {
        var entries = new[]
        {
            new LogEntry(false, new Command { Type = CommandType.PlaceEntity, ProtoId = 1, X = 0, Y = 0 }, 0),
            new LogEntry(true, default, 5),
            new LogEntry(false, new Command { Type = CommandType.RotateEntity, ProtoId = 0, X = 0, Y = 0, Rotation = 1 }, 0),
        };

        string json = ReplayLogFormat.Serialize(entries, seed: 1, dataDir: "data/base");
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.GetProperty("entries");

        Assert.Equal(3, arr.GetArrayLength());
        Assert.False(arr[0].GetProperty("isStep").GetBoolean());
        Assert.True(arr[1].GetProperty("isStep").GetBoolean());
        Assert.False(arr[2].GetProperty("isStep").GetBoolean());
        Assert.Equal((int)CommandType.RotateEntity, arr[2].GetProperty("command").GetProperty("type").GetInt32());
    }
}
