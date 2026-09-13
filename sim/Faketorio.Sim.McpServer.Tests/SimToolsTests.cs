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

        var ex = Assert.Throws<ModelContextProtocol.McpException>(() => SimTools.GetTick());
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

        Assert.Throws<ModelContextProtocol.McpException>(() => SimTools.Step(ticks: 0));
        Assert.Throws<ModelContextProtocol.McpException>(() => SimTools.Step(ticks: -1));
    }

    [Fact]
    public void Step_BeforeAnyReset_Throws()
    {
        typeof(SimHost).GetField(nameof(SimHost.Sim))!.SetValue(null, null);

        var ex = Assert.Throws<ModelContextProtocol.McpException>(() => SimTools.Step(ticks: 1));
        Assert.Equal(NotReadyError.Message, ex.Message);
    }

    [Fact]
    public void SubmitCommand_InvalidType_Throws()
    {
        SimTools.ResetSimulation(seed: 1);

        var ex = Assert.Throws<ModelContextProtocol.McpException>(() =>
            SimTools.SubmitCommand(type: "NotARealCommandType", x: 0, y: 0));
        Assert.Contains("PlaceEntity", ex.Message);   // 错误信息要列出合法值
    }

    [Fact]
    public void SubmitCommand_BothProtoIdAndProtoNameGiven_Throws()
    {
        SimTools.ResetSimulation(seed: 1);

        Assert.Throws<ModelContextProtocol.McpException>(() =>
            SimTools.SubmitCommand(type: "PlaceEntity", x: 0, y: 0, protoId: 1, protoName: "stone-furnace"));
    }

    [Fact]
    public void SubmitCommand_NeitherProtoIdNorProtoNameGiven_Throws()
    {
        SimTools.ResetSimulation(seed: 1);

        Assert.Throws<ModelContextProtocol.McpException>(() =>
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

        var ex = Assert.Throws<ModelContextProtocol.McpException>(() =>
            SimTools.SubmitCommand(type: "PlaceEntity", x: 0, y: 0, protoName: "this-does-not-exist"));
        Assert.Contains("this-does-not-exist", ex.Message);
    }

    [Fact]
    public void SubmitCommand_AmbiguousProtoName_Throws()
    {
        // "wooden-chest" 同名匹配 ItemPrototype/ContainerPrototype/RecipePrototype
        // 三个不同类型的原型（同 ResolvePrototype_AmbiguousName_NoFilter_ReturnsAllMatches
        // 用的场景）。protoName 解析必须在这种情况下报错而不是悄悄挑一个，否则
        // 命令可能用错的 ProtoId 提交，行为诡异且无提示。
        SimTools.ResetSimulation(seed: 1);

        var ex = Assert.Throws<ModelContextProtocol.McpException>(() =>
            SimTools.SubmitCommand(type: "PlaceEntity", x: 0, y: 0, protoName: "wooden-chest"));
        Assert.Contains("歧义", ex.Message);
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

        Assert.Throws<ModelContextProtocol.McpException>(() => SimTools.GetEntityAt(0, 0));
    }

    [Fact]
    public void GetInventory_BeforeAnyReset_Throws()
    {
        typeof(SimHost).GetField(nameof(SimHost.Sim))!.SetValue(null, null);

        Assert.Throws<ModelContextProtocol.McpException>(() => SimTools.GetInventory(0, 0));
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
        // "wooden-chest" 现在同名匹配 3 种原型类型，protoName 会因为歧义报错——
        // 这里用 protoId 指定确切想要的 ContainerPrototype，避开歧义检查。
        int chestProtoId = SimTools.Sim!.Prototypes.Get<Faketorio.Sim.Prototypes.ContainerPrototype>("wooden-chest").Id;
        SimTools.SubmitCommand(type: "PlaceEntity", x: 5, y: 5, protoId: chestProtoId, rotation: 0);
        SimTools.Step(ticks: 1);

        var result = SimTools.GetInventory(5, 5, role: 99);

        Assert.Null(result);
    }

    [Fact]
    public void GetInventory_ChestWithItems_ListsNonEmptySlots()
    {
        SimTools.ResetSimulation(seed: 1);
        int chestProtoId = SimTools.Sim!.Prototypes.Get<Faketorio.Sim.Prototypes.ContainerPrototype>("wooden-chest").Id;
        SimTools.SubmitCommand(type: "PlaceEntity", x: 5, y: 5, protoId: chestProtoId, rotation: 0);
        SimTools.Step(ticks: 1);
        var chestId = SimTools.Sim!.World.GetEntityAt(5, 5);
        int oreId = SimTools.Sim!.Prototypes.Get<Faketorio.Sim.Prototypes.ItemPrototype>("iron-ore").Id;
        SimTools.Sim!.Inventories.Get(SimTools.Sim!.Inventories.GetInventoryId(chestId)).Insert(oreId, 5, 50);

        var result = SimTools.GetInventory(5, 5);

        Assert.NotNull(result);
        Assert.Contains(result!.Slots, s => s.ItemName == "iron-ore" && s.Count == 5);
    }

    [Fact]
    public void ResolvePrototype_BeforeAnyReset_Throws()
    {
        typeof(SimHost).GetField(nameof(SimHost.Sim))!.SetValue(null, null);

        Assert.Throws<ModelContextProtocol.McpException>(() => SimTools.ResolvePrototype("stone-furnace"));
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
    public void ListPrototypes_BeforeAnyReset_Throws()
    {
        typeof(SimHost).GetField(nameof(SimHost.Sim))!.SetValue(null, null);

        Assert.Throws<ModelContextProtocol.McpException>(() => SimTools.ListPrototypes());
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

        Assert.Throws<ModelContextProtocol.McpException>(() => SimTools.ComputeStateHash());
    }

    [Fact]
    public void EndToEnd_PoweredFurnaceSmeltsOre_ThroughMcpToolsOnly()
    {
        SimTools.ResetSimulation(seed: 0);

        // 电线杆 (0,0) + 发电机 (2,0)，同 SimulationTests.PlacePoweredMachineInfra
        // 的坐标关系。
        SimTools.SubmitCommand(type: "PlaceEntity", x: 0, y: 0, protoName: "small-electric-pole", rotation: 0);
        SimTools.SubmitCommand(type: "PlaceEntity", x: 2, y: 0, protoName: "burner-generator", rotation: 0);
        SimTools.Step(ticks: 1);

        // 给发电机塞煤——TransferToEntity 命令要求玩家背包里先有煤，
        // 这条端到端测试直接往 Sim.Player.Inventory 塞（跟既有
        // SimulationTests.PlacePoweredMachineInfra 的做法一致），因为
        // "往玩家背包塞初始物品"本身不是这 9 个工具要覆盖的场景（玩家背包
        // 管理不在这轮 MCP 工具范围内，spec §4 没有把它列进来）。
        int coalId = SimTools.Sim!.Prototypes.Get<Faketorio.Sim.Prototypes.ItemPrototype>("coal").Id;
        int coalStack = SimTools.Sim!.Prototypes.Get<Faketorio.Sim.Prototypes.ItemPrototype>("coal").StackSize;
        SimTools.Sim!.Player.Inventory.Insert(coalId, 5, coalStack);
        SimTools.SubmitCommand(type: "TransferToEntity", x: 2, y: 0, protoId: coalId, count: 5);
        SimTools.Step(ticks: 1);

        // 熔炉 (0,2)，在电线杆 Chebyshev 距离 2 以内。
        SimTools.SubmitCommand(type: "PlaceEntity", x: 0, y: 2, protoName: "stone-furnace", rotation: 0);
        SimTools.Step(ticks: 1);

        var furnace = SimTools.GetEntityAt(0, 2);
        Assert.NotNull(furnace);

        // 直接往熔炉输入库存(role 1)塞矿——用 get_inventory 确认库存形状后
        // 手动操纵，因为"隔空塞矿"本身不是这 9 个工具要覆盖的场景，
        // TransferToEntity 走的是玩家背包->实体这条路，这里为了让测试独立于
        // 玩家背包细节，直接摆状态（同既有 SimulationTests 里
        // Furnace_AutoMatchesAndSmeltsIronOre 的做法）。
        int oreId = SimTools.Sim!.Prototypes.Get<Faketorio.Sim.Prototypes.ItemPrototype>("iron-ore").Id;
        int oreStack = SimTools.Sim!.Prototypes.Get<Faketorio.Sim.Prototypes.ItemPrototype>("iron-ore").StackSize;
        var furnaceInputInv = SimTools.Sim!.Inventories.Get(SimTools.Sim!.Inventories.GetInventoryId(
            SimTools.Sim!.World.GetEntityAt(0, 2), role: 1));
        furnaceInputInv.Insert(oreId, 1, oreStack);
        SimTools.Sim!.Machines.MarkAwake(SimTools.Sim!.World.GetEntityAt(0, 2));   // 直接操纵输入库存,按既有测试约定显式唤醒

        // iron-plate 是 192 tick，给够余量。
        SimTools.Step(ticks: 200);

        var output = SimTools.GetInventory(0, 2, role: 2);
        Assert.NotNull(output);
        Assert.Contains(output!.Slots, s => s.ItemName == "iron-plate" && s.Count >= 1);

        var hash = SimTools.ComputeStateHash();
        Assert.Matches("^0x[0-9A-F]{16}$", hash.Hash);   // 只是确认这个工具跑得通、格式正确，不对拍具体数值
    }
}
