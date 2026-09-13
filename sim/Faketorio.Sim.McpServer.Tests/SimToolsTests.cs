using Faketorio.Sim.McpServer;

namespace Faketorio.Sim.McpServer.Tests;

public class SimToolsTests
{
    // 每条测试跑之前，SimHost.Sim 可能残留上一条测试的状态——静态字段在同一个
    // 测试进程内跨测试共享。每条测试自己先调 ResetSimulation 建立已知状态，
    // 不依赖测试执行顺序或初始的 null 状态。

    [Fact]
    public void ResetSimulation_ReturnsTickZeroAndPositivePrototypeCount()
    {
        var result = SimTools.ResetSimulation(seed: 42);

        Assert.Equal(0, result.Tick);
        Assert.True(result.PrototypeCount > 0);
    }

    [Fact]
    public void GetTick_AfterReset_ReturnsZeroTickAndZeroRejected()
    {
        SimTools.ResetSimulation(seed: 1);

        var tick = SimTools.GetTick();

        Assert.Equal(0, tick.Tick);
        Assert.Equal(0, tick.RejectedCommandCount);
    }

    [Fact]
    public void GetTick_BeforeAnyReset_Throws()
    {
        // 这条测试依赖一个全新的、从未 reset 过的 SimHost.Sim 状态，但静态字段
        // 在测试进程内跨测试共享——用一个反射把 SimHost.Sim 显式设回 null，
        // 模拟"进程刚启动、还没人调过 reset_simulation"这个真实场景，不依赖
        // 测试执行顺序。
        typeof(SimHost).GetField(nameof(SimHost.Sim))!.SetValue(null, null);

        var ex = Assert.Throws<InvalidOperationException>(() => SimTools.GetTick());
        Assert.Equal(NotReadyError.Message, ex.Message);
    }

    [Fact]
    public void Step_AdvancesTick()
    {
        SimTools.ResetSimulation(seed: 1);

        var result = SimTools.Step(ticks: 5);

        Assert.Equal(5, result.Tick);
        Assert.Equal(0, result.RejectedCommandCount);
        Assert.Equal(0, result.RejectedThisCall);
    }

    [Fact]
    public void Step_ZeroOrNegativeTicks_Throws()
    {
        SimTools.ResetSimulation(seed: 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => SimTools.Step(ticks: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SimTools.Step(ticks: -1));
    }

    [Fact]
    public void Step_BeforeAnyReset_Throws()
    {
        typeof(SimHost).GetField(nameof(SimHost.Sim))!.SetValue(null, null);

        var ex = Assert.Throws<InvalidOperationException>(() => SimTools.Step(ticks: 1));
        Assert.Equal(NotReadyError.Message, ex.Message);
    }

    [Fact]
    public void SubmitCommand_InvalidType_Throws()
    {
        SimTools.ResetSimulation(seed: 1);

        var ex = Assert.Throws<ArgumentException>(() =>
            SimTools.SubmitCommand(type: "NotARealCommandType", x: 0, y: 0));
        Assert.Contains("PlaceEntity", ex.Message);   // 错误信息要列出合法值
    }

    [Fact]
    public void SubmitCommand_BothProtoIdAndProtoNameGiven_Throws()
    {
        SimTools.ResetSimulation(seed: 1);

        Assert.Throws<ArgumentException>(() =>
            SimTools.SubmitCommand(type: "PlaceEntity", x: 0, y: 0, protoId: 1, protoName: "stone-furnace"));
    }

    [Fact]
    public void SubmitCommand_NeitherProtoIdNorProtoNameGiven_Throws()
    {
        SimTools.ResetSimulation(seed: 1);

        Assert.Throws<ArgumentException>(() =>
            SimTools.SubmitCommand(type: "PlaceEntity", x: 0, y: 0));
    }

    [Fact]
    public void SubmitCommand_WithProtoName_PlacesSameEntityAsProtoId()
    {
        SimTools.ResetSimulation(seed: 1);
        int furnaceId = SimTools.Sim!.Prototypes.Get<Faketorio.Sim.Prototypes.FurnacePrototype>("stone-furnace").Id;

        var result = SimTools.SubmitCommand(type: "PlaceEntity", x: 0, y: 0, protoName: "stone-furnace", rotation: 0);
        SimTools.Step(ticks: 1);

        Assert.True(result.Queued);
        var placed = SimTools.Sim!.World.GetEntityAt(0, 0);
        Assert.True(placed.IsValid);
        Assert.Equal(furnaceId, SimTools.Sim!.Entities.Get(placed).ProtoId);
    }

    [Fact]
    public void SubmitCommand_UnresolvableProtoName_Throws()
    {
        SimTools.ResetSimulation(seed: 1);

        var ex = Assert.Throws<ArgumentException>(() =>
            SimTools.SubmitCommand(type: "PlaceEntity", x: 0, y: 0, protoName: "this-does-not-exist"));
        Assert.Contains("this-does-not-exist", ex.Message);
    }

    [Fact]
    public void GetEntityAt_EmptyTile_ReturnsNull()
    {
        SimTools.ResetSimulation(seed: 1);

        var result = SimTools.GetEntityAt(999, 999);

        Assert.Null(result);
    }

    [Fact]
    public void GetEntityAt_AfterPlacingEntity_ReturnsInfo()
    {
        SimTools.ResetSimulation(seed: 1);
        SimTools.SubmitCommand(type: "PlaceEntity", x: 5, y: 5, protoName: "stone-furnace", rotation: 2);
        SimTools.Step(ticks: 1);

        var result = SimTools.GetEntityAt(5, 5);

        Assert.NotNull(result);
        Assert.Equal("stone-furnace", result!.ProtoName);
        Assert.Equal(5, result.X);
        Assert.Equal(5, result.Y);
        Assert.Equal(2, result.Rotation);
    }

    [Fact]
    public void GetEntityAt_BeforeAnyReset_Throws()
    {
        typeof(SimHost).GetField(nameof(SimHost.Sim))!.SetValue(null, null);

        Assert.Throws<InvalidOperationException>(() => SimTools.GetEntityAt(0, 0));
    }

    [Fact]
    public void GetInventory_EmptyTile_ReturnsNull()
    {
        SimTools.ResetSimulation(seed: 1);

        var result = SimTools.GetInventory(999, 999);

        Assert.Null(result);
    }

    [Fact]
    public void GetInventory_InvalidRoleForEntity_ReturnsNull()
    {
        SimTools.ResetSimulation(seed: 1);
        SimTools.SubmitCommand(type: "PlaceEntity", x: 5, y: 5, protoName: "wooden-chest", rotation: 0);
        SimTools.Step(ticks: 1);

        var result = SimTools.GetInventory(5, 5, role: 99);

        Assert.Null(result);
    }

    [Fact]
    public void GetInventory_ChestWithItems_ListsNonEmptySlots()
    {
        SimTools.ResetSimulation(seed: 1);
        SimTools.SubmitCommand(type: "PlaceEntity", x: 5, y: 5, protoName: "wooden-chest", rotation: 0);
        SimTools.Step(ticks: 1);
        var chestId = SimTools.Sim!.World.GetEntityAt(5, 5);
        int oreId = SimTools.Sim!.Prototypes.Get<Faketorio.Sim.Prototypes.ItemPrototype>("iron-ore").Id;
        SimTools.Sim!.Inventories.Get(SimTools.Sim!.Inventories.GetInventoryId(chestId)).Insert(oreId, 5, 50);

        var result = SimTools.GetInventory(5, 5);

        Assert.NotNull(result);
        Assert.Contains(result!.Slots, s => s.ItemName == "iron-ore" && s.Count == 5);
    }

    [Fact]
    public void ResolvePrototype_KnownName_ReturnsOneMatch()
    {
        SimTools.ResetSimulation(seed: 1);

        var result = SimTools.ResolvePrototype("stone-furnace");

        Assert.Single(result);
        Assert.Equal("stone-furnace", result[0].Name);
        Assert.Equal("FurnacePrototype", result[0].TypeName);
    }

    [Fact]
    public void ResolvePrototype_UnknownName_ReturnsEmptyList()
    {
        SimTools.ResetSimulation(seed: 1);

        var result = SimTools.ResolvePrototype("this-does-not-exist");

        Assert.Empty(result);
    }

    [Fact]
    public void ResolvePrototype_WithTypeNameFilter_NarrowsMatch()
    {
        SimTools.ResetSimulation(seed: 1);

        var result = SimTools.ResolvePrototype("stone-furnace", typeName: "FurnacePrototype");

        Assert.Single(result);
    }

    [Fact]
    public void ResolvePrototype_AmbiguousName_NoFilter_ReturnsAllMatches()
    {
        // "wooden-chest" 在 data/base 里同时注册为 ItemPrototype（物品）、
        // ContainerPrototype（可放置的实体）和 RecipePrototype（配方）——
        // 不加 typeName 过滤应该三个都返回，用来验证"同名多类型都返回"这条行为
        // 真的生效，而不是巧合地只有一个匹配。
        SimTools.ResetSimulation(seed: 1);

        var result = SimTools.ResolvePrototype("wooden-chest");

        Assert.True(result.Count > 1);
    }

    [Fact]
    public void ResolvePrototype_AmbiguousName_WithTypeNameFilter_NarrowsToOne()
    {
        SimTools.ResetSimulation(seed: 1);

        var result = SimTools.ResolvePrototype("wooden-chest", typeName: "ItemPrototype");

        Assert.Single(result);
        Assert.Equal("ItemPrototype", result[0].TypeName);
    }

    [Fact]
    public void ListPrototypes_ReturnsAllLoadedPrototypes()
    {
        SimTools.ResetSimulation(seed: 1);

        var result = SimTools.ListPrototypes();

        Assert.True(result.Count > 0);
        Assert.Contains(result, p => p.Name == "stone-furnace");
    }

    [Fact]
    public void ListPrototypes_WithFilter_OnlyReturnsMatchingType()
    {
        SimTools.ResetSimulation(seed: 1);

        var result = SimTools.ListPrototypes(typeNameFilter: "FurnacePrototype");

        Assert.All(result, p => Assert.Equal("FurnacePrototype", p.TypeName));
        Assert.Contains(result, p => p.Name == "stone-furnace");
    }

    [Fact]
    public void ComputeStateHash_SameSeedSameCommands_ProducesSameHash()
    {
        SimTools.ResetSimulation(seed: 7);
        SimTools.SubmitCommand(type: "PlaceEntity", x: 1, y: 1, protoName: "stone-furnace", rotation: 0);
        SimTools.Step(ticks: 10);
        var hash1 = SimTools.ComputeStateHash();

        SimTools.ResetSimulation(seed: 7);
        SimTools.SubmitCommand(type: "PlaceEntity", x: 1, y: 1, protoName: "stone-furnace", rotation: 0);
        SimTools.Step(ticks: 10);
        var hash2 = SimTools.ComputeStateHash();

        Assert.Equal(hash1.Hash, hash2.Hash);
    }

    [Fact]
    public void ComputeStateHash_BeforeAnyReset_Throws()
    {
        typeof(SimHost).GetField(nameof(SimHost.Sim))!.SetValue(null, null);

        Assert.Throws<InvalidOperationException>(() => SimTools.ComputeStateHash());
    }
}
