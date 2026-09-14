using System.Text.Json;
using System.Text.Json.Nodes;
using Faketorio.Sim.Commands;

namespace Faketorio.Sim.McpServer;

// 操作历史的 JSON 序列化格式——供 Godot 端(game/SimHost.cs 的回放分支,
// Task 4)反序列化。schema:
//   { "seed": <long>, "dataDir": <string>,
//     "entries": [
//       { "isStep": false, "command": { "type": <int>, "protoId": <int>, "x": <int>, "y": <int>, "rotation": <int>, "count": <int> } },
//       { "isStep": true, "stepTicks": <int> },
//       ...
//     ] }
// command.type 是 CommandType 枚举的底层数值(byte 转成的 int),不是字符串——
// Godot 端拿到这个数值直接 (CommandType)value 转回来即可,两边共享同一份
// Faketorio.Sim 程序集里的枚举定义,数值本身就是唯一真源,不需要额外的
// 字符串<->枚举映射表。
internal static class ReplayLogFormat
{
    public static string Serialize(IReadOnlyList<LogEntry> entries, long seed, string dataDir)
    {
        var root = new JsonObject
        {
            ["seed"] = seed,
            ["dataDir"] = dataDir,
        };

        var entriesArray = new JsonArray();
        foreach (var entry in entries)
        {
            var entryObj = new JsonObject { ["isStep"] = entry.IsStep };
            if (entry.IsStep)
            {
                entryObj["stepTicks"] = entry.StepTicks;
            }
            else
            {
                entryObj["command"] = new JsonObject
                {
                    ["type"] = (int)entry.Command.Type,
                    ["protoId"] = entry.Command.ProtoId,
                    ["x"] = entry.Command.X,
                    ["y"] = entry.Command.Y,
                    ["rotation"] = entry.Command.Rotation,
                    ["count"] = entry.Command.Count,
                };
            }
            entriesArray.Add(entryObj);
        }
        root["entries"] = entriesArray;

        return root.ToJsonString();
    }
}
