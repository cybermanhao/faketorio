using Faketorio.Sim.Belts;
using Faketorio.Sim.Commands;
using Faketorio.Sim.Electric;
using Faketorio.Sim.Entities;
using Faketorio.Sim.Items;
using Faketorio.Sim.Prototypes;

namespace Faketorio.Sim.Tests;

public class SimulationTests
{
    private static Simulation NewSim()
        => new(PrototypeLoader.LoadFromDirectory("data/base"));

    private static Command PlaceChest(Simulation sim, int x, int y) => new()
    {
        Type = CommandType.PlaceEntity,
        ProtoId = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id,
        X = x, Y = y, Rotation = 0,
    };

    private static Command PlaceLargeChest(Simulation sim, int x, int y) => new()
    {
        Type = CommandType.PlaceEntity,
        ProtoId = sim.Prototypes.Get<ContainerPrototype>("large-chest").Id,
        X = x, Y = y, Rotation = 0,
    };

    [Fact]
    public void Command_NotAppliedUntilNextStep()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 3, 4));
        Assert.Equal(EntityId.Invalid, sim.World.GetEntityAt(3, 4)); // 尚未应用
        sim.Step();
        Assert.True(sim.World.GetEntityAt(3, 4).IsValid);
    }

    [Fact]
    public void Step_IncrementsTick()
    {
        var sim = NewSim();
        Assert.Equal(0, sim.Tick);
        sim.Step();
        sim.Step();
        Assert.Equal(2, sim.Tick);
    }

    [Fact]
    public void PlacedEntity_HasCorrectData()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 3, 4));
        sim.Step();
        var id = sim.World.GetEntityAt(3, 4);
        ref var data = ref sim.Entities.Get(id);
        Assert.Equal(3, data.X);
        Assert.Equal(4, data.Y);
        Assert.Equal("wooden-chest", sim.Prototypes.GetById(data.ProtoId).Name);
    }

    [Fact]
    public void PlaceOnOccupiedTile_IsRejected()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 0, 0));
        sim.Submit(PlaceChest(sim, 0, 0)); // 同 tick 第二个,应被拒
        sim.Step();
        Assert.Equal(1, sim.RejectedCommandCount);
    }

    [Fact]
    public void RemoveEntity_FreesTilesAndPool()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 0, 0));
        sim.Step();
        var id = sim.World.GetEntityAt(0, 0);
        sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 0, Y = 0 });
        sim.Step();
        Assert.Equal(EntityId.Invalid, sim.World.GetEntityAt(0, 0));
        Assert.False(sim.Entities.IsAlive(id));
    }

    [Fact]
    public void RemoveOnEmptyTile_IsRejected()
    {
        var sim = NewSim();
        sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 9, Y = 9 });
        sim.Step();
        Assert.Equal(1, sim.RejectedCommandCount);
    }

    [Fact]
    public void PlaceEntity_WithOutOfRangeProtoId_IsRejected()
    {
        var sim = NewSim();
        sim.Submit(new Command
        {
            Type = CommandType.PlaceEntity,
            ProtoId = int.MaxValue, // Out of range
            X = 0, Y = 0, Rotation = 0,
        });
        sim.Step();
        Assert.Equal(1, sim.RejectedCommandCount);
        Assert.Equal(EntityId.Invalid, sim.World.GetEntityAt(0, 0));
    }

    [Fact]
    public void PlaceEntity_WithOutOfRangeRotation_IsRejected()
    {
        var sim = NewSim();
        sim.Submit(new Command
        {
            Type = CommandType.PlaceEntity,
            ProtoId = sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id,
            X = 0, Y = 0, Rotation = 4,
        });
        sim.Step();
        Assert.Equal(1, sim.RejectedCommandCount);
        Assert.Equal(EntityId.Invalid, sim.World.GetEntityAt(0, 0));
        Assert.Equal(0, LiveLineCount(sim.Belts)); // helper already in this file from Task 1
    }

    [Fact]
    public void MultiTileEntity_PlacementAndRemoval_CoversFullFootprintAcrossChunks()
    {
        var sim = NewSim();
        const int x = -33, y = -33; // 2x3 footprint straddles all four neighboring chunks (ChunkSize=32)
        sim.Submit(PlaceLargeChest(sim, x, y));
        sim.Step();

        var id = sim.World.GetEntityAt(x, y);
        Assert.True(id.IsValid);
        for (int dy = 0; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
                Assert.Equal(id, sim.World.GetEntityAt(x + dx, y + dy));

        // 移除命令可以指向占地内任意一格,不要求是原点
        sim.Submit(new Command { Type = CommandType.RemoveEntity, X = x + 1, Y = y + 2 });
        sim.Step();

        for (int dy = 0; dy < 3; dy++)
            for (int dx = 0; dx < 2; dx++)
                Assert.Equal(EntityId.Invalid, sim.World.GetEntityAt(x + dx, y + dy));
    }

    [Fact]
    public void PlaceEntity_WithNegativeProtoId_IsRejected()
    {
        var sim = NewSim();
        sim.Submit(new Command
        {
            Type = CommandType.PlaceEntity,
            ProtoId = -1, // Negative ID
            X = 0, Y = 0, Rotation = 0,
        });
        sim.Step();
        Assert.Equal(1, sim.RejectedCommandCount);
        Assert.Equal(EntityId.Invalid, sim.World.GetEntityAt(0, 0));
    }

    [Fact]
    public void PlaceEntity_AllEntityTypes_RegisterCorrectly_RegressionForRefactor()
    {
        // 重构 CreateAndRegisterEntity 前的行为快照——belt/container/pole/generator/
        // machine/drill/inserter 各摆一个,确认注册到了各自的子系统里。重构后这条
        // 必须继续通过,证明抽 helper 没有改变任何实际行为。
        var sim = NewSim();

        int belt = sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id;
        int chest = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
        int pole = sim.Prototypes.Get<ElectricPolePrototype>("small-electric-pole").Id;
        int gen = sim.Prototypes.Get<FuelGeneratorPrototype>("burner-generator").Id;
        int furnace = sim.Prototypes.Get<FurnacePrototype>("stone-furnace").Id;
        int drill = sim.Prototypes.Get<MiningDrillPrototype>("electric-mining-drill").Id;
        int inserter = sim.Prototypes.Get<InserterPrototype>("inserter-basic").Id;

        sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = belt, X = 0, Y = 0, Rotation = 1 });
        sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = chest, X = 1, Y = 0 });
        sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = pole, X = 2, Y = 0 });
        sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = gen, X = 3, Y = 0 });
        sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = furnace, X = 6, Y = 0 });
        sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = drill, X = 9, Y = 0 });
        sim.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = inserter, X = 12, Y = 0 });
        sim.Step();

        Assert.Equal(0, sim.RejectedCommandCount);
        var beltId = sim.World.GetEntityAt(0, 0);
        Assert.True(sim.Belts.GetLineAt(0, 0).IsValid, "belt should register in BeltNetwork");
        var chestId = sim.World.GetEntityAt(1, 0);
        Assert.True(sim.Inventories.GetInventoryId(chestId).IsValid, "chest should get an inventory");
        Assert.True(sim.ElectricGrid.FindNetworkAt(2, 0).IsValid, "pole should register in ElectricGrid");
        var genId = sim.World.GetEntityAt(3, 0);
        Assert.Contains(genId, sim.ElectricGrid.GeneratorIds);
        var furnaceId = sim.World.GetEntityAt(6, 0);
        Assert.True(sim.Inventories.GetInventoryId(furnaceId, role: 1).IsValid, "furnace should get input inventory");
        Assert.True(sim.Inventories.GetInventoryId(furnaceId, role: 2).IsValid, "furnace should get output inventory");
        var drillId = sim.World.GetEntityAt(9, 0);
        Assert.Contains(drillId, sim.MiningDrills.ActiveIds);
        var inserterId = sim.World.GetEntityAt(12, 0);
        Assert.Contains(inserterId, sim.Inserters.ActiveIds);
    }

    private const byte E = 1;

    private static Command PlaceBelt(Simulation sim, int x, int y, byte rot) => new()
    {
        Type = CommandType.PlaceEntity,
        ProtoId = sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id,
        X = x, Y = y, Rotation = rot,
    };

    private static int LiveLineCount(BeltNetwork b)
    {
        int c = 0;
        for (int i = 0; i < b.Capacity; i++) if (b.IsAliveAtIndex(i)) c++;
        return c;
    }

    [Fact]
    public void PlaceBelt_RegistersLineInNetwork()
    {
        var sim = NewSim();
        sim.Submit(PlaceBelt(sim, 5, 5, E));
        sim.Step();
        var id = sim.Belts.GetLineAt(5, 5);
        Assert.True(id.IsValid);
        Assert.Equal(new[] { (5, 5) }, sim.Belts.GetLine(id).Tiles);
    }

    [Fact]
    public void PlaceTwoAdjacentSameDirBelts_MergeToOneLine()
    {
        var sim = NewSim();
        sim.Submit(PlaceBelt(sim, 5, 5, E));
        sim.Step();
        sim.Submit(PlaceBelt(sim, 6, 5, E));
        sim.Step();
        Assert.Equal(1, LiveLineCount(sim.Belts));
        var id = sim.Belts.GetLineAt(5, 5);
        Assert.Equal(new[] { (6, 5), (5, 5) }, sim.Belts.GetLine(id).Tiles);
    }

    [Fact]
    public void RemoveMiddleBelt_SplitsNetworkLine()
    {
        var sim = NewSim();
        foreach (var x in new[] { 5, 6, 7 }) { sim.Submit(PlaceBelt(sim, x, 5, E)); sim.Step(); }
        Assert.Equal(1, LiveLineCount(sim.Belts));

        sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 6, Y = 5 });
        sim.Step();

        Assert.Equal(2, LiveLineCount(sim.Belts));
        Assert.False(sim.Belts.GetLineAt(6, 5).IsValid);
        Assert.False(sim.World.GetEntityAt(6, 5).IsValid);
    }

    [Fact]
    public void RemoveNonBeltEntity_DoesNotTouchNetwork()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 0, 0));
        sim.Step();
        sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 0, Y = 0 });
        sim.Step(); // isBelt 守卫:不得抛
        Assert.Equal(0, LiveLineCount(sim.Belts));
        Assert.Equal(0, sim.RejectedCommandCount);
    }

    [Fact]
    public void PlaceChest_CreatesInventory_WithSixteenSlots()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 3, 4));
        sim.Step();
        var e = sim.World.GetEntityAt(3, 4);
        var invId = sim.Inventories.GetInventoryId(e);
        Assert.True(invId.IsValid);
        Assert.Equal(16, sim.Inventories.Get(invId).SlotCount);
    }

    [Fact]
    public void PlaceLargeChest_CreatesInventory_WithFortyEightSlots()
    {
        var sim = NewSim();
        sim.Submit(PlaceLargeChest(sim, -33, -33));
        sim.Step();
        var e = sim.World.GetEntityAt(-33, -33);
        var invId = sim.Inventories.GetInventoryId(e);
        Assert.True(invId.IsValid);
        Assert.Equal(48, sim.Inventories.Get(invId).SlotCount);
    }

    [Fact]
    public void RemoveChest_DestroysInventory()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 0, 0));
        sim.Step();
        var e = sim.World.GetEntityAt(0, 0);
        var invId = sim.Inventories.GetInventoryId(e);

        sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 0, Y = 0 });
        sim.Step();

        Assert.Equal(InventoryId.Invalid, sim.Inventories.GetInventoryId(e));
        Assert.False(sim.Inventories.IsAliveAtIndex(invId.Index));
        Assert.Equal(0, sim.RejectedCommandCount);
    }

    [Fact]
    public void RemoveNonContainerEntity_DoesNotTouchInventories()
    {
        var sim = NewSim();
        sim.Submit(PlaceBelt(sim, 5, 5, E));
        sim.Step();
        sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 5, Y = 5 });
        sim.Step();   // isContainer 守卫:不得抛
        Assert.Equal(0, sim.Inventories.Capacity);
        Assert.Equal(0, sim.RejectedCommandCount);
    }

    [Fact]
    public void Resources_StartsWithNoGeneratedChunks()
    {
        Assert.Equal(0, NewSim().Resources.GeneratedChunkCount);
    }

    [Fact]
    public void GetResourceAt_GeneratesChunk()
    {
        var sim = NewSim();
        sim.Resources.GetResourceAt(6, -8);
        Assert.True(sim.Resources.IsChunkGenerated(6, -8));
        Assert.Equal(1, sim.Resources.GeneratedChunkCount);
    }

    [Fact]
    public void WriteState_CoversResourceLayer()
    {
        var sim = NewSim();
        var before = sim.ComputeStateHash();
        sim.Resources.GetResourceAt(6, -8);      // starter patch -> non-empty chunk
        Assert.NotEqual(before, sim.ComputeStateHash());
    }

    private static Command Move(byte dir) => new() { Type = CommandType.MovePlayer, Rotation = dir };
    private static Command StopMove() => new() { Type = CommandType.StopPlayer };

    [Fact]
    public void MovePlayer_ThenStep_AdvancesByWalkSpeed()
    {
        var sim = NewSim();
        int speed = sim.Prototypes.Get<PlayerPrototype>("player").WalkSpeedSubTilesPerTick;
        sim.Submit(Move(2));   // east = +X
        sim.Step();
        Assert.Equal(speed, sim.Player.X);
        Assert.Equal(0, sim.Player.Y);
    }

    [Fact]
    public void StopPlayer_HaltsMovement()
    {
        var sim = NewSim();
        sim.Submit(Move(2)); sim.Step();
        int x = sim.Player.X;
        sim.Submit(StopMove()); sim.Step();
        Assert.Equal(x, sim.Player.X);
    }

    [Fact]
    public void MovePlayer_OutOfRangeDirection_IsRejected()
    {
        var sim = NewSim();
        sim.Submit(new Command { Type = CommandType.MovePlayer, Rotation = 8 });
        sim.Step();
        Assert.Equal(1, sim.RejectedCommandCount);
        Assert.Equal(0, sim.Player.X);
    }

    [Fact]
    public void MovePlayer_IntoOccupiedTile_IsBlocked()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 1, 0));   // tile (1,0), east of the player at sub-tile (0,0)
        sim.Step();
        for (int t = 0; t < 20; t++) { sim.Submit(Move(2)); sim.Step(); }
        // walking east must never enter tile x>=1
        Assert.True(sim.Player.X >> 8 < 1, $"player X sub-tile {sim.Player.X} entered an occupied tile");
    }

    private static Command MineAt(int x, int y) => new() { Type = CommandType.MineStart, X = x, Y = y };

    [Fact]
    public void HandMine_Entity_RemovesItAndYieldsItem()
    {
        var sim = NewSim();
        int chestItem = sim.Prototypes.Get<ItemPrototype>("wooden-chest").Id;
        int miningTicks = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").MiningTimeTicks;
        sim.Submit(PlaceChest(sim, 1, 0));
        sim.Step();
        int before = sim.Player.Inventory.CountOf(chestItem);

        for (int t = 0; t < miningTicks + 2; t++) { sim.Submit(MineAt(1, 0)); sim.Step(); }

        Assert.False(sim.World.GetEntityAt(1, 0).IsValid);
        Assert.Equal(before + 1, sim.Player.Inventory.CountOf(chestItem));
        Assert.Equal(0, sim.RejectedCommandCount);
    }

    [Fact]
    public void HandMine_Resource_ExtractsOneAndYieldsItem()
    {
        var sim = NewSim();
        int coalItem = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        int miningTicks = sim.Prototypes.Get<ResourcePrototype>("coal").MiningTimeTicks;
        int itemBefore = sim.Player.Inventory.CountOf(coalItem);
        int resBefore = sim.Resources.GetResourceAt(1, -1).Amount;
        Assert.True(resBefore > 0);

        for (int t = 0; t < miningTicks + 2; t++) { sim.Submit(MineAt(1, -1)); sim.Step(); }

        Assert.Equal(itemBefore + 1, sim.Player.Inventory.CountOf(coalItem));
        Assert.Equal(resBefore - 1, sim.Resources.GetResourceAt(1, -1).Amount);
    }

    [Fact]
    public void HandMine_OutOfReach_MakesNoProgress()
    {
        var sim = NewSim();
        int before = sim.Player.Inventory.TotalItems();
        for (int t = 0; t < 200; t++) { sim.Submit(MineAt(6, -8)); sim.Step(); }   // ~10 tiles, reach is 6
        Assert.Equal(0, sim.Player.MineProgress);
        Assert.Equal(before, sim.Player.Inventory.TotalItems());
    }

    [Fact]
    public void HandMine_Resource_InventoryFull_Pauses()
    {
        var sim = NewSim();
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        int coalStack = sim.Prototypes.Get<ItemPrototype>("coal").StackSize;
        int miningTicks = sim.Prototypes.Get<ResourcePrototype>("coal").MiningTimeTicks;
        // fill the whole player inventory with coal
        var inv = sim.Player.Inventory;
        inv.Insert(coal, inv.SlotCount * coalStack, coalStack);
        int amtBefore = sim.Resources.GetResourceAt(1, -1).Amount;

        for (int t = 0; t < miningTicks + 5; t++) { sim.Submit(MineAt(1, -1)); sim.Step(); }

        Assert.Equal(amtBefore, sim.Resources.GetResourceAt(1, -1).Amount);   // nothing extracted
        Assert.Equal(miningTicks, sim.Player.MineProgress);                    // parked at threshold
    }

    private static Command Craft(int recipeId, int count) => new()
        { Type = CommandType.CraftEnqueue, ProtoId = recipeId, X = count };

    [Fact]
    public void HandCraft_DeductsIngredientsAtEnqueue_ProducesResultsOverTime()
    {
        var sim = NewSim();
        int plate = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int gear = sim.Prototypes.Get<ItemPrototype>("iron-gear-wheel").Id;
        var recipe = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel");
        int plateStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;
        sim.Player.Inventory.Insert(plate, 4, plateStack);   // exactly enough for 2 gears (2 each)
        int plateBefore = sim.Player.Inventory.CountOf(plate);
        int gearBefore = sim.Player.Inventory.CountOf(gear);

        sim.Submit(Craft(recipe.Id, 2));
        sim.Step();
        Assert.Equal(plateBefore - 4, sim.Player.Inventory.CountOf(plate));
        Assert.Single(sim.Player.CraftQueue);

        for (int t = 0; t < 2 * recipe.EnergyRequiredTicks + 2; t++) sim.Step();
        Assert.Equal(gearBefore + 2, sim.Player.Inventory.CountOf(gear));
        Assert.Empty(sim.Player.CraftQueue);
    }

    [Fact]
    public void HandCraft_NotEnoughIngredients_IsRejected_NoDeduction()
    {
        var sim = NewSim();
        int plate = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int gearRecipeId = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel").Id;
        int before = sim.Player.Inventory.CountOf(plate);
        sim.Submit(Craft(gearRecipeId, 999));      // needs 1998 plate — far beyond any starting amount
        sim.Step();
        Assert.Equal(1, sim.RejectedCommandCount);
        Assert.Equal(before, sim.Player.Inventory.CountOf(plate));
        Assert.Empty(sim.Player.CraftQueue);
    }

    [Fact]
    public void HandCraft_HugeCount_IsRejected_NoDeduction()
    {
        var sim = NewSim();
        int plate = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int gearRecipeId = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel").Id;
        int before = sim.Player.Inventory.CountOf(plate);

        sim.Submit(Craft(gearRecipeId, 1_073_741_824));   // would overflow int * int
        sim.Step();

        Assert.Equal(1, sim.RejectedCommandCount);
        Assert.Equal(before, sim.Player.Inventory.CountOf(plate));
        Assert.Empty(sim.Player.CraftQueue);
    }

    [Fact]
    public void HandCraft_SmeltingRecipe_IsRejected()
    {
        var sim = NewSim();
        int ironPlateRecipeId = sim.Prototypes.Get<RecipePrototype>("iron-plate").Id;  // category "smelting"
        sim.Submit(Craft(ironPlateRecipeId, 1));
        sim.Step();
        Assert.Equal(1, sim.RejectedCommandCount);
    }

    [Fact]
    public void HandCraft_QueueFull_IsRejected()
    {
        var sim = NewSim();
        int cap = sim.Prototypes.Get<PlayerPrototype>("player").CraftQueueCap;
        int chestRecipeId = sim.Prototypes.Get<RecipePrototype>("wooden-chest").Id;
        // give the player plenty of plate
        sim.Player.Inventory.Insert(sim.Prototypes.Get<ItemPrototype>("iron-plate").Id, 500, 100);
        // batch all cap enqueues into a single tick: wooden-chest's EnergyRequiredTicks (30) is
        // less than CraftQueueCap (32), so stepping once per submit would let the head job
        // complete mid-loop and drain the queue below cap before the loop finishes.
        for (int i = 0; i < cap; i++) sim.Submit(Craft(chestRecipeId, 1));
        sim.Step();
        Assert.Equal(cap, sim.Player.CraftQueue.Count);
        sim.Submit(Craft(chestRecipeId, 1)); sim.Step();
        Assert.Equal(1, sim.RejectedCommandCount);
        Assert.Equal(cap, sim.Player.CraftQueue.Count);
    }

    [Fact]
    public void HandCraft_InventoryFull_BlocksHeadJob()
    {
        var sim = NewSim();
        int plate = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int gear = sim.Prototypes.Get<ItemPrototype>("iron-gear-wheel").Id;
        var recipe = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel");
        // more than enough plate for one gear (2 needed), then jam every slot with a
        // non-gear item so the result can't land. Using exactly 2 plate would empty that
        // slot the instant CraftEnqueue deducts the ingredient, freeing a spot for the gear
        // and defeating the "inventory full" setup — so stock extra plate that survives deduction.
        var inv = sim.Player.Inventory;
        inv.Insert(plate, 4, 100);   // craft consumes 2, leaving 2 behind (slot stays non-empty, still iron-plate)
        // fill remaining slots: keep 0 free for gear. Use coal to fill all-but-none.
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        inv.Insert(coal, inv.SlotCount * 50, 50);   // fills every slot not holding the plate stack
        // (SlotCount-1) slots * 50 coal fills them; plate slot stays iron-plate(2 after deduction).
        // No empty slot, no partial gear-compatible slot.

        sim.Submit(Craft(recipe.Id, 1));
        sim.Step();
        for (int t = 0; t < recipe.EnergyRequiredTicks + 5; t++) sim.Step();

        Assert.Equal(0, sim.Player.Inventory.CountOf(gear));   // blocked
        Assert.Single(sim.Player.CraftQueue);
        Assert.Equal(recipe.EnergyRequiredTicks, sim.Player.CraftQueue[0].Progress);
    }

    [Fact]
    public void NewSimulation_FillsPlayerInventoryFromStartingKit()
    {
        var sim = NewSim();
        int plate = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int chest = sim.Prototypes.Get<ItemPrototype>("wooden-chest").Id;
        Assert.Equal(8, sim.Player.Inventory.CountOf(plate));
        Assert.Equal(1, sim.Player.Inventory.CountOf(chest));
    }

    [Fact]
    public void FullLoop_PlaceChest_HandMineItBack_ThenCraftAnother()
    {
        var sim = NewSim();
        int chestItem = sim.Prototypes.Get<ItemPrototype>("wooden-chest").Id;
        int plate = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int chestRecipe = sim.Prototypes.Get<RecipePrototype>("wooden-chest").Id;
        int miningTicks = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").MiningTimeTicks;
        var chestEnergy = sim.Prototypes.Get<RecipePrototype>("wooden-chest").EnergyRequiredTicks;
        int chestBefore = sim.Player.Inventory.CountOf(chestItem);   // starter kit chest
        int plateBefore = sim.Player.Inventory.CountOf(plate);       // starter kit plate

        // place a chest at (1,0) -- PlaceEntity does not consume the player's inventory -- then mine it back
        sim.Submit(new Command
        {
            Type = CommandType.PlaceEntity,
            ProtoId = sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id,
            X = 1, Y = 0, Rotation = 0,
        });
        sim.Step();
        Assert.Equal(chestBefore, sim.Player.Inventory.CountOf(chestItem));   // unchanged by placement

        for (int t = 0; t < miningTicks + 2; t++) { sim.Submit(MineAt(1, 0)); sim.Step(); }
        Assert.Equal(chestBefore + 1, sim.Player.Inventory.CountOf(chestItem));   // mined back

        // craft a second chest from starter plate
        sim.Submit(Craft(chestRecipe, 1));
        sim.Step();
        for (int t = 0; t < chestEnergy + 2; t++) sim.Step();
        Assert.Equal(chestBefore + 2, sim.Player.Inventory.CountOf(chestItem));
        Assert.Equal(plateBefore - 2, sim.Player.Inventory.CountOf(plate));
    }

    private static Command PlacePole(Simulation sim, int x, int y) => new()
    {
        Type = CommandType.PlaceEntity,
        ProtoId = sim.Prototypes.Get<ElectricPolePrototype>("small-electric-pole").Id,
        X = x, Y = y, Rotation = 0,
    };

    private static Command PlaceGenerator(Simulation sim, int x, int y) => new()
    {
        Type = CommandType.PlaceEntity,
        ProtoId = sim.Prototypes.Get<FuelGeneratorPrototype>("burner-generator").Id,
        X = x, Y = y, Rotation = 0,
    };

    private static Command TransferTo(int x, int y, int itemId, int count) => new()
        { Type = CommandType.TransferToEntity, X = x, Y = y, ProtoId = itemId, Count = count };

    [Fact]
    public void PlaceTwoPoles_WithinRange_SameNetwork()
    {
        var sim = NewSim();
        sim.Submit(PlacePole(sim, 0, 0));
        sim.Submit(PlacePole(sim, 5, 0));
        sim.Step();
        Assert.Equal(sim.ElectricGrid.FindNetworkAt(0, 0), sim.ElectricGrid.FindNetworkAt(5, 0));
    }

    [Fact]
    public void RemovePole_LeavesTheOtherPoleAlone()
    {
        var sim = NewSim();
        sim.Submit(PlacePole(sim, 0, 0));
        sim.Submit(PlacePole(sim, 6, 0));
        sim.Step();
        Assert.Equal(sim.ElectricGrid.FindNetworkAt(0, 0), sim.ElectricGrid.FindNetworkAt(6, 0));

        sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 0, Y = 0 });
        sim.Step();
        Assert.False(sim.ElectricGrid.FindNetworkAt(0, 0).IsValid);
        Assert.True(sim.ElectricGrid.FindNetworkAt(6, 0).IsValid);
    }

    [Fact]
    public void TransferToEntity_FillsGeneratorFuelSlot()
    {
        var sim = NewSim();
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        sim.Player.Inventory.Insert(coal, 5, sim.Prototypes.Get<ItemPrototype>("coal").StackSize);
        sim.Submit(PlaceGenerator(sim, 0, 0));
        sim.Step();
        sim.Submit(TransferTo(0, 0, coal, 3));
        sim.Step();

        var genId = sim.World.GetEntityAt(0, 0);
        var fuelInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(genId));
        Assert.Equal(3, fuelInv.CountOf(coal));
    }

    [Fact]
    public void TransferToEntity_OutOfReach_IsRejected()
    {
        var sim = NewSim();
        sim.Player.Inventory.Insert(sim.Prototypes.Get<ItemPrototype>("coal").Id, 5, 50);
        sim.Submit(PlaceChest(sim, 20, 0));
        sim.Step();
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        sim.Submit(TransferTo(20, 0, coal, 1));
        sim.Step();
        Assert.Equal(1, sim.RejectedCommandCount);
    }

    [Fact]
    public void GeneratorWithFuel_PowersManuallyRegisteredDemand_BurnsProportionally()
    {
        var sim = NewSim();
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        int coalStack = sim.Prototypes.Get<ItemPrototype>("coal").StackSize;
        sim.Player.Inventory.Insert(coal, 5, coalStack);

        sim.Submit(PlacePole(sim, 0, 0));
        sim.Submit(PlaceGenerator(sim, 2, 0));   // Chebyshev distance 2 <= pole's supplyAreaDistanceTiles 2
        sim.Step();
        sim.Submit(TransferTo(2, 0, coal, 5));
        sim.Step();

        var genId = sim.World.GetEntityAt(2, 0);
        long powerPerTick = sim.Prototypes.Get<FuelGeneratorPrototype>("burner-generator").PowerOutputJPerTick;
        var fakeConsumer = new EntityId(999, 1);

        sim.ElectricGrid.RegisterDemand(fakeConsumer, 0, 0, UsagePriority.PrimaryInput, powerPerTick / 2);
        sim.Step();   // Step 内:发电机登记供给(此时已有煤)-> Settle(与上面手动登记的需求一起结算)-> 烧油

        Assert.Equal(Q16.One, sim.ElectricGrid.GetSatisfaction(fakeConsumer));
        Assert.True(sim.ElectricGrid.GetFuelBufferJ(genId) > 0);   // 只烧了一半,缓冲还有剩(一块煤够很多 tick)
    }

    private static Command TransferFrom(int x, int y, int itemId, int count) => new()
        { Type = CommandType.TransferFromEntity, X = x, Y = y, ProtoId = itemId, Count = count };

    [Fact]
    public void TransferFromEntity_HappyPath_MovesItemsBothWays()
    {
        var sim = NewSim();
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        sim.Submit(PlaceChest(sim, 5, 0));
        sim.Step();

        var chestId = sim.World.GetEntityAt(5, 0);
        var chestInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(chestId));
        chestInv.Insert(coal, 10, sim.Prototypes.Get<ItemPrototype>("coal").StackSize);

        int playerBefore = sim.Player.Inventory.CountOf(coal);
        sim.Submit(TransferFrom(5, 0, coal, 6));
        sim.Step();

        Assert.Equal(playerBefore + 6, sim.Player.Inventory.CountOf(coal));
        Assert.Equal(4, chestInv.CountOf(coal));
    }

    [Fact]
    public void TransferFromEntity_MoreThanPlayerCanHold_ClampsWithoutLosingItems()
    {
        var sim = NewSim();
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        int coalStack = sim.Prototypes.Get<ItemPrototype>("coal").StackSize;
        int ironPlate = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int ironStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;

        // 玩家背包 60 槽。Task 2 扩充了开局物资包(data/base/player.json):
        // iron-plate x8、wooden-chest x1、electric-mining-drill x1、stone-furnace x1、
        // burner-generator x1、small-electric-pole x2、coal x20 —— 7 种不同物品各占
        // 一槽,起始就占用 7 槽(不再是旧版的 2 槽),其中 coal 槽是半满的(20/50,
        // 还有 30 个空间)。
        //
        // 关键点:Inventory.Insert 先补同类未满槽(第一轮),再占空槽(第二轮)——
        // 玩家现在已经有一个半满的 coal 槽,后面转移煤时这个槽会先被吃掉一部分,
        // 不能再假设"转移进来的煤全部落在新的空槽里"。
        //
        // 为了让这条测试仍然精确覆盖"转移量超过玩家能吃下的量,多出的留在箱子里"
        // 这条行为,这里把铁板灌到刚好填满*所有*空槽(53 个)外加那个半满的铁板槽
        // (60 - 7 起始槽 = 53 个空槽;补满半满槽需要 100-8=92,再填满 53 个满槽需要
        // 53*100),让玩家背包里除了那个半满的 coal 槽以外,再没有任何空间——
        // 这样转移煤时第二轮(占空槽)完全没有名额,只有第一轮(补 coal 半满槽的
        // 30 个空间)能接收。
        sim.Player.Inventory.Insert(ironPlate, (ironStack - 8) + 53 * ironStack, ironStack);

        sim.Submit(PlaceChest(sim, 5, 0));
        sim.Step();
        var chestId = sim.World.GetEntityAt(5, 0);
        var chestInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(chestId));
        chestInv.Insert(coal, 80, coalStack);   // 箱子里 80 个煤,远超玩家能吃下的量

        int totalBefore = sim.Player.Inventory.CountOf(coal) + chestInv.CountOf(coal);
        sim.Submit(TransferFrom(5, 0, coal, 80));
        sim.Step();

        int totalAfter = sim.Player.Inventory.CountOf(coal) + chestInv.CountOf(coal);
        Assert.Equal(0, sim.RejectedCommandCount);
        Assert.Equal(totalBefore, totalAfter);              // 一个都没凭空消失
        // 玩家原有 20 个煤(半满槽,空间 30)+ 第一轮吃满这 30 个空间 = 50;
        // 没有空槽可用,第二轮拿不到名额,超出的 50 个留在箱子里(80-30=50)。
        Assert.Equal(50, sim.Player.Inventory.CountOf(coal));  // 20(起始) + 30(补满半满槽)
        Assert.Equal(50, chestInv.CountOf(coal));              // 80 - 30 = 50,箱子里剩下搬不走的部分
    }

    private static Command PlaceFurnace(Simulation sim, int x, int y) => new()
    {
        Type = CommandType.PlaceEntity,
        ProtoId = sim.Prototypes.Get<FurnacePrototype>("stone-furnace").Id,
        X = x, Y = y, Rotation = 0,
    };

    private static Command PlaceAssembler(Simulation sim, int x, int y) => new()
    {
        Type = CommandType.PlaceEntity,
        ProtoId = sim.Prototypes.Get<AssemblingMachinePrototype>("assembling-machine-1").Id,
        X = x, Y = y, Rotation = 0,
    };

    private static Command SetRecipe(int x, int y, int recipeProtoId) => new()
        { Type = CommandType.SetRecipe, X = x, Y = y, ProtoId = recipeProtoId };

    // Shared setup: pole(0,0) + generator(2,0) fueled with coal, then a
    // furnace or assembler at (0,2) — Chebyshev distance 2 from the pole,
    // matching small-electric-pole's supplyAreaDistanceTiles (2). Every test
    // below that expects a machine to actually make progress needs this —
    // ElectricGrid.GetSatisfaction defaults to Q16.Zero for any demand with
    // no reachable network AND for any demand on a network with zero
    // registered supply, so a machine with no pole+generator never advances
    // at all (this is the exact mistake an earlier draft of this plan made
    // and is why every progress-observing test below places power first).
    private static void PlacePoweredMachineInfra(Simulation sim)
    {
        sim.Submit(PlacePole(sim, 0, 0));
        sim.Submit(PlaceGenerator(sim, 2, 0));
        sim.Step();
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        int coalStack = sim.Prototypes.Get<ItemPrototype>("coal").StackSize;
        sim.Player.Inventory.Insert(coal, 5, coalStack);   // player has no coal by default (data/base/player.json)
        sim.Submit(TransferTo(2, 0, coal, 5));
        sim.Step();
    }

    [Fact]
    public void Furnace_AutoMatchesAndSmeltsIronOre()
    {
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceFurnace(sim, 0, 2));
        sim.Step();

        var furnaceId = sim.World.GetEntityAt(0, 2);
        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 1));
        int oreId = sim.Prototypes.Get<ItemPrototype>("iron-ore").Id;
        inputInv.Insert(oreId, 1, sim.Prototypes.Get<ItemPrototype>("iron-ore").StackSize);
        sim.Machines.MarkAwake(furnaceId);   // 直接操纵输入库存,按测试约定显式唤醒

        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        var outputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 2));

        // iron-plate is 192 ticks (3.2s); the generator's 1500 J/tick output
        // exactly matches the furnace's 1500 J/tick demand (both "90kW" in
        // data/base), so with no competing consumer it runs at full
        // satisfaction — 192 ticks to complete + 1 more tick for the next
        // PreSettle to flush the output. 200 gives comfortable headroom.
        for (int t = 0; t < 200; t++) sim.Step();

        Assert.Equal(1, outputInv.CountOf(plateId));
        Assert.Equal(0, inputInv.CountOf(oreId));   // consumed
    }

    [Fact]
    public void AssemblingMachine_WithoutSetRecipe_NeverConsumesInput()
    {
        var sim = NewSim();
        sim.Submit(PlaceAssembler(sim, 0, 0));
        sim.Step();

        var asmId = sim.World.GetEntityAt(0, 0);
        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(asmId, 1));
        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        inputInv.Insert(plateId, 2, sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize);

        for (int t = 0; t < 100; t++) sim.Step();

        Assert.Equal(2, inputInv.CountOf(plateId));   // untouched — no recipe ever set
    }

    [Fact]
    public void AssemblingMachine_SetRecipe_LoopsSameRecipeAcrossBatches()
    {
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceAssembler(sim, 0, 2));
        sim.Step();

        var asmId = sim.World.GetEntityAt(0, 2);
        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int gearId = sim.Prototypes.Get<ItemPrototype>("iron-gear-wheel").Id;
        int gearRecipeId = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel").Id;

        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(asmId, 1));
        var outputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(asmId, 2));
        inputInv.Insert(plateId, 4, sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize);   // 2 batches worth

        sim.Submit(SetRecipe(0, 2, gearRecipeId));
        sim.Step();

        // iron-gear-wheel is 30 ticks (0.5s); the generator's 1500 J/tick
        // output covers the assembler's 1250 J/tick demand fully (satisfaction
        // == One), so each batch completes in 30 ticks + 1 flush tick; two
        // batches back-to-back (assembler recipe stays set — no second
        // SetRecipe needed) comfortably fit in 100 more ticks.
        for (int t = 0; t < 100; t++) sim.Step();

        Assert.Equal(2, outputInv.CountOf(gearId));
        Assert.Equal(0, inputInv.CountOf(plateId));
    }

    [Fact]
    public void SetRecipe_RejectsFurnaceTarget()
    {
        var sim = NewSim();
        sim.Submit(PlaceFurnace(sim, 0, 0));
        sim.Step();
        int recipeId = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel").Id;

        sim.Submit(SetRecipe(0, 0, recipeId));
        sim.Step();

        Assert.Equal(1, sim.RejectedCommandCount);
    }

    [Fact]
    public void SetRecipe_RejectsNonMachineTarget()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 0, 0));
        sim.Step();
        int recipeId = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel").Id;

        sim.Submit(SetRecipe(0, 0, recipeId));
        sim.Step();

        Assert.Equal(1, sim.RejectedCommandCount);
    }

    [Fact]
    public void SetRecipe_RejectsCategoryMismatch()
    {
        var sim = NewSim();
        sim.Submit(PlaceAssembler(sim, 0, 0));
        sim.Step();
        int smeltingRecipeId = sim.Prototypes.Get<RecipePrototype>("iron-plate").Id;   // category "smelting"

        sim.Submit(SetRecipe(0, 0, smeltingRecipeId));
        sim.Step();

        Assert.Equal(1, sim.RejectedCommandCount);
    }

    [Fact]
    public void Machine_UnderpoweredSatisfaction_TakesTwiceAsLong()
    {
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceFurnace(sim, 0, 2));
        sim.Step();

        var furnaceId = sim.World.GetEntityAt(0, 2);
        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 1));
        int oreId = sim.Prototypes.Get<ItemPrototype>("iron-ore").Id;
        inputInv.Insert(oreId, 1, sim.Prototypes.Get<ItemPrototype>("iron-ore").StackSize);

        // A second PrimaryInput consumer demanding exactly the furnace's own
        // demand (both "90kW" = 1500 J/tick) doubles total demand (3000)
        // against the generator's fixed 1500 J/tick supply, giving BOTH
        // consumers satisfaction == 0.5 (Settle() broadcasts one ratio per
        // tier, not a per-registrant split — see ElectricGrid.SettleNetwork).
        // Progress advances at half the per-tick rate, so completion takes
        // ~2x as many ticks as the unthrottled 192 + 1 flush tick baseline.
        long furnaceDemand = sim.Prototypes.Get<FurnacePrototype>("stone-furnace").EnergyUsageJPerTick;
        var fakeConsumer = new EntityId(999, 1);
        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        var outputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 2));

        int tick = 0;
        while (outputInv.CountOf(plateId) == 0 && tick < 500)
        {
            sim.ElectricGrid.RegisterDemand(fakeConsumer, 0, 0, UsagePriority.PrimaryInput, furnaceDemand);
            sim.Step();
            tick++;
        }

        // Comfortably brackets the ~385-tick expected value (2*192 + 1 flush
        // tick) while staying far above the 193-tick full-speed baseline, so
        // this still catches a "throttling doesn't work" regression without
        // depending on an exact off-by-one in the flush-timing arithmetic.
        Assert.InRange(tick, 300, 450);
    }

    [Fact]
    public void Machine_StandbyEnergyRegisteredEvenWhenIdle()
    {
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceFurnace(sim, 0, 2));
        sim.Step();   // furnace has no ore yet — idles, but must still register demand

        var fakeConsumer = new EntityId(999, 1);
        long furnaceDemand = sim.Prototypes.Get<FurnacePrototype>("stone-furnace").EnergyUsageJPerTick;

        // The generator's output (1500 J/tick) exactly equals furnaceDemand.
        // If the idle furnace registered 0 demand, this consumer alone would
        // fully consume the generator's output (satisfaction == One); since
        // the idle furnace's standby draw competes for the same 1500 J/tick,
        // this consumer is throttled to half instead.
        sim.ElectricGrid.RegisterDemand(fakeConsumer, 0, 0, UsagePriority.PrimaryInput, furnaceDemand);
        sim.Step();

        Assert.NotEqual(Q16.One, sim.ElectricGrid.GetSatisfaction(fakeConsumer));
    }

    [Fact]
    public void Machine_OutputBlocked_HoldsCompletedUntilSpaceFrees()
    {
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceFurnace(sim, 0, 2));
        sim.Step();

        var furnaceId = sim.World.GetEntityAt(0, 2);
        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 1));
        var outputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 2));
        int oreId = sim.Prototypes.Get<ItemPrototype>("iron-ore").Id;
        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int plateStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;

        inputInv.Insert(oreId, 1, sim.Prototypes.Get<ItemPrototype>("iron-ore").StackSize);
        sim.Machines.MarkAwake(furnaceId);   // 直接操纵输入库存,按测试约定显式唤醒
        outputInv.Insert(plateId, plateStack, plateStack);   // 预填满输出槽(不触发任何唤醒/睡眠判断,合法)

        for (int t = 0; t < 200; t++) sim.Step();

        Assert.Equal(0, inputInv.CountOf(oreId));            // consumed at completion
        Assert.Equal(plateStack, outputInv.CountOf(plateId)); // still full — flush blocked
        Assert.False(sim.Machines.IsAwake(furnaceId));   // 新增断言:确认它确实睡了,不是碰巧还醒着

        sim.Submit(TransferFrom(0, 2, plateId, plateStack));   // 真实路径腾空间,同时是 Task 4 的唤醒触发点
        sim.Step();

        Assert.Equal(1, outputInv.CountOf(plateId));
    }

    [Fact]
    public void AssemblingMachine_MissingIngredients_FreezesProgressWithoutResetting()
    {
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceAssembler(sim, 0, 2));
        sim.Step();

        var asmId = sim.World.GetEntityAt(0, 2);
        int gearRecipeId = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel").Id;
        sim.Submit(SetRecipe(0, 2, gearRecipeId));
        sim.Step();

        for (int t = 0; t < 50; t++) sim.Step();   // input inventory still empty the whole time

        Assert.Equal(0, sim.Machines.GetProgress(asmId));
        Assert.Equal(gearRecipeId, sim.Machines.GetCurrentRecipe(asmId));   // not reset

        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(asmId, 1));
        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        inputInv.Insert(plateId, 2, sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize);
        sim.Machines.MarkAwake(asmId);   // 直接操纵输入库存,按测试约定显式唤醒

        sim.Step();
        Assert.True(sim.Machines.GetProgress(asmId) > 0);   // now advancing from 0
    }

    [Fact]
    public void AssemblingMachine_NoRecipe_GoesAsleepWithinOneTick()
    {
        var sim = NewSim();
        sim.Submit(PlaceAssembler(sim, 0, 0));
        sim.Step();   // 放置

        var asmId = sim.World.GetEntityAt(0, 0);
        sim.Step();   // 一次 PreSettle 判断:没配方 -> 睡

        Assert.False(sim.Machines.IsAwake(asmId));
    }

    [Fact]
    public void AssemblingMachine_SafetyNet_SelfHealsWithoutExplicitWakeCall()
    {
        // 模拟"漏唤醒"场景:直接往输入库存塞原料(不经过任何命令/机械臂,
        // 也不手动调 MarkAwake),断言安全网在 SafetyNetIntervalTicks 个 tick
        // 之内自己把机器叫醒并推进——这是 spec §8 要求的关键回归测试,验证
        // "唤醒调用点漏写"不会导致永久卡死。
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceAssembler(sim, 0, 2));
        sim.Step();

        var asmId = sim.World.GetEntityAt(0, 2);
        int gearRecipeId = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel").Id;
        sim.Submit(SetRecipe(0, 2, gearRecipeId));
        sim.Step();   // 有配方但没原料 -> PostSettle 判定 asleep

        Assert.False(sim.Machines.IsAwake(asmId));

        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(asmId, 1));
        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        inputInv.Insert(plateId, 2, sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize);
        // 故意不调 sim.Machines.MarkAwake —— 这就是"漏唤醒"的场景。

        for (int t = 0; t < Machines.SafetyNetIntervalTicks; t++) sim.Step();

        Assert.True(sim.Machines.GetProgress(asmId) > 0);   // 安全网救回来了
    }

    private static Command PlaceDrill(Simulation sim, int x, int y, byte rotation = 0) => new()
    {
        Type = CommandType.PlaceEntity,
        ProtoId = sim.Prototypes.Get<MiningDrillPrototype>("electric-mining-drill").Id,
        X = x, Y = y, Rotation = rotation,
    };

    // 同 P9 的 PlacePoweredMachineInfra:电线杆(0,0)+ 发电机(2,0)充好煤。
    private static void PlacePoweredDrillInfra(Simulation sim)
    {
        sim.Submit(PlacePole(sim, 0, 0));
        sim.Submit(PlaceGenerator(sim, 2, 0));
        sim.Step();
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        int coalStack = sim.Prototypes.Get<ItemPrototype>("coal").StackSize;
        sim.Player.Inventory.Insert(coal, 5, coalStack);
        sim.Submit(TransferTo(2, 0, coal, 5));
        sim.Step();
    }

    // Task 2 数据变更(7 种建筑原型加入注册表)让 PrototypeRegistry.AssignIds() 的全局
    // 排序结果整体偏移,ResourceGrid 的伪随机矿脉生成跟着 ResourcePrototype.Id 走,
    // 分布随之改变(同 ScenarioSentinel.MeasuredFedDrillCount 那条根因说明)——原来写死
    // 的 "drill 在 (0,2) 脚下有矿" 不再成立。用这个探测手法代替:在 pole(0,0) 供电范围内
    // (Chebyshev 距离 2,2x2 footprint 完整落在范围内)搜第一个满足 minOreTiles 的位置,
    // 复用 MiningDrill_FindsResourceInFootprint_AndExtractsToOutputChest / Exhausted 那两条
    // 测试已经用过的搜索模式,不是新发明一套。
    private static (int X, int Y) FindPoweredOreOrigin(Simulation sim, int minOreTiles = 1)
    {
        for (int ty = -2; ty <= 1; ty++)
            for (int tx = -1; tx <= 1; tx++)
            {
                int oreTiles = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                        if (!sim.Resources.GetResourceAt(tx + dx, ty + dy).IsEmpty) oreTiles++;
                if (oreTiles >= minOreTiles) return (tx, ty);
            }
        throw new InvalidOperationException(
            $"could not find a 2x2 area with >= {minOreTiles} ore tile(s) under the default seed within powered region");
    }

    [Fact]
    public void MiningDrill_FindsResourceInFootprint_AndExtractsToOutputChest()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        // Drill origin must stay within the pole's supplyAreaDistanceTiles (2, Chebyshev,
        // measured from the entity's placement-time (X,Y) origin — see ElectricGrid).
        // Pole is at (0,0), so drill must be within Chebyshev distance 2.
        // Task 2 data变更影响 resource id,导致矿脉分布改变,需要搜索新的有矿坐标。
        // 采矿机 2x2 footprint,朝东(rotation=1)输出到东方 2 格处。
        var (drillX, drillY) = FindPoweredOreOrigin(sim);

        sim.Submit(PlaceDrill(sim, drillX, drillY, rotation: 1));
        int chestX = drillX + 2, chestY = drillY;
        sim.Submit(PlaceChest(sim, chestX, chestY));
        sim.Step();

        var drillId = sim.World.GetEntityAt(drillX, drillY);
        var chestId = sim.World.GetEntityAt(chestX, chestY);
        var chestInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(chestId));

        for (int t = 0; t < 200; t++) sim.Step();

        Assert.True(chestInv.TotalItems() > 0);
        Assert.NotEqual(-1, sim.MiningDrills.GetTargetX(drillId)); // still locked on a footprint tile (rich enough to outlast 200 ticks)
    }

    [Fact]
    public void MiningDrill_NoResourceUnderFootprint_GoesAsleepAndStaysAsleep()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        // 找一个确定没有矿的 2x2 区域(默认种子全局覆盖率只有 ~11%,大多数格子
        // 都是空的)——用同样的探测手法但反过来断言:必须全空。
        int px = 0, py = 20;   // 远离其它测试用到的坐标,避免踩到别的矿脉
        bool anyResource = false;
        for (int dy = 0; dy < 2 && !anyResource; dy++)
            for (int dx = 0; dx < 2 && !anyResource; dx++)
                if (!sim.Resources.GetResourceAt(px + dx, py + dy).IsEmpty) anyResource = true;
        Assert.False(anyResource, "test assumes (0,20)-(1,21) has no ore under the default seed — if this fails, pick a different empty coordinate.");

        sim.Submit(PlaceDrill(sim, px, py, rotation: 1));
        sim.Step();   // 放置
        var drillId = sim.World.GetEntityAt(px, py);

        sim.Step();   // 目标搜索失败 -> 睡

        Assert.False(sim.MiningDrills.IsAwake(drillId));

        long progressBefore = sim.MiningDrills.GetProgress(drillId);
        for (int t = 0; t < 200; t++) sim.Step();   // 远超一次安全网周期(60),期间没有任何外部事件

        Assert.Equal(progressBefore, sim.MiningDrills.GetProgress(drillId));   // 完全没有进展——安全网把它闪醒了几次,但每次重新搜索仍然找不到矿,又睡回去
        Assert.Equal(-1, sim.MiningDrills.GetTargetX(drillId));
    }

    [Fact]
    public void MiningDrill_OutputsToBelt_WithCorrectItemType()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        // Same footprint/power reasoning as the chest test above: drill origin must stay
        // within the pole's supplyAreaDistanceTiles (2, Chebyshev, from (0,0)).
        var (drillX, drillY) = FindPoweredOreOrigin(sim);
        sim.Submit(PlaceDrill(sim, drillX, drillY, rotation: 1)); // 朝东输出到 (drillX+2, drillY)
        int beltX = drillX + 2, beltY = drillY;
        sim.Submit(new Command { Type = CommandType.PlaceEntity,
            ProtoId = sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id,
            X = beltX, Y = beltY, Rotation = 1 });
        sim.Step();

        for (int t = 0; t < 200; t++) sim.Step();

        var lineId = sim.Belts.GetLineAt(beltX, beltY);
        Assert.True(lineId.IsValid);
        var line = sim.Belts.GetLine(lineId);
        bool hasItem = line.LaneA.Count > 0 || line.LaneB.Count > 0;
        Assert.True(hasItem, "expected the drill's output to have landed on the belt at (2,2)");
    }

    [Fact]
    public void MiningDrill_ParallelToBelt_OutputsToRightLane()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        var (drillX, drillY) = FindPoweredOreOrigin(sim);
        sim.Submit(PlaceDrill(sim, drillX, drillY, rotation: 1));   // 朝东输出到 (drillX+2, drillY)
        int beltX = drillX + 2, beltY = drillY;
        sim.Submit(new Command { Type = CommandType.PlaceEntity,
            ProtoId = sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id,
            X = beltX, Y = beltY, Rotation = 1 });                  // 带也向东 -> 平行
        sim.Step();
        for (int t = 0; t < 200; t++) sim.Step();

        var line = sim.Belts.GetLine(sim.Belts.GetLineAt(beltX, beltY));
        Assert.True(line.LaneB.Count > 0, "平行进料应落右侧 LaneB");
        Assert.Equal(0, line.LaneA.Count);                          // 不回退到另一条
    }

    [Fact]
    public void MiningDrill_OrthogonalToBelt_OutputsToFarLane()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        var (drillX, drillY) = FindPoweredOreOrigin(sim);
        sim.Submit(PlaceDrill(sim, drillX, drillY, rotation: 1));   // 朝东输出到 (drillX+2, drillY)
        int beltX = drillX + 2, beltY = drillY;
        sim.Submit(new Command { Type = CommandType.PlaceEntity,
            ProtoId = sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id,
            X = beltX, Y = beltY, Rotation = 2 });                  // 带向南 -> 与"朝东"正交
        sim.Step();
        for (int t = 0; t < 200; t++) sim.Step();

        var line = sim.Belts.GetLine(sim.Belts.GetLineAt(beltX, beltY));
        // 带向南行进,右手法线 = 西;采矿机在带西侧往东怼 -> 远端在东 = 左侧 LaneA。
        Assert.True(line.LaneA.Count > 0, "正交进料应落远端 LaneA");
        Assert.Equal(0, line.LaneB.Count);
    }

    [Fact]
    public void MiningDrill_BlockedByFullBeltLane_StaysAwake_UnlikeChestBlocked()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        var (drillX, drillY) = FindPoweredOreOrigin(sim);
        sim.Submit(PlaceDrill(sim, drillX, drillY, rotation: 1));   // 朝东输出到 (drillX+2, drillY)
        int beltX = drillX + 2, beltY = drillY;
        sim.Submit(new Command { Type = CommandType.PlaceEntity,
            ProtoId = sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id,
            X = beltX, Y = beltY, Rotation = 1 });                  // 单格带,无下游消费者
        sim.Step();

        var drillId = sim.World.GetEntityAt(drillX, drillY);

        // 单格带长 256 subtiles,每个物品占 64 subtiles -> 单条 lane 最多塞 4 个。
        // 没有下游消费者,跑够久之后这条 lane 必然被喂满,TryInsertAtBack 之后
        // 每次都失败——这正是"堵在传送带里"的稳态。
        for (int t = 0; t < 600; t++) sim.Step();

        var line = sim.Belts.GetLine(sim.Belts.GetLineAt(beltX, beltY));
        int itemsOnLine = line.LaneA.Count + line.LaneB.Count;
        Assert.True(itemsOnLine > 0, "expected the belt lane to have received at least one item");

        // 案例 C(堵在传送带里):设计明确要求维持现状,不睡眠——继续再跑一大段,
        // 采矿机应当全程保持清醒(只会被传送带满卡住,不会被 MarkAsleep)。
        for (int t = 0; t < 200; t++)
        {
            sim.Step();
            Assert.True(sim.MiningDrills.IsAwake(drillId), $"drill blocked by a full belt lane must stay awake (tick {t})");
        }
    }

    [Fact]
    public void Inserter_ParallelToBelt_DropsToRightLane()
    {
        var sim = NewSim();
        PlacePoweredInserterInfra(sim);
        sim.Submit(PlaceChest(sim, 0, 2));                          // 抓取源
        sim.Submit(PlaceInserter(sim, 1, 2, rotation: 1));          // 机械臂朝东
        sim.Submit(PlaceBelt(sim, 2, 2, 1));                        // 下游带向东 -> 平行
        sim.Step();
        int iron = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(0, 2)))
            .Insert(iron, 3, sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize);
        for (int t = 0; t < 200; t++) sim.Step();

        var line = sim.Belts.GetLine(sim.Belts.GetLineAt(2, 2));
        Assert.True(line.LaneB.Count > 0, "平行放件应落右侧 LaneB");
        Assert.Equal(0, line.LaneA.Count);                          // 不回退到另一条
    }

    [Fact]
    public void Inserter_OrthogonalToBelt_DropsToFarLane()
    {
        var sim = NewSim();
        PlacePoweredInserterInfra(sim);
        sim.Submit(PlaceChest(sim, 0, 2));
        sim.Submit(PlaceInserter(sim, 1, 2, rotation: 1));          // 机械臂朝东
        sim.Submit(PlaceBelt(sim, 2, 2, 2));                        // 下游带向南 -> 正交
        sim.Step();
        int iron = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(0, 2)))
            .Insert(iron, 3, sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize);
        for (int t = 0; t < 200; t++) sim.Step();

        var line = sim.Belts.GetLine(sim.Belts.GetLineAt(2, 2));
        // 带向南,右手法线 = 西;机械臂在带西侧朝东放 -> 远端在东 = 左侧 LaneA。
        Assert.True(line.LaneA.Count > 0, "正交放件应落远端 LaneA");
        Assert.Equal(0, line.LaneB.Count);
    }

    [Fact]
    public void MiningDrill_NoTargetInFootprint_NeverProgresses()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        // (50,50) 起的 2x2 区域:若碰巧有矿,换一个更偏远的坐标直到全空(矿脉是无限惰性生成,
        // 但任意固定 2x2 区域全空的概率不为零——用一个大坐标降低撞上矿脉密集区的概率)。
        // Task 2 添加建筑原型导致 resource id 变化,影响伪随机生成。坐标更新为 (10000,10000)。
        sim.Submit(PlaceDrill(sim, 10000, 10000, rotation: 1));
        sim.Step();

        var drillId = sim.World.GetEntityAt(10000, 10000);
        bool anyResource = false;
        for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++)
                if (!sim.Resources.GetResourceAt(10000 + dx, 10000 + dy).IsEmpty) anyResource = true;
        Assert.False(anyResource, "test assumes (10000,10000)-(10001,10001) has no ore under the default seed — pick a different far-away coordinate if this ever becomes false");

        for (int t = 0; t < 50; t++) sim.Step();

        Assert.Equal(-1, sim.MiningDrills.GetTargetX(drillId));
        Assert.Equal(0, sim.MiningDrills.GetProgress(drillId));
    }

    [Fact]
    public void MiningDrill_DestroyedMidCycle_UnregistersCleanly()
    {
        var sim = NewSim();
        sim.Submit(PlaceDrill(sim, 10, 10, rotation: 1));
        sim.Step();
        var drillId = sim.World.GetEntityAt(10, 10);

        sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 10, Y = 10 });
        sim.Step();

        Assert.Equal(-1, sim.MiningDrills.GetTargetX(drillId)); // 反查:摘除后读默认值,不抛异常
        Assert.False(sim.World.GetEntityAt(10, 10).IsValid);
    }

    // C1 regression: a drill's locked target tile emptied by someone else (player
    // hand-mining the ore under the drill, a neighbouring drill, ...) between the
    // drill locking it and the drill completing its cycle. Post-settle used to
    // read the now-empty cell and cast prototype id 0 (AssemblingMachinePrototype)
    // to ResourcePrototype, throwing InvalidCastException straight out of Step().
    // Unpowered drill is the simplest trigger — the cast happened before power was
    // ever consulted.
    [Fact]
    public void MiningDrill_LockedTargetEmptiedExternally_DoesNotThrow_ReTargets()
    {
        var sim = NewSim();
        // (-1,-2) 是默认种子下靠近原点、稳定有矿的 2x2 区域(同其它 MiningDrill_* 测试
        // 用 FindPoweredOreOrigin 探测出的同一块矿——这条测试特意不接电,不需要走
        // 供电范围搜索,直接复用这个已知坐标)。
        sim.Submit(PlaceDrill(sim, -1, -2, rotation: 1)); // no power infra — unpowered on purpose
        sim.Step();

        var drillId = sim.World.GetEntityAt(-1, -2);
        int tx = sim.MiningDrills.GetTargetX(drillId), ty = sim.MiningDrills.GetTargetY(drillId);
        Assert.NotEqual(-1, tx); // default seed has ore under (-1,-2)-(0,-1); drill locks it regardless of power

        var locked = sim.Resources.GetResourceAt(tx, ty);
        sim.Resources.Extract(tx, ty, locked.Amount); // someone else empties the locked tile
        Assert.True(sim.Resources.GetResourceAt(tx, ty).IsEmpty);

        var ex = Record.Exception(() => sim.Step());
        Assert.Null(ex); // used to be InvalidCastException out of Step()
        Assert.Equal(-1, sim.MiningDrills.GetTargetX(drillId)); // dropped the dead tile, will re-search next tick
    }

    [Fact]
    public void MiningDrill_ExhaustedTargetTile_AutoReTargetsWithinFootprint()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        // 需要 >=2 个矿格,保证挖空第一个目标后 footprint 内还有第二个可重新锁定。
        var (drillX, drillY) = FindPoweredOreOrigin(sim, minOreTiles: 2);

        sim.Submit(PlaceDrill(sim, drillX, drillY, rotation: 1));
        int chestX = drillX + 2, chestY = drillY;
        sim.Submit(PlaceChest(sim, chestX, chestY));
        sim.Step();

        var drillId = sim.World.GetEntityAt(drillX, drillY);
        var chestId = sim.World.GetEntityAt(chestX, chestY);
        var chestInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(chestId));

        int firstTx = sim.MiningDrills.GetTargetX(drillId), firstTy = sim.MiningDrills.GetTargetY(drillId);
        Assert.NotEqual(-1, firstTx);

        // Exhaust the locked tile out from under the drill.
        var lockedCell = sim.Resources.GetResourceAt(firstTx, firstTy);
        sim.Resources.Extract(firstTx, firstTy, lockedCell.Amount);

        for (int t = 0; t < 200; t++) sim.Step();

        int newTx = sim.MiningDrills.GetTargetX(drillId), newTy = sim.MiningDrills.GetTargetY(drillId);
        Assert.NotEqual(-1, newTx);                                     // didn't get stuck idle
        Assert.False(newTx == firstTx && newTy == firstTy);             // moved off the exhausted tile
        Assert.InRange(newTx, drillX, drillX + 1);                      // still inside the 2x2 footprint
        Assert.InRange(newTy, drillY, drillY + 1);
        Assert.False(sim.Resources.GetResourceAt(newTx, newTy).IsEmpty);
        Assert.True(chestInv.TotalItems() > 0);                         // kept producing across the re-target
    }

    [Fact]
    public void MiningDrill_OutputBlocked_HoldsPendingThenResumesWhenSpaceFrees()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        var (drillX, drillY) = FindPoweredOreOrigin(sim);
        sim.Submit(PlaceDrill(sim, drillX, drillY, rotation: 1));
        int chestX = drillX + 2, chestY = drillY;
        sim.Submit(PlaceChest(sim, chestX, chestY));
        sim.Step();

        var drillId = sim.World.GetEntityAt(drillX, drillY);
        var chestId = sim.World.GetEntityAt(chestX, chestY);
        var chestInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(chestId));
        Assert.NotEqual(-1, sim.MiningDrills.GetTargetX(drillId)); // has ore to mine

        // Fill the output chest completely so the finished item has nowhere to go.
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        int coalStack = sim.Prototypes.Get<ItemPrototype>("coal").StackSize;
        int cap = chestInv.SlotCount * coalStack;
        Assert.Equal(cap, chestInv.Insert(coal, cap, coalStack));

        for (int t = 0; t < 200; t++) sim.Step(); // well past one 60-tick cycle at full power

        Assert.True(sim.MiningDrills.IsCompleted(drillId));           // cycle done, item pending
        long heldProgress = sim.MiningDrills.GetProgress(drillId);
        for (int t = 0; t < 50; t++) sim.Step();
        Assert.True(sim.MiningDrills.IsCompleted(drillId));           // still held, not dropped
        Assert.Equal(heldProgress, sim.MiningDrills.GetProgress(drillId)); // progress frozen

        chestInv.Remove(coal, coalStack); // free one slot
        // Direct inventory manipulation bypasses the 3 real wake triggers (RotateEntity/
        // TransferFromEntity/inserter grab) — explicitly wake the drill waiting on this
        // output tile, per the established "direct inventory manipulation" test convention.
        sim.MiningDrills.WakeWaitersAt(chestX, chestY);
        sim.Step();

        Assert.False(sim.MiningDrills.IsCompleted(drillId));          // resumed
        Assert.True(sim.MiningDrills.GetProgress(drillId) < heldProgress); // fresh cycle (one tick in), not the held full-cycle value
        Assert.True(chestInv.TotalItems() > (chestInv.SlotCount - 1) * coalStack); // pending item flushed in
    }

    [Fact]
    public void MiningDrill_MissedWakeCall_SelfHealsWithinSafetyNetWindow()
    {
        // Spec §8's safety-net self-heal test (final-review Important #1): simulate a
        // "missed wake call" bug — output space frees up but NONE of the 3 production
        // wake triggers (RotateEntity/TransferFromEntity/inserter grab) and no explicit
        // MiningDrills.WakeWaitersAt run. The drill must still resume within
        // SafetyNetIntervalTicks ticks via the 60-tick modulo fallback in
        // MiningDrillsTickPreSettle, or it would be stuck asleep forever.
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        var (drillX, drillY) = FindPoweredOreOrigin(sim);
        sim.Submit(PlaceDrill(sim, drillX, drillY, rotation: 1));
        int chestX = drillX + 2, chestY = drillY;
        sim.Submit(PlaceChest(sim, chestX, chestY));
        sim.Step();

        var drillId = sim.World.GetEntityAt(drillX, drillY);
        var chestId = sim.World.GetEntityAt(chestX, chestY);
        var chestInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(chestId));
        Assert.NotEqual(-1, sim.MiningDrills.GetTargetX(drillId)); // has ore to mine

        // Fill the output chest completely so the finished item has nowhere to go.
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        int coalStack = sim.Prototypes.Get<ItemPrototype>("coal").StackSize;
        int cap = chestInv.SlotCount * coalStack;
        Assert.Equal(cap, chestInv.Insert(coal, cap, coalStack));

        for (int t = 0; t < 200; t++) sim.Step(); // well past one 60-tick cycle at full power

        Assert.True(sim.MiningDrills.IsCompleted(drillId)); // cycle done, item pending
        Assert.False(sim.MiningDrills.IsAwake(drillId));    // registered as blocked-output waiter, asleep

        chestInv.Remove(coal, coalStack); // free one slot — NO WakeWaitersAt call, no other trigger.

        // The safety net wakes drill `id` on the tick where
        // id.Index % SafetyNetIntervalTicks == Tick % SafetyNetIntervalTicks. Tick % 60
        // sweeps every residue 0..59 exactly once across any 60 consecutive ticks, so
        // stepping exactly SafetyNetIntervalTicks ticks from here is guaranteed to hit
        // the drill's residue at least once, regardless of the current tick's phase.
        for (int t = 0; t < MiningDrills.SafetyNetIntervalTicks; t++) sim.Step();

        Assert.True(sim.MiningDrills.IsAwake(drillId));      // safety net woke it with nothing else telling it to
        Assert.False(sim.MiningDrills.IsCompleted(drillId)); // pending item flushed, fresh cycle started
        Assert.True(chestInv.TotalItems() > (chestInv.SlotCount - 1) * coalStack); // pending item flushed in
    }

    [Fact]
    public void MiningDrill_UnderpoweredSatisfaction_TakesTwiceAsLong()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        var (drillX, drillY) = FindPoweredOreOrigin(sim);
        sim.Submit(PlaceDrill(sim, drillX, drillY, rotation: 1));
        int chestX = drillX + 2, chestY = drillY;
        sim.Submit(PlaceChest(sim, chestX, chestY));
        sim.Step();

        var drillId = sim.World.GetEntityAt(drillX, drillY);
        var chestId = sim.World.GetEntityAt(chestX, chestY);
        var chestInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(chestId));
        Assert.NotEqual(-1, sim.MiningDrills.GetTargetX(drillId));

        // A competing PrimaryInput consumer demanding exactly the drill's own draw
        // (both "90kW" = 1500 J/tick) doubles demand against the generator's fixed
        // 1500 J/tick supply -> both settle to satisfaction 0.5 -> progress at half
        // rate (Settle() broadcasts one ratio per tier — see P9's
        // Machine_UnderpoweredSatisfaction_TakesTwiceAsLong).
        long drillDemand = sim.Prototypes.Get<MiningDrillPrototype>("electric-mining-drill").EnergyUsageJPerTick;
        var fakeConsumer = new EntityId(999, 1);

        int tick = 0;
        while (chestInv.TotalItems() == 0 && tick < 400)
        {
            sim.ElectricGrid.RegisterDemand(fakeConsumer, 0, 0, UsagePriority.PrimaryInput, drillDemand);
            sim.Step();
            tick++;
        }

        // Full-power baseline is ~61 ticks (60-tick resource + 1 flush); at half
        // power ~121 + 1. Bracket generously (like P9's underpowered test) so this
        // catches "throttling ignored" without pinning an exact flush off-by-one.
        Assert.InRange(tick, 90, 170);
    }

    private static Command PlaceInserter(Simulation sim, int x, int y, byte rotation) => new()
    {
        Type = CommandType.PlaceEntity,
        ProtoId = sim.Prototypes.Get<InserterPrototype>("inserter-basic").Id,
        X = x, Y = y, Rotation = rotation,
    };

    // 电线杆(0,0) + 发电机(2,0)充好煤。机械臂/机器放在 y>=2 处避开 infra footprint。
    private static void PlacePoweredInserterInfra(Simulation sim)
    {
        sim.Submit(PlacePole(sim, 0, 0));
        sim.Submit(PlaceGenerator(sim, 2, 0));
        sim.Step();
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        int coalStack = sim.Prototypes.Get<ItemPrototype>("coal").StackSize;
        sim.Player.Inventory.Insert(coal, 5, coalStack);
        sim.Submit(TransferTo(2, 0, coal, 5));
        sim.Step();
    }

    [Fact]
    public void Inserter_ChestToChest_MovesOneItemPerCycle()
    {
        var sim = NewSim();
        PlacePoweredInserterInfra(sim);
        // 抓取箱(0,2) — 机械臂(1,2) 朝东(rotation 1) — 放置箱(2,2)
        sim.Submit(PlaceChest(sim, 0, 2));
        sim.Submit(PlaceInserter(sim, 1, 2, rotation: 1));
        sim.Submit(PlaceChest(sim, 2, 2));
        sim.Step();

        int iron = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int ironStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;
        var srcInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(0, 2)));
        var dstInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(2, 2)));
        srcInv.Insert(iron, 3, ironStack);

        // rotationTimeSeconds 0.5 -> RotationSpeed.Raw = 65536/30 = 2184/tick at full power;
        // half-swing (>= 65536) takes 31 ticks, full pick-to-pick cycle ~63 ticks + grab/place.
        // 3 items ~= 190 ticks; run 280 for comfortable slack.
        for (int t = 0; t < 280; t++) sim.Step();

        Assert.Equal(3, dstInv.CountOf(iron));
        Assert.Equal(0, srcInv.CountOf(iron));
    }

    [Fact]
    public void Inserter_SwingProgressAdvancesThroughThreePhases()
    {
        var sim = NewSim();
        PlacePoweredInserterInfra(sim);
        sim.Submit(PlaceChest(sim, 0, 2));
        sim.Submit(PlaceInserter(sim, 1, 2, rotation: 1));
        sim.Submit(PlaceChest(sim, 2, 2));
        sim.Step();

        int iron = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(0, 2)))
            .Insert(iron, 1, sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize);

        var insId = sim.World.GetEntityAt(1, 2);
        sim.Step(); // grab happens this tick (phase A) -> held set, progress 0
        Assert.Equal(iron, sim.Inserters.GetHeldItemProtoId(insId));

        // swing out: held != 0, progress climbs
        for (int t = 0; t < 15; t++) sim.Step();
        Assert.True(sim.Inserters.GetSwingProgress(insId) > 0);
        Assert.True(sim.Inserters.GetSwingProgress(insId) < Inserters.HalfSwing);

        // enough more ticks to place and start swinging back: hand cleared, progress >= HalfSwing
        for (int t = 0; t < 30; t++) sim.Step();
        Assert.Equal(0, sim.Inserters.GetHeldItemProtoId(insId));
        Assert.True(sim.Inserters.GetSwingProgress(insId) >= Inserters.HalfSwing);
    }

    [Fact]
    public void Inserter_BeltToBelt_MovesItemWithCorrectType()
    {
        var sim = NewSim();
        PlacePoweredInserterInfra(sim);
        // 上游带(0,2) 朝东; 机械臂(1,2) 朝东; 下游带(2,2) 朝东
        sim.Submit(PlaceBelt(sim, 0, 2, 1));
        sim.Submit(PlaceInserter(sim, 1, 2, rotation: 1));
        sim.Submit(PlaceBelt(sim, 2, 2, 1));
        sim.Step();

        int iron = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        // put an item on the upstream belt lane near the inserter's pickup tile (tile index 0, its only tile)
        var upLine = sim.Belts.GetLine(sim.Belts.GetLineAt(0, 2));
        Assert.True(upLine.LaneA.TryInsertAt(128, iron));   // centered on tile (0,2)

        for (int t = 0; t < 220; t++) sim.Step();

        var downLine = sim.Belts.GetLine(sim.Belts.GetLineAt(2, 2));
        bool onDown = downLine.LaneA.Count > 0 || downLine.LaneB.Count > 0;
        Assert.True(onDown, "expected the inserter to have moved the item onto the downstream belt");
        // confirm type survived: whichever lane has it, its front (after enough ticks it reaches the exit) is iron
        for (int t = 0; t < 40; t++) sim.Step();
        int frontType = downLine.LaneA.Count > 0 && downLine.LaneA.IsFrontReady ? downLine.LaneA.FrontItemProtoId
                      : downLine.LaneB.Count > 0 && downLine.LaneB.IsFrontReady ? downLine.LaneB.FrontItemProtoId
                      : iron; // if it hasn't reached the exit yet, don't fail on that alone
        Assert.Equal(iron, frontType);
    }

    [Fact]
    public void Inserter_IntoFurnaceInput_UsesRole1_OutOfFurnaceOutput_UsesRole2()
    {
        var sim = NewSim();
        PlacePoweredInserterInfra(sim);
        // 抓取箱(0,2) -> 机械臂(1,2)朝东 -> 熔炉(2,2)(输入=role1)
        sim.Submit(PlaceChest(sim, 0, 2));
        sim.Submit(PlaceInserter(sim, 1, 2, rotation: 1));
        sim.Submit(PlaceFurnace(sim, 2, 2));
        sim.Step();

        int ore = sim.Prototypes.Get<ItemPrototype>("iron-ore").Id;
        var srcInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(0, 2)));
        srcInv.Insert(ore, 1, sim.Prototypes.Get<ItemPrototype>("iron-ore").StackSize);

        var furnaceId = sim.World.GetEntityAt(2, 2);
        var furnaceInput = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 1));

        for (int t = 0; t < 120; t++) sim.Step();

        Assert.True(furnaceInput.CountOf(ore) > 0);   // the inserter put ore into role 1, not role 2 or a nonexistent slot
        Assert.Equal(0, srcInv.CountOf(ore));
    }

    [Fact]
    public void Inserter_OutOfFurnaceOutput_UsesRole2()
    {
        var sim = NewSim();
        PlacePoweredInserterInfra(sim);
        // 机械臂(1,2)朝西(rotation 3):pickup = 身后(2,2) = 熔炉输出(role2);dropoff = 身前(0,2) = 箱
        sim.Submit(PlaceChest(sim, 0, 2));
        sim.Submit(PlaceInserter(sim, 1, 2, rotation: 3));
        sim.Submit(PlaceFurnace(sim, 2, 2));
        sim.Step();

        int plate = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int plateStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;

        var furnaceId = sim.World.GetEntityAt(2, 2);
        var furnaceOut = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 2));   // role 2 = output
        var furnaceInput = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 1)); // role 1 = input, never seeded
        var dstInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(0, 2)));
        furnaceOut.Insert(plate, 3, plateStack);

        for (int t = 0; t < 200; t++) sim.Step();

        Assert.True(dstInv.CountOf(plate) > 0);            // the inserter delivered items to the chest
        Assert.True(furnaceOut.CountOf(plate) < 3);        // pulled from role 2 (output)
        Assert.Equal(3, dstInv.CountOf(plate) + furnaceOut.CountOf(plate)); // conservation: nothing lost
        Assert.Equal(0, furnaceInput.CountOf(plate));      // role 1 (input) never touched
    }

    [Fact]
    public void Inserter_DropoffBlocked_HoldsItemUntilSpaceFrees()
    {
        var sim = NewSim();
        PlacePoweredInserterInfra(sim);
        sim.Submit(PlaceChest(sim, 0, 2));
        sim.Submit(PlaceInserter(sim, 1, 2, rotation: 1));
        sim.Submit(PlaceChest(sim, 2, 2));
        sim.Step();

        int iron = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int ironStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;
        var srcInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(0, 2)));
        var dstInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(2, 2)));
        srcInv.Insert(iron, 1, ironStack);
        // fill the destination chest completely
        for (int s = 0; s < dstInv.SlotCount; s++) dstInv.Insert(iron, ironStack, ironStack);

        var insId = sim.World.GetEntityAt(1, 2);
        for (int t = 0; t < 100; t++) sim.Step();

        Assert.Equal(iron, sim.Inserters.GetHeldItemProtoId(insId));                 // still holding
        Assert.True(sim.Inserters.GetSwingProgress(insId) >= Inserters.HalfSwing);   // stuck at the drop angle

        // free one stack; the inserter should place and then complete the cycle
        dstInv.Remove(iron, ironStack);
        for (int t = 0; t < 120; t++) sim.Step();
        Assert.Equal(0, sim.Inserters.GetHeldItemProtoId(insId));
        Assert.True(dstInv.CountOf(iron) > (dstInv.SlotCount - 1) * ironStack); // gained the held item back
    }

    [Fact]
    public void Inserter_UnderpoweredSatisfaction_TakesRoughlyTwiceAsLong()
    {
        var sim = NewSim();
        PlacePoweredInserterInfra(sim);
        sim.Submit(PlaceChest(sim, 0, 2));
        sim.Submit(PlaceInserter(sim, 1, 2, rotation: 1));
        sim.Submit(PlaceChest(sim, 2, 2));
        sim.Step();

        int iron = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        var srcInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(0, 2)));
        var dstInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(2, 2)));
        srcInv.Insert(iron, 1, sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize);

        // The generator produces genOut J/tick; the inserter's own draw is tiny (~83 J/tick).
        // To drive satisfaction to ~0.5 the whole network's demand must be ~2x supply, so the
        // fake competing consumer demands (2*genOut - inserterDemand) — total = 2*genOut,
        // supply = genOut, satisfaction = 0.5 uniformly across the PrimaryInput tier.
        long genOut = sim.Prototypes.Get<FuelGeneratorPrototype>("burner-generator").PowerOutputJPerTick;
        long inserterDemand = sim.Prototypes.Get<InserterPrototype>("inserter-basic").EnergyUsageJPerTick;
        long fakeDemand = 2 * genOut - inserterDemand;
        var fakeConsumer = new EntityId(9999, 1);

        int tick = 0;
        while (dstInv.CountOf(iron) == 0 && tick < 400)
        {
            sim.ElectricGrid.RegisterDemand(fakeConsumer, 0, 0, UsagePriority.PrimaryInput, fakeDemand);
            sim.Step();
            tick++;
        }

        // Full-power first delivery is ~half a cycle (grab @ tick 1, place @ ~tick 32). Halved
        // satisfaction roughly doubles the swing-out phase to ~62. Wide bracket, comfortably clear
        // of the full-power ~32 on the low side and a stuck/hung inserter (400 cap) on the high side.
        Assert.InRange(tick, 48, 130);
    }

    [Fact]
    public void Inserter_DestroyedWhileHoldingItem_UnregistersCleanly()
    {
        var sim = NewSim();
        PlacePoweredInserterInfra(sim);
        sim.Submit(PlaceChest(sim, 0, 2));
        sim.Submit(PlaceInserter(sim, 1, 2, rotation: 1));
        sim.Submit(PlaceChest(sim, 2, 2));
        sim.Step();

        int iron = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(0, 2)))
            .Insert(iron, 1, sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize);

        var insId = sim.World.GetEntityAt(1, 2);
        sim.Step();   // grab
        Assert.Equal(iron, sim.Inserters.GetHeldItemProtoId(insId));

        sim.Submit(new Command { Type = CommandType.RemoveEntity, X = 1, Y = 2 });
        var ex = Record.Exception(() => sim.Step());

        Assert.Null(ex);
        Assert.Equal(0, sim.Inserters.GetHeldItemProtoId(insId));   // state gone, held item silently discarded
        Assert.False(sim.World.GetEntityAt(1, 2).IsValid);
    }

    // --- RotateEntity ---------------------------------------------------

    private static Command RotateEntity(int x, int y, byte rotation) => new()
    {
        Type = CommandType.RotateEntity, X = x, Y = y, Rotation = rotation,
    };

    [Fact]
    public void RotateEntity_OutOfReach_Rejected()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 20, 0));   // 玩家在 (0,0),ReachSubTiles 1536 = 6 tile,远超够不着
        sim.Step();
        int before = sim.RejectedCommandCount;

        sim.Submit(RotateEntity(20, 0, 1));
        sim.Step();

        Assert.Equal(before + 1, sim.RejectedCommandCount);
    }

    [Fact]
    public void RotateEntity_NonSquareFootprint_Rejected()
    {
        var sim = NewSim();
        sim.Submit(new Command
        {
            Type = CommandType.PlaceEntity,
            ProtoId = sim.Prototypes.Get<ContainerPrototype>("large-chest").Id,   // 2x3,非方形
            X = 0, Y = 0,
        });
        sim.Step();
        int before = sim.RejectedCommandCount;

        sim.Submit(RotateEntity(0, 0, 1));
        sim.Step();

        Assert.Equal(before + 1, sim.RejectedCommandCount);
    }

    [Fact]
    public void RotateEntity_SameRotation_IsNoOpAndNotRejected()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 0, 0));
        sim.Step();
        int before = sim.RejectedCommandCount;

        sim.Submit(RotateEntity(0, 0, 0));   // 箱子放置时就是 rotation 0
        sim.Step();

        Assert.Equal(before, sim.RejectedCommandCount);   // 没被拒绝
    }

    [Fact]
    public void RotateEntity_Belt_ChangesDirectionAndDiscardsExistingItems()
    {
        var sim = NewSim();
        sim.Submit(PlaceBelt(sim, 0, 0, E));   // 东
        sim.Step();

        // 塞一个物品到两条 lane 上,验证转向后被丢弃(同 RemoveBelt 既有约定)。
        var line = sim.Belts.GetLine(sim.Belts.GetLineAt(0, 0));
        int ore = sim.Prototypes.Get<ItemPrototype>("iron-ore").Id;
        line.LaneA.TryInsertAtBack(ore);
        line.LaneB.TryInsertAtBack(ore);

        sim.Submit(RotateEntity(0, 0, 2));   // 南
        sim.Step();

        var newLineId = sim.Belts.GetLineAt(0, 0);
        Assert.True(newLineId.IsValid);
        var newLine = sim.Belts.GetLine(newLineId);
        Assert.Equal(2, newLine.Direction);
        Assert.Equal(0, newLine.LaneA.Count);
        Assert.Equal(0, newLine.LaneB.Count);
    }

    [Fact]
    public void RotateEntity_MiningDrill_Idle_AppliesImmediately()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        var (drillX, drillY) = FindPoweredOreOrigin(sim);
        sim.Submit(PlaceDrill(sim, drillX, drillY, rotation: 1));         // 朝东,输出到 (drillX+2, drillY)
        int oldOutX = drillX + 2, oldOutY = drillY;
        int newOutX = drillX, newOutY = drillY + 2;                      // 新输出格(朝南 rotation=2 时)
        sim.Submit(PlaceChest(sim, oldOutX, oldOutY));                   // 旧输出格
        sim.Submit(PlaceChest(sim, newOutX, newOutY));                   // 新输出格
        sim.Step();

        var drillId = sim.World.GetEntityAt(drillX, drillY);
        var oldOutInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(oldOutX, oldOutY)));
        var newOutInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(newOutX, newOutY)));

        // 还没挖完(!IsCompleted)时转向 —— 不在危险窗口,立即生效。
        Assert.False(sim.MiningDrills.IsCompleted(drillId));
        sim.Submit(RotateEntity(drillX, drillY, 2));   // 南
        sim.Step();

        int tick = 0;
        while (oldOutInv.TotalItems() == 0 && newOutInv.TotalItems() == 0 && tick < 400)
        {
            sim.Step();
            tick++;
        }

        Assert.Equal(0, oldOutInv.TotalItems());     // 没有排到旧方向
        Assert.True(newOutInv.TotalItems() > 0);     // 排到了新方向,说明立即生效
    }

    [Fact]
    public void RotateEntity_MiningDrill_WhileCompleted_DefersUntilFlushed()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        // 这个用例要跑完整整两轮挖矿周期 + 若干轮询,PlacePoweredDrillInfra 自带的 5 块煤
        // (~222 tick 满功率预算)撑不住,直接给发电机灌满,把燃料排除在变量之外。
        sim.ElectricGrid.SetFuelBufferJ(sim.World.GetEntityAt(2, 0), 1_000_000_000L);
        var (drillX, drillY) = FindPoweredOreOrigin(sim);
        int oldOutX = drillX + 2, oldOutY = drillY;
        int newOutX = drillX, newOutY = drillY + 2;
        sim.Submit(PlaceDrill(sim, drillX, drillY, rotation: 1));   // 朝东,输出到 (oldOutX, oldOutY)
        sim.Submit(PlaceChest(sim, oldOutX, oldOutY));              // 旧输出格
        sim.Submit(PlaceChest(sim, newOutX, newOutY));              // 新输出格(朝南)
        sim.Step();

        var drillId = sim.World.GetEntityAt(drillX, drillY);
        Assert.True(sim.World.GetEntityAt(oldOutX, oldOutY).IsValid, "old output chest missing");
        Assert.True(sim.World.GetEntityAt(newOutX, newOutY).IsValid, "new output chest missing");
        Assert.NotEqual(-1, sim.MiningDrills.GetTargetX(drillId));
        var oldOutInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(oldOutX, oldOutY)));
        var newOutInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(newOutX, newOutY)));

        // 堵住旧输出格,逼它停在"挖完待排出"的危险窗口里。
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        int coalStack = sim.Prototypes.Get<ItemPrototype>("coal").StackSize;
        oldOutInv.Insert(coal, oldOutInv.SlotCount * coalStack, coalStack);

        int tick = 0;
        while (!sim.MiningDrills.IsCompleted(drillId) && tick < 400) { sim.Step(); tick++; }
        Assert.True(sim.MiningDrills.IsCompleted(drillId), $"never completed after {tick} ticks; progress={sim.MiningDrills.GetProgress(drillId)}, targetX={sim.MiningDrills.GetTargetX(drillId)}");

        // 挖完待排出时发转向:排队,不立即改朝向(排出格还没落地)。
        sim.Submit(RotateEntity(drillX, drillY, 2));   // 南
        sim.Step();
        Assert.True(sim.MiningDrills.IsCompleted(drillId));   // 仍卡在旧朝向的危险窗口

        // 腾出旧输出格 —— flush 必须走旧朝向落进旧箱子,不能瞬移到新朝向。
        // (旧箱子腾位后仍剩不少煤,TotalItems() 不会归 0,用 IsCompleted 归 false 判断 flush 是否发生。)
        int totalBeforeFlush = oldOutInv.TotalItems();
        oldOutInv.Remove(coal, coalStack);
        // Direct inventory manipulation bypasses the 3 real wake triggers (RotateEntity here
        // only queues the pending rotation, it doesn't wake a completed drill by design) —
        // explicitly wake the drill waiting on this output tile, per the established
        // "direct inventory manipulation" test convention.
        sim.MiningDrills.WakeWaitersAt(oldOutX, oldOutY);
        tick = 0;
        while (sim.MiningDrills.IsCompleted(drillId) && tick < 50) { sim.Step(); tick++; }
        Assert.False(sim.MiningDrills.IsCompleted(drillId));               // flush 成功
        Assert.True(oldOutInv.TotalItems() > totalBeforeFlush - coalStack); // 挖出的那件确实进了旧箱子
        Assert.Equal(0, newOutInv.TotalItems());

        // flush 之后排队的转向生效,后续产出走新方向。
        int oldAfterFirstFlush = oldOutInv.TotalItems();
        tick = 0;
        while (newOutInv.TotalItems() == 0 && oldOutInv.TotalItems() == oldAfterFirstFlush && tick < 400) { sim.Step(); tick++; }
        Assert.True(newOutInv.TotalItems() > 0,
            $"tick={tick} old={oldOutInv.TotalItems()}(was {oldAfterFirstFlush}) targetX={sim.MiningDrills.GetTargetX(drillId)} " +
            $"targetY={sim.MiningDrills.GetTargetY(drillId)} completed={sim.MiningDrills.IsCompleted(drillId)} progress={sim.MiningDrills.GetProgress(drillId)}");
    }

    [Fact]
    public void RotateEntity_Inserter_Idle_AppliesImmediately_ReversesFlow()
    {
        var sim = NewSim();
        PlacePoweredInserterInfra(sim);
        sim.Submit(PlaceChest(sim, 0, 2));
        sim.Submit(PlaceInserter(sim, 1, 2, rotation: 1));   // 东:behind=(0,2) ahead=(2,2)
        sim.Submit(PlaceChest(sim, 2, 2));
        sim.Step();

        int iron = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int ironStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;
        var chestA = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(0, 2)));
        var chestB = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(2, 2)));

        var insId = sim.World.GetEntityAt(1, 2);
        Assert.Equal(0, sim.Inserters.GetHeldItemProtoId(insId));   // 空闲,不在危险窗口

        // 转向西:behind/ahead 互换。
        sim.Submit(RotateEntity(1, 2, 3));
        sim.Step();

        // 现在把料放进 chestB(新 behind),验证搬运方向真的翻了。
        chestB.Insert(iron, 1, ironStack);
        int tick = 0;
        while (chestA.CountOf(iron) == 0 && tick < 280) { sim.Step(); tick++; }

        Assert.Equal(1, chestA.CountOf(iron));
        Assert.Equal(0, chestB.CountOf(iron));
    }

    [Fact]
    public void RotateEntity_Inserter_WhileHolding_DefersUntilHandEmpty_ThenAppliesForNextCycle()
    {
        var sim = NewSim();
        PlacePoweredInserterInfra(sim);
        sim.Submit(PlaceChest(sim, 0, 2));
        sim.Submit(PlaceInserter(sim, 1, 2, rotation: 1));   // 东:behind=(0,2) ahead=(2,2)
        sim.Submit(PlaceChest(sim, 2, 2));
        sim.Step();

        int iron = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int ironStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;
        var chestA = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(0, 2)));   // 旧 behind / 新 ahead
        var chestB = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(2, 2)));   // 旧 ahead / 新 behind
        chestA.Insert(iron, 1, ironStack);

        var insId = sim.World.GetEntityAt(1, 2);

        // 跑到刚抓到手(held != 0)就停 —— 进入危险窗口。
        int tick = 0;
        while (sim.Inserters.GetHeldItemProtoId(insId) == 0 && tick < 100) { sim.Step(); tick++; }
        Assert.NotEqual(0, sim.Inserters.GetHeldItemProtoId(insId));

        // 手上有东西时发转向:排队,不立即生效。
        sim.Submit(RotateEntity(1, 2, 3));   // 西
        sim.Step();

        // 继续跑到放下(held 归 0)——这一下必须用旧朝向落进 chestB,没有瞬移。
        tick = 0;
        while (sim.Inserters.GetHeldItemProtoId(insId) != 0 && tick < 200) { sim.Step(); tick++; }
        Assert.Equal(1, chestB.CountOf(iron));
        Assert.Equal(0, chestA.CountOf(iron));

        // 排队的转向在手空之后应用;给 chestB(新 behind)放料,验证下一轮方向真的翻了。
        // chestB 此时已有上一轮反向落下的 1 个,这里再插 1 个 -> 2 个;下面只跑到 chestA
        // 拿到第一个就停,所以 chestB 该剩 1 个(被搬走 1 个),不是 0。
        chestB.Insert(iron, 1, ironStack);
        int chestBBefore = chestB.CountOf(iron);
        tick = 0;
        while (chestA.CountOf(iron) == 0 && tick < 400) { sim.Step(); tick++; }
        Assert.Equal(1, chestA.CountOf(iron));
        Assert.Equal(chestBBefore - 1, chestB.CountOf(iron));
    }

    // --- 机器输入过滤(role 1) ------------------------------------------

    [Fact]
    public void TransferToEntity_AssemblerWithoutRecipe_InsertsNothing()
    {
        var sim = NewSim();
        sim.Submit(PlaceAssembler(sim, 0, 2));
        sim.Step();

        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int plateStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;
        sim.Player.Inventory.Insert(plateId, 2, plateStack);
        // player.json 的开局物资包本来就带 8 个 iron-plate,不能假设从 0 开始。
        int playerBefore = sim.Player.Inventory.CountOf(plateId);

        sim.Submit(TransferTo(0, 2, plateId, 2));
        sim.Step();

        var asmId = sim.World.GetEntityAt(0, 2);
        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(asmId, 1));
        Assert.Equal(0, inputInv.CountOf(plateId));                       // 没配方,不知道要收什么,一律拒收
        Assert.Equal(playerBefore, sim.Player.Inventory.CountOf(plateId)); // 玩家背包也没被扣
        Assert.Equal(0, sim.RejectedCommandCount);                        // 不算命令被拒,同"库存满"语义
    }

    [Fact]
    public void TransferToEntity_AssemblerWithRecipe_OnlyAcceptsCurrentIngredient()
    {
        var sim = NewSim();
        sim.Submit(PlaceAssembler(sim, 0, 2));
        sim.Step();

        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int plateStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;
        int oreId = sim.Prototypes.Get<ItemPrototype>("iron-ore").Id;
        int oreStack = sim.Prototypes.Get<ItemPrototype>("iron-ore").StackSize;
        int gearRecipeId = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel").Id;   // 原料:iron-plate

        sim.Submit(SetRecipe(0, 2, gearRecipeId));
        sim.Step();

        sim.Player.Inventory.Insert(plateId, 2, plateStack);
        sim.Player.Inventory.Insert(oreId, 2, oreStack);
        sim.Submit(TransferTo(0, 2, oreId, 2));    // 铁矿不是这个配方的原料
        sim.Submit(TransferTo(0, 2, plateId, 2));  // 铁板是
        sim.Step();

        var asmId = sim.World.GetEntityAt(0, 2);
        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(asmId, 1));
        Assert.Equal(0, inputInv.CountOf(oreId));
        Assert.Equal(2, inputInv.CountOf(plateId));
    }

    [Fact]
    public void TransferToEntity_Furnace_AcceptsSmeltingIngredient_EvenBeforeRecipeInferred()
    {
        var sim = NewSim();
        sim.Submit(PlaceFurnace(sim, 0, 2));
        sim.Step();

        var furnaceId = sim.World.GetEntityAt(0, 2);
        Assert.Equal(-1, sim.Machines.GetCurrentRecipe(furnaceId));   // 还没倒推出配方(第一炉都没投)

        int oreId = sim.Prototypes.Get<ItemPrototype>("iron-ore").Id;
        int oreStack = sim.Prototypes.Get<ItemPrototype>("iron-ore").StackSize;
        sim.Player.Inventory.Insert(oreId, 2, oreStack);
        sim.Submit(TransferTo(0, 2, oreId, 2));
        sim.Step();

        // 收了——过滤看的是"是不是某个 smelting 配方的原料",不是"当前配方"(还不存在)。
        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 1));
        Assert.Equal(2, inputInv.CountOf(oreId));
    }

    [Fact]
    public void TransferToEntity_Furnace_RejectsNonSmeltingItem()
    {
        var sim = NewSim();
        sim.Submit(PlaceFurnace(sim, 0, 2));
        sim.Step();

        int gearId = sim.Prototypes.Get<ItemPrototype>("iron-gear-wheel").Id;
        int gearStack = sim.Prototypes.Get<ItemPrototype>("iron-gear-wheel").StackSize;
        sim.Player.Inventory.Insert(gearId, 2, gearStack);
        sim.Submit(TransferTo(0, 2, gearId, 2));
        sim.Step();

        var furnaceId = sim.World.GetEntityAt(0, 2);
        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 1));
        Assert.Equal(0, inputInv.CountOf(gearId));
        Assert.Equal(2, sim.Player.Inventory.CountOf(gearId));
    }

    [Fact]
    public void Inserter_DropIntoMachineInput_BlockedByFilter_HoldsItemIndefinitely()
    {
        var sim = NewSim();
        PlacePoweredInserterInfra(sim);
        sim.Submit(PlaceChest(sim, 0, 2));
        sim.Submit(PlaceInserter(sim, 1, 2, rotation: 1));   // 东
        sim.Submit(PlaceAssembler(sim, 2, 2));                // 没设配方,role-1 一律拒收
        sim.Step();

        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int plateStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;
        var chestInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(0, 2)));
        chestInv.Insert(plateId, 1, plateStack);

        var insId = sim.World.GetEntityAt(1, 2);
        var asmId = sim.World.GetEntityAt(2, 2);
        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(asmId, 1));

        for (int t = 0; t < 300; t++) sim.Step();   // 远超一次摆臂周期(~63 tick),够它反复试放

        Assert.NotEqual(0, sim.Inserters.GetHeldItemProtoId(insId));   // 一直卡在手里,没被"塞"进去
        Assert.Equal(0, inputInv.CountOf(plateId));
    }

    [Fact]
    public void Inserter_NoPower_DoesNotGrab_ThenGrabsOncePowered()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 0, 2));
        sim.Submit(PlaceInserter(sim, 1, 2, rotation: 1));
        sim.Submit(PlaceChest(sim, 2, 2));
        sim.Step();

        int iron = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int ironStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;
        var chestA = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(0, 2)));
        chestA.Insert(iron, 1, ironStack);

        var insId = sim.World.GetEntityAt(1, 2);

        // 没接电网(satisfaction 恒 0)——阶段 A 不该抓,哪怕物品明明够得着。
        for (int t = 0; t < 100; t++) sim.Step();
        Assert.Equal(0, sim.Inserters.GetHeldItemProtoId(insId));
        Assert.Equal(1, chestA.CountOf(iron));   // 原封不动待在源箱子里

        // 接上电网(pole(0,0)+gen(2,0),跟已有布局不冲突)——来电后正常抓。
        PlacePoweredInserterInfra(sim);
        int tick = 0;
        while (sim.Inserters.GetHeldItemProtoId(insId) == 0 && tick < 100) { sim.Step(); tick++; }
        Assert.NotEqual(0, sim.Inserters.GetHeldItemProtoId(insId));
    }

    [Fact]
    public void TransferToEntity_WakesUpAsleepMachine_SameTick()
    {
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceFurnace(sim, 0, 2));
        sim.Step();   // 放置

        var furnaceId = sim.World.GetEntityAt(0, 2);
        sim.Step();   // 没原料 -> 熔炉 PreSettle 扫完配方仍是 -1 -> 睡
        Assert.False(sim.Machines.IsAwake(furnaceId));

        int oreId = sim.Prototypes.Get<ItemPrototype>("iron-ore").Id;
        sim.Player.Inventory.Insert(oreId, 1, sim.Prototypes.Get<ItemPrototype>("iron-ore").StackSize);
        sim.Submit(TransferTo(0, 2, oreId, 1));
        sim.Step();   // TransferToEntity 在 Commands 阶段(整个 tick 最早)执行,
                       // 早于本 tick 的 MachinesTickPreSettle,应该同 tick 就醒。

        Assert.True(sim.Machines.IsAwake(furnaceId));
    }

    [Fact]
    public void TransferFromEntity_WakesUpMachineBlockedOnFullOutput()
    {
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceFurnace(sim, 0, 2));
        sim.Step();

        var furnaceId = sim.World.GetEntityAt(0, 2);
        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 1));
        var outputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 2));
        int oreId = sim.Prototypes.Get<ItemPrototype>("iron-ore").Id;
        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int plateStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;

        inputInv.Insert(oreId, 1, sim.Prototypes.Get<ItemPrototype>("iron-ore").StackSize);
        sim.Machines.MarkAwake(furnaceId);   // 直接操纵库存,按测试约定显式唤醒(spec §5)
        outputInv.Insert(plateId, plateStack, plateStack);   // 预填满输出槽

        for (int t = 0; t < 200; t++) sim.Step();   // 完成一轮 -> 输出塞不下 -> 睡
        Assert.False(sim.Machines.IsAwake(furnaceId));
        Assert.Equal(0, inputInv.CountOf(oreId));            // 已经在完成时消耗

        // 补一点新矿:熔炉的 clearRecipe=true 语义是"每轮从输入内容现推配方"——
        // flush 成功后 RestartCycle 会立刻把配方清空,若这时输入库存仍是空的,
        // MachineTickPreSettle 会在同一 tick 内(重新扫配方失败 -> 判睡)紧接着又把
        // 它判回睡眠,盖过我们刚触发的唤醒,测试就没法验证"TransferFromEntity 触发
        // 唤醒"这件事本身。补一份原料让它唤醒后确实有活干、能验证唤醒真的生效
        // (brief 原始草稿没有这一步,这里是必要的修正)。
        inputInv.Insert(oreId, 1, sim.Prototypes.Get<ItemPrototype>("iron-ore").StackSize);
        sim.Submit(TransferFrom(0, 2, plateId, plateStack)); // 玩家手动搬走成品
        sim.Step();

        Assert.True(sim.Machines.IsAwake(furnaceId));
    }

    [Fact]
    public void SetRecipe_WakesUpSleepingAssembler_SameTick()
    {
        var sim = NewSim();
        sim.Submit(PlaceAssembler(sim, 0, 0));
        sim.Step();
        var asmId = sim.World.GetEntityAt(0, 0);
        sim.Step();   // 没配方 -> 睡
        Assert.False(sim.Machines.IsAwake(asmId));

        // 装配机的配方满足性检查(MachineTickPostSettle)和唤醒(SetRecipe 命令处理)
        // 在同一 tick 内先后发生:先给够原料,SetRecipe 唤醒后 PostSettle 才不会因为
        // 缺料又把它判回睡(这一步跟 brief 原始测试草稿不同——原始版本没垫原料,
        // 装配机会在同一 tick 里被 SetRecipe 唤醒又被 MachineTickPostSettle 缺料判睡,
        // 断言必然失败;这里补上原料让测试真正验证"SetRecipe 触发同 tick 唤醒"这件事)。
        int gearRecipeId = sim.Prototypes.Get<RecipePrototype>("iron-gear-wheel").Id;
        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(asmId, 1));
        inputInv.Insert(plateId, 2, sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize);
        sim.Submit(SetRecipe(0, 0, gearRecipeId));
        sim.Step();

        Assert.True(sim.Machines.IsAwake(asmId));
    }

    [Fact]
    public void Inserter_DropIntoMachineInput_WakesUpSleepingFurnace()
    {
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        sim.Submit(PlaceFurnace(sim, 2, 2));
        // insA at (1,2) rot=East feeds a wooden-chest's contents into the furnace input —
        // 复用既有 Inserter_IntoFurnaceInput_UsesRole1 一带的布局风格:机械臂背后放一个
        // 木箱塞满铁矿,机械臂负责把矿搬进熔炉输入。
        sim.Submit(PlaceChest(sim, 0, 2));
        sim.Submit(PlaceInserter(sim, 1, 2, rotation: 1));   // East:pickup(0,2) chest -> drop(2,2) furnace
        sim.Step();

        var furnaceId = sim.World.GetEntityAt(2, 2);
        sim.Step();   // 没原料 -> 睡
        Assert.False(sim.Machines.IsAwake(furnaceId));

        var chestId = sim.World.GetEntityAt(0, 2);
        int oreId = sim.Prototypes.Get<ItemPrototype>("iron-ore").Id;
        sim.Inventories.Get(sim.Inventories.GetInventoryId(chestId)).Insert(oreId, 5, sim.Prototypes.Get<ItemPrototype>("iron-ore").StackSize);

        // 精确定位机械臂真正把矿放进熔炉输入的那个 tick(同 Inserter_IntoFurnaceInput_
        // UsesRole1_OutOfFurnaceOutput_UsesRole2 的计时校准手法:轮询 role-1 库存,而不是
        // 用一个宽松的、可能被 60-tick 安全网单独满足的 eventual-consistency 循环)。
        // 关键断言绑定在"这一 tick 之前刚好还睡着,这一 tick 之后刚好醒了"——即使
        // 安全网偶尔在等待期间把它闪一下唤醒又睡回去,也不会被误判为放件触发的唤醒,
        // 因为我们看的是紧贴放件那一刻前后的状态跳变,不是任意时刻的 IsAwake==true。
        var furnaceInput = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 1));
        int dropTick = -1;
        bool awakeBeforeDrop = true;
        for (int t = 0; t < 150; t++)
        {
            bool awakeBefore = sim.Machines.IsAwake(furnaceId);
            sim.Step();
            if (furnaceInput.CountOf(oreId) > 0) { dropTick = t; awakeBeforeDrop = awakeBefore; break; }
        }

        Assert.True(dropTick >= 0);       // 机械臂确实把矿放进了熔炉输入
        Assert.False(awakeBeforeDrop);    // 放件前一刻仍是睡着的
        Assert.True(sim.Machines.IsAwake(furnaceId));   // MarkAwake 与放件同一次 Step() 内生效
    }

    // 第 5 个也是最后一个唤醒触发点(design spec §8 要求全覆盖):机械臂从机器的
    // 输出库存里"抓"东西(InserterTickPostSettle 阶段 A,role==2 分支的
    // Machines.MarkAwake(pickEntity))。前 4 个已覆盖:TransferToEntity、
    // TransferFromEntity、SetRecipe、机械臂放件进输入(上面那个测试)。这个是唯一
    // 剩下的——之前完全没有测试覆盖。布局取自既有 Inserter_OutOfFurnaceOutput_UsesRole2
    // (机械臂身后=熔炉输出、身前=箱子),场景取自 TransferFromEntity_WakesUpMachineBlockedOnFullOutput
    // (熔炉完成一轮但输出堵满 -> 睡;需要有人腾出输出空间才能再次唤醒)。
    [Fact]
    public void Inserter_GrabFromMachineOutput_WakesUpMachineBlockedOnFullOutput()
    {
        var sim = NewSim();
        PlacePoweredMachineInfra(sim);
        // 先只放熔炉——机械臂这时候还不在场,不会在"喂料->完成->堵塞->睡"这段
        // 过程里提前把输出槽腾出来(否则熔炉永远不会真的堵塞/睡着,后面就测不出
        // "抓取触发唤醒"这件事本身)。机械臂和箱子等确认堵塞入睡后再放。
        sim.Submit(PlaceFurnace(sim, 2, 2));
        sim.Step();

        var furnaceId = sim.World.GetEntityAt(2, 2);
        var inputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 1));
        var outputInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(furnaceId, 2));
        int oreId = sim.Prototypes.Get<ItemPrototype>("iron-ore").Id;
        int plateId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int plateStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;

        inputInv.Insert(oreId, 1, sim.Prototypes.Get<ItemPrototype>("iron-ore").StackSize);
        sim.Machines.MarkAwake(furnaceId);   // 直接操纵库存,按测试约定显式唤醒(spec §5)
        outputInv.Insert(plateId, plateStack, plateStack);   // 预填满输出槽,逼它完成一轮后堵住

        for (int t = 0; t < 200; t++) sim.Step();   // 完成一轮 -> 输出塞不下 -> 睡
        Assert.False(sim.Machines.IsAwake(furnaceId));
        Assert.Equal(0, inputInv.CountOf(oreId));            // 已经在完成时消耗
        Assert.Equal(plateStack, outputInv.CountOf(plateId)); // 输出槽确实还是满的(没人腾过)

        // 补一点新矿,让唤醒后确实有活干(同 TransferFromEntity_WakesUpMachineBlockedOnFullOutput
        // 的修正:否则唤醒后 PreSettle 马上又因为没配方/没料判回睡,验证不到"抓取触发唤醒"这件事)。
        inputInv.Insert(oreId, 1, sim.Prototypes.Get<ItemPrototype>("iron-ore").StackSize);

        // 现在才放机械臂 + 箱子(布局取自既有 Inserter_OutOfFurnaceOutput_UsesRole2:
        // 机械臂身后=熔炉输出、身前=箱子)——它一来电就会尝试从熔炉输出里抓东西,
        // 抓走后腾出的空间正是熔炉本轮唤醒要验证的触发点。
        sim.Submit(PlaceChest(sim, 0, 2));
        sim.Submit(PlaceInserter(sim, 1, 2, rotation: 3));   // West:pickup(2,2) 熔炉输出 -> drop(0,2) 箱子
        sim.Step();

        // 精确定位机械臂真正从输出槽里抓走一件成品的那个 tick——MarkAwake 发生在
        // InserterTickPostSettle 阶段 A 抓取成功的那一刻(role==2 分支),不是等它摆
        // 到箱子那边放下才醒,所以要看 outputInv 的数量减少,而不是箱子里数量增加。
        // 注意:不用"抓取前一刻必须还睡着"这种更强的断言——前面已经跑了近 200 tick
        // 让熔炉进入堵塞状态,早就跨过了好几个 60-tick 安全网周期,安全网自己也会周期性
        // 把它闪唤醒一下(见 MachinesTickPreSettle 里 justWoken 那一 tick 的行为)又在下
        // 一 tick 因为仍然堵塞被判回睡——这种残留状态可能恰好落在抓取前一 tick,和这里
        // 想验证的"抓取触发的唤醒"混在一起,断言就不稳定了。把断言绑定在"抓取发生的
        // 这一 tick,唤醒确实生效"上,就足以证明 role==2 分支的 MarkAwake 在起作用,同时
        // 排除了"纯靠安全网这个大窗口迟早蒙对"的可能(抓取远早于 200 tick 上限出现)。
        int grabTick = -1;
        for (int t = 0; t < 200; t++)
        {
            sim.Step();
            if (outputInv.CountOf(plateId) < plateStack) { grabTick = t; break; }
        }

        Assert.True(grabTick >= 0);       // 机械臂确实从熔炉输出里抓走了一件成品
        Assert.True(sim.Machines.IsAwake(furnaceId));   // MarkAwake 与抓取同一次 Step() 内生效
    }

    [Fact]
    public void RotateEntity_RevealsNewTarget_WakesUpSleepingDrill()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        // (0,20)-(1,21) 空地(同上一个测试的探测);找一个转个向就能碰到矿的朝向,
        // 或者退而求其次:只断言转向后 IsAwake 变 true(RotateEntity 无条件唤醒,
        // 不要求这次转向本身一定找得到矿——设计只承诺"给它一次重新搜索的机会")。
        int px = 0, py = 20;
        sim.Submit(PlaceDrill(sim, px, py, rotation: 1));
        sim.Step();
        var drillId = sim.World.GetEntityAt(px, py);
        sim.Step();   // 没矿 -> 睡
        Assert.False(sim.MiningDrills.IsAwake(drillId));

        // 玩家默认出生在 (0,0),ReachSubTiles=1536(6 tile),够不到 (0,20) 的采矿机——
        // RotateEntity 会因为超出距离静默 RejectedCommandCount++ 而不是真的转向。
        // 直接把玩家瞬移到采矿机旁边(MoveTo 是 internal,测试项目通过
        // InternalsVisibleTo 可见),不用真的走过去。
        sim.Player.MoveTo(px * 256 + 128, py * 256 + 128);
        sim.Submit(new Command { Type = CommandType.RotateEntity, X = px, Y = py, Rotation = 2 });
        sim.Step();

        Assert.True(sim.MiningDrills.IsAwake(drillId));   // 转向立即唤醒,不管这次搜索最终有没有找到矿
    }

    [Fact]
    public void TransferFromEntity_WakesUpDrillBlockedOnFullOutputChest()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        var (drillX, drillY) = FindPoweredOreOrigin(sim);
        int chestX = drillX + 2, chestY = drillY;
        sim.Submit(PlaceDrill(sim, drillX, drillY, rotation: 1));   // 朝东输出到 (chestX, chestY)
        sim.Submit(PlaceChest(sim, chestX, chestY));
        sim.Step();

        var drillId = sim.World.GetEntityAt(drillX, drillY);
        var chestId = sim.World.GetEntityAt(chestX, chestY);
        var chestInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(chestId));

        // 把箱子塞满(16 槽 x 某个 stack size 的任意填充物),逼采矿机挖完之后卡住。
        int fillerId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int fillerStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;
        for (int s = 0; s < 16; s++) chestInv.Insert(fillerId, fillerStack, fillerStack);

        for (int t = 0; t < 200; t++) sim.Step();   // 挖完一份 -> 塞不进箱子 -> 睡
        Assert.False(sim.MiningDrills.IsAwake(drillId));

        // 精确定位箱子腾出空间的那个 tick,断言唤醒发生在紧邻的那个 tick 内——不用
        // 宽松的 eventual-consistency 循环(Machines 最终审查 Finding 2 教训:那样
        // 可能被 60-tick 安全网碰巧满足,测不出 WakeWaitersAt 这条路径本身)。
        int before = chestInv.CountOf(fillerId);
        sim.Submit(TransferFrom(chestX, chestY, fillerId, fillerStack));
        sim.Step();

        Assert.True(chestInv.CountOf(fillerId) < before);   // 命令确实腾出了空间
        Assert.True(sim.MiningDrills.IsAwake(drillId));      // 同一 tick 内唤醒生效
    }

    [Fact]
    public void Inserter_GrabFromChest_WakesUpDrillBlockedOnThatChest()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        var (drillX, drillY) = FindPoweredOreOrigin(sim);
        int chestX = drillX + 2, chestY = drillY;
        sim.Submit(PlaceDrill(sim, drillX, drillY, rotation: 1));   // 朝东输出到 (chestX, chestY)
        sim.Submit(PlaceChest(sim, chestX, chestY));
        sim.Step();

        var drillId = sim.World.GetEntityAt(drillX, drillY);
        var chestId = sim.World.GetEntityAt(chestX, chestY);
        var chestInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(chestId));

        int fillerId = sim.Prototypes.Get<ItemPrototype>("iron-plate").Id;
        int fillerStack = sim.Prototypes.Get<ItemPrototype>("iron-plate").StackSize;
        for (int s = 0; s < 16; s++) chestInv.Insert(fillerId, fillerStack, fillerStack);

        for (int t = 0; t < 200; t++) sim.Step();
        Assert.False(sim.MiningDrills.IsAwake(drillId));

        // 现在才放机械臂(身后=输出箱、身前=另一个箱子),让它把箱子里的东西搬走。
        // 机械臂离唯一的电线杆 (0,0) 的 Chebyshev 距离若超出 small-electric-pole 的
        // supplyAreaDistanceTiles(2),单靠那根杆够不到它,机械臂会因为 satisfaction==0
        // 永远不抓——按需补一根线距内(<=7)的杆并入已有电网(同旧版写死坐标时的推理,
        // 只是现在跟着探测到的 drill 位置走,而不是写死 (3,4))。
        int inserterX = chestX + 1, inserterY = chestY;
        int dropX = chestX + 2, dropY = chestY;
        if (Math.Max(Math.Abs(inserterX), Math.Abs(inserterY)) > 2)
        {
            int extraPoleX = inserterX, extraPoleY = inserterY + 4;
            sim.Submit(PlacePole(sim, extraPoleX, extraPoleY));
        }
        sim.Submit(PlaceChest(sim, dropX, dropY));
        sim.Submit(PlaceInserter(sim, inserterX, inserterY, rotation: 1));   // East: pickup chest -> drop chest
        sim.Step();

        int fillCount = chestInv.CountOf(fillerId);
        int grabTick = -1;
        for (int t = 0; t < 200; t++)
        {
            sim.Step();
            if (chestInv.CountOf(fillerId) < fillCount) { grabTick = t; break; }
        }

        Assert.True(grabTick >= 0);   // 机械臂确实从箱子里抓走了东西
        Assert.True(sim.MiningDrills.IsAwake(drillId));   // WakeWaitersAt 与抓取同一次 Step() 内生效
    }

    // P16: 玩家碰撞盒 + 传送带带人移动 --------------------------------------

    private const int St = Faketorio.Sim.Belts.BeltLine.TileSubTiles; // 256

    [Fact]
    public void Player_BlockedByChest_WholeStepReject()
    {
        var sim = NewSim();
        sim.Submit(PlaceChest(sim, 3, 0));
        sim.Step();

        // 玩家在 (2,0) 格中心,朝东走(chest inset 0 → 硬挡)
        sim.Player.MoveTo(2 * St + St / 2, 0 * St + St / 2);
        sim.Player.SetWalk(2); // 东
        int x0 = sim.Player.X;
        for (int t = 0; t < 20; t++) sim.Step();

        // 没能走进 (3,0) 的占地:x 前沿始终 < 3*256
        Assert.True(sim.Player.X < 3 * St, $"expected blocked before x=768, got {sim.Player.X}");
        Assert.True(sim.Player.X > x0, $"expected the player to have advanced from x0={x0}, got {sim.Player.X}");
    }

    [Fact]
    public void Player_WalksFromOutsideOntoBelt_NotBlocked()
    {
        // P16 F5 验收时一度怀疑"走近传送带会被挡住"(后来查出是 Godot 编辑器
        // 加载了过期构建,不是真 bug),但排查过程中发现已有的 belt-carry 测试
        // 全部用 Player.MoveTo 直接把玩家传送到带上,从没测过"从相邻格子走进
        // 带所在格"这个真实移动路径——这是个真实的覆盖缺口,补上。
        var sim = NewSim();
        sim.Submit(PlaceBelt(sim, 3, 0, 1)); // 带在 (3,0)
        sim.Step();

        sim.Player.MoveTo(2 * St + St / 2, 0 * St + St / 2); // 玩家在 (2,0) 格中心
        sim.Player.SetWalk(2); // 朝东走向带
        for (int t = 0; t < 20; t++) sim.Step();

        Assert.True(sim.Player.X >= 3 * St, $"expected player to walk onto/through the belt tile at x>=768, got {sim.Player.X}");
    }

    [Fact]
    public void Player_WalksOntoBelt_AndDriftsWhenIdle()
    {
        var sim = NewSim();
        sim.Submit(PlaceBelt(sim, 5, 0, 1)); // 单格带,rot 1 = 东(Command.Rotation: 0/1/2/3 = 北/东/南/西;BeltNetwork.Delta(1)=(1,0))
        sim.Step();

        sim.Player.MoveTo(5 * St + St / 2, 0 * St + St / 2); // 站在带上
        sim.Player.StopWalk();                                // 不走
        int y0 = sim.Player.Y;

        for (int t = 0; t < 10; t++) sim.Step();

        // 每 tick 沿带方向漂移 carry(基础带 8);10 tick → +80 子格,Y 不变
        Assert.Equal(5 * St + St / 2 + 10 * 8, sim.Player.X);
        Assert.Equal(y0, sim.Player.Y);
    }

    [Fact]
    public void Player_WalkingWithBelt_AddsCarry()
    {
        var sim = NewSim();
        sim.Submit(PlaceBelt(sim, 5, 0, 1)); // 东
        sim.Step();
        sim.Player.MoveTo(5 * St + St / 2, 0 * St + St / 2);
        sim.Player.SetWalk(2); // 东,顺带
        int x0 = sim.Player.X;
        sim.Step();
        Assert.Equal(x0 + 38 + 8, sim.Player.X); // walk 38 + carry 8
    }

    [Fact]
    public void Player_WalkingAgainstBelt_NetSlower()
    {
        var sim = NewSim();
        sim.Submit(PlaceBelt(sim, 5, 0, 1)); // 东
        sim.Step();
        sim.Player.MoveTo(5 * St + St / 2, 0 * St + St / 2);
        sim.Player.SetWalk(6); // 西,逆带
        int x0 = sim.Player.X;
        sim.Step();
        Assert.Equal(x0 - 38 + 8, sim.Player.X); // -30:仍向西,但更慢
    }

    [Fact]
    public void Player_Anchored_NotCarriedByBelt()
    {
        var sim = NewSim();
        sim.Submit(PlaceBelt(sim, 5, 0, 1));
        sim.Step();
        sim.Player.MoveTo(5 * St + St / 2, 0 * St + St / 2);
        sim.Player.StopWalk();
        sim.Player.SetAnchored(true);
        int x0 = sim.Player.X, y0 = sim.Player.Y;
        for (int t = 0; t < 10; t++) sim.Step();
        Assert.Equal(x0, sim.Player.X);
        Assert.Equal(y0, sim.Player.Y);
    }

    [Fact]
    public void Player_PassesThroughSeamBetweenTiledAssemblers()
    {
        var sim = NewSim();
        // 两台 3×3 装配机,原点 (10,0) 与 (13,0) —— 密铺,东西相邻无 gap
        sim.Submit(PlaceAssembler(sim, 10, 0));
        sim.Submit(PlaceAssembler(sim, 13, 0));
        sim.Step();

        // 两机之间的 X 缝:A.box maxX = 13*256-96 = 3232;B.box minX = 13*256+96 = 3424。
        // 缝中点 X = 13*256 = 3328。玩家在缝里,从南(y 大)往北(y 小)穿过 3×3 的整段高度。
        int seamX = 13 * St;
        sim.Player.MoveTo(seamX, 3 * St);     // 机器占 y∈[0,3),从 y=3*256 起(机器南边外)
        sim.Player.SetWalk(0);                // 北
        for (int t = 0; t < 60; t++) sim.Step();

        // 穿过去了:y 前沿越过了机器北边 y=0
        Assert.True(sim.Player.Y < 0, $"expected to pass the seam to y<0, got {sim.Player.Y}");
        Assert.Equal(seamX, sim.Player.X);   // 没有横向漂移
    }

    [Fact]
    public void Player_BlockedByAssemblerCenter()
    {
        var sim = NewSim();
        sim.Submit(PlaceAssembler(sim, 10, 0));
        sim.Step();

        // 正对机器几何中心那一列 (x = 10*256 + 3*128 = 2944) 从南往北走 → 被实心块挡
        int centerX = 10 * St + 3 * St / 2;
        sim.Player.MoveTo(centerX, 4 * St);
        sim.Player.SetWalk(0); // 北
        for (int t = 0; t < 60; t++) sim.Step();

        // box: minY = 96, maxY = 3*256-96 = 672。玩家从 y=1024 往北,应停在 maxY(672) 前沿附近,不穿过。
        Assert.True(sim.Player.Y >= 3 * St - 96, $"expected blocked at/after box maxY=672, got {sim.Player.Y}");
        Assert.True(sim.Player.Y < 4 * St, $"expected the player to have advanced from y=1024, got {sim.Player.Y}");
    }

    [Fact]
    public void PlayerCollisionBox_RightEdgeIsHalfOpen()
    {
        var sim = NewSim();
        sim.Submit(PlaceAssembler(sim, 10, 0));
        sim.Step();

        // 装配机 (10,0) 3×3。碰撞盒 maxX = 13*256 - 96 = 3232,y 盒 [96, 672)。
        // 半开:玩家能恰好停在 x == 3232,停不进 3231。
        // 从东侧 x=3346 朝西走(每 tick -38):3308 -> 3270 -> 3232(恰在 maxX,不算撞,可停)
        //   -> 下一步 3194 落入盒 -> 整步拒绝。最终停在 3232。
        // 若判定误用 px <= maxX,则 3232 也算撞,玩家会停在 3270。
        sim.Player.MoveTo(3232 + 38 * 3, 1 * St);   // (3346, 256)
        sim.Player.SetWalk(6);                        // 西(八向 6 = 西)
        for (int t = 0; t < 10; t++) sim.Step();

        Assert.Equal(3232, sim.Player.X);
        Assert.Equal(1 * St, sim.Player.Y);
    }
}
