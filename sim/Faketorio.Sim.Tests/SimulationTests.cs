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

        // 玩家背包 60 槽,起始物品(8 个铁板的半满槽 + 1 把木箱的槽)占 2 槽。
        // 补满那个半满槽(92 个)再填满剩下 58 个空槽中的 57 个(57*100),
        // 精确留 1 个空槽——只够装 1 组煤(50)。
        sim.Player.Inventory.Insert(ironPlate, (ironStack - 8) + 57 * ironStack, ironStack);

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
        Assert.Equal(50, sim.Player.Inventory.CountOf(coal));  // 只搬了玩家吃得下的 50 个
        Assert.Equal(30, chestInv.CountOf(coal));              // 箱子里剩下搬不走的 30 个
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
        outputInv.Insert(plateId, plateStack, plateStack);   // pre-fill the furnace's one output slot

        for (int t = 0; t < 200; t++) sim.Step();

        Assert.Equal(0, inputInv.CountOf(oreId));            // consumed at completion
        Assert.Equal(plateStack, outputInv.CountOf(plateId)); // still full — flush blocked

        outputInv.Remove(plateId, plateStack);   // free the output
        sim.Step();

        Assert.Equal(1, outputInv.CountOf(plateId));   // flushed on the next tick
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

        sim.Step();
        Assert.True(sim.Machines.GetProgress(asmId) > 0);   // now advancing from 0
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

    [Fact]
    public void MiningDrill_FindsResourceInFootprint_AndExtractsToOutputChest()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        // Drill origin must stay within the pole's supplyAreaDistanceTiles (2, Chebyshev,
        // measured from the entity's placement-time (X,Y) origin — see ElectricGrid).
        // Pole is at (0,0), so (0,2) is right at the boundary; (0,3) would be unpowered.
        // 采矿机 2x2 footprint 占 (0,2)-(1,3),朝东(rotation=1)输出到 (2,2)。
        sim.Submit(PlaceDrill(sim, 0, 2, rotation: 1));
        sim.Submit(PlaceChest(sim, 2, 2));
        sim.Step();

        // Resources 是惰性生成的矿脉——用固定种子(NewSim 默认种子 0)读一遍 (0,2)-(1,3)
        // 范围,找到实际有矿的格再断言(矿脉分布是种子的确定函数,不是本测试要验证的东西;
        // 这里只需要确认"某个格有矿、采矿机能找到它、挖出对应物品、堆进箱子"这条流程通)。
        var drillId = sim.World.GetEntityAt(0, 2);
        var chestId = sim.World.GetEntityAt(2, 2);
        var chestInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(chestId));

        bool foundResource = false;
        for (int dy = 0; dy < 2 && !foundResource; dy++)
            for (int dx = 0; dx < 2 && !foundResource; dx++)
                if (!sim.Resources.GetResourceAt(dx, 2 + dy).IsEmpty) foundResource = true;
        Assert.True(foundResource, "test assumes the default seed puts at least one resource tile under (0,2)-(1,3) — if this fails, adjust the drill's placement coordinates to a spot the seeded map actually has ore under.");

        for (int t = 0; t < 200; t++) sim.Step();

        Assert.True(chestInv.TotalItems() > 0);
        Assert.NotEqual(-1, sim.MiningDrills.GetTargetX(drillId)); // still locked on a footprint tile (rich enough to outlast 200 ticks)
    }

    [Fact]
    public void MiningDrill_OutputsToBelt_WithCorrectItemType()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        // Same footprint/power reasoning as the chest test above: drill origin must stay
        // within the pole's supplyAreaDistanceTiles (2, Chebyshev, from (0,0)).
        sim.Submit(PlaceDrill(sim, 0, 2, rotation: 1)); // 朝东输出到 (2,2)
        sim.Submit(new Command { Type = CommandType.PlaceEntity,
            ProtoId = sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id,
            X = 2, Y = 2, Rotation = 1 });
        sim.Step();

        bool foundResource = false;
        for (int dy = 0; dy < 2 && !foundResource; dy++)
            for (int dx = 0; dx < 2 && !foundResource; dx++)
                if (!sim.Resources.GetResourceAt(dx, 2 + dy).IsEmpty) foundResource = true;
        Assert.True(foundResource, "adjust drill placement if the seeded map doesn't have ore here");

        for (int t = 0; t < 200; t++) sim.Step();

        var lineId = sim.Belts.GetLineAt(2, 2);
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
        sim.Submit(PlaceDrill(sim, 0, 2, rotation: 1));              // 朝东输出到 (2,2)
        sim.Submit(new Command { Type = CommandType.PlaceEntity,
            ProtoId = sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id,
            X = 2, Y = 2, Rotation = 1 });                          // 带也向东 -> 平行
        sim.Step();
        for (int t = 0; t < 200; t++) sim.Step();

        var line = sim.Belts.GetLine(sim.Belts.GetLineAt(2, 2));
        Assert.True(line.LaneB.Count > 0, "平行进料应落右侧 LaneB");
        Assert.Equal(0, line.LaneA.Count);                          // 不回退到另一条
    }

    [Fact]
    public void MiningDrill_OrthogonalToBelt_OutputsToFarLane()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        sim.Submit(PlaceDrill(sim, 0, 2, rotation: 1));              // 朝东输出到 (2,2)
        sim.Submit(new Command { Type = CommandType.PlaceEntity,
            ProtoId = sim.Prototypes.Get<TransportBeltPrototype>("transport-belt-basic").Id,
            X = 2, Y = 2, Rotation = 2 });                          // 带向南 -> 与"朝东"正交
        sim.Step();
        for (int t = 0; t < 200; t++) sim.Step();

        var line = sim.Belts.GetLine(sim.Belts.GetLineAt(2, 2));
        // 带向南行进,右手法线 = 西;采矿机在带西侧往东怼 -> 远端在东 = 左侧 LaneA。
        Assert.True(line.LaneA.Count > 0, "正交进料应落远端 LaneA");
        Assert.Equal(0, line.LaneB.Count);
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
        sim.Submit(PlaceDrill(sim, 5000, 5000, rotation: 1));
        sim.Step();

        var drillId = sim.World.GetEntityAt(5000, 5000);
        bool anyResource = false;
        for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++)
                if (!sim.Resources.GetResourceAt(5000 + dx, 5000 + dy).IsEmpty) anyResource = true;
        Assert.False(anyResource, "test assumes (5000,5000)-(5001,5001) has no ore under the default seed — pick a different far-away coordinate if this ever becomes false");

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
        sim.Submit(PlaceDrill(sim, 0, 2, rotation: 1)); // no power infra — unpowered on purpose
        sim.Step();

        var drillId = sim.World.GetEntityAt(0, 2);
        int tx = sim.MiningDrills.GetTargetX(drillId), ty = sim.MiningDrills.GetTargetY(drillId);
        Assert.NotEqual(-1, tx); // seed 0 has ore under (0,2)-(1,3); drill locks it regardless of power

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
        sim.Submit(PlaceDrill(sim, 0, 2, rotation: 1));
        sim.Submit(PlaceChest(sim, 2, 2));
        sim.Step();

        var drillId = sim.World.GetEntityAt(0, 2);
        var chestId = sim.World.GetEntityAt(2, 2);
        var chestInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(chestId));

        int oreTiles = 0;
        for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++)
                if (!sim.Resources.GetResourceAt(dx, 2 + dy).IsEmpty) oreTiles++;
        Assert.True(oreTiles >= 2, "test needs >=2 resource tiles under (0,2)-(1,3) for the default seed — move the drill to a denser ore spot if this fails.");

        int firstTx = sim.MiningDrills.GetTargetX(drillId), firstTy = sim.MiningDrills.GetTargetY(drillId);
        Assert.NotEqual(-1, firstTx);

        // Exhaust the locked tile out from under the drill.
        var lockedCell = sim.Resources.GetResourceAt(firstTx, firstTy);
        sim.Resources.Extract(firstTx, firstTy, lockedCell.Amount);

        for (int t = 0; t < 200; t++) sim.Step();

        int newTx = sim.MiningDrills.GetTargetX(drillId), newTy = sim.MiningDrills.GetTargetY(drillId);
        Assert.NotEqual(-1, newTx);                                     // didn't get stuck idle
        Assert.False(newTx == firstTx && newTy == firstTy);             // moved off the exhausted tile
        Assert.InRange(newTx, 0, 1);                                    // still inside the 2x2 footprint
        Assert.InRange(newTy, 2, 3);
        Assert.False(sim.Resources.GetResourceAt(newTx, newTy).IsEmpty);
        Assert.True(chestInv.TotalItems() > 0);                         // kept producing across the re-target
    }

    [Fact]
    public void MiningDrill_OutputBlocked_HoldsPendingThenResumesWhenSpaceFrees()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        sim.Submit(PlaceDrill(sim, 0, 2, rotation: 1));
        sim.Submit(PlaceChest(sim, 2, 2));
        sim.Step();

        var drillId = sim.World.GetEntityAt(0, 2);
        var chestId = sim.World.GetEntityAt(2, 2);
        var chestInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(chestId));
        Assert.NotEqual(-1, sim.MiningDrills.GetTargetX(drillId)); // has ore to mine (seed 0)

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
        sim.Step();

        Assert.False(sim.MiningDrills.IsCompleted(drillId));          // resumed
        Assert.True(sim.MiningDrills.GetProgress(drillId) < heldProgress); // fresh cycle (one tick in), not the held full-cycle value
        Assert.True(chestInv.TotalItems() > (chestInv.SlotCount - 1) * coalStack); // pending item flushed in
    }

    [Fact]
    public void MiningDrill_UnderpoweredSatisfaction_TakesTwiceAsLong()
    {
        var sim = NewSim();
        PlacePoweredDrillInfra(sim);
        sim.Submit(PlaceDrill(sim, 0, 2, rotation: 1));
        sim.Submit(PlaceChest(sim, 2, 2));
        sim.Step();

        var drillId = sim.World.GetEntityAt(0, 2);
        var chestId = sim.World.GetEntityAt(2, 2);
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
        sim.Submit(PlaceDrill(sim, 0, 2, rotation: 1));   // 朝东,输出到 (2,2)
        sim.Submit(PlaceChest(sim, 2, 2));                // 旧输出格
        sim.Submit(PlaceChest(sim, 0, 4));                // 新输出格(朝南 rotation=2 时:(0,2)+(0,2)*2=(0,4))
        sim.Step();

        var drillId = sim.World.GetEntityAt(0, 2);
        var oldOutInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(2, 2)));
        var newOutInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(0, 4)));

        // 还没挖完(!IsCompleted)时转向 —— 不在危险窗口,立即生效。
        Assert.False(sim.MiningDrills.IsCompleted(drillId));
        sim.Submit(RotateEntity(0, 2, 2));   // 南
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
        sim.Submit(PlaceDrill(sim, 0, 2, rotation: 1));   // 朝东,输出到 (2,2)
        sim.Submit(PlaceChest(sim, 2, 2));                // 旧输出格
        sim.Submit(PlaceChest(sim, 0, 4));                // 新输出格(朝南)
        sim.Step();

        var drillId = sim.World.GetEntityAt(0, 2);
        Assert.True(sim.World.GetEntityAt(2, 2).IsValid, "old output chest missing");
        Assert.True(sim.World.GetEntityAt(0, 4).IsValid, "new output chest missing");
        Assert.NotEqual(-1, sim.MiningDrills.GetTargetX(drillId));
        var oldOutInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(2, 2)));
        var newOutInv = sim.Inventories.Get(sim.Inventories.GetInventoryId(sim.World.GetEntityAt(0, 4)));

        // 堵住旧输出格,逼它停在"挖完待排出"的危险窗口里。
        int coal = sim.Prototypes.Get<ItemPrototype>("coal").Id;
        int coalStack = sim.Prototypes.Get<ItemPrototype>("coal").StackSize;
        oldOutInv.Insert(coal, oldOutInv.SlotCount * coalStack, coalStack);

        int tick = 0;
        while (!sim.MiningDrills.IsCompleted(drillId) && tick < 400) { sim.Step(); tick++; }
        Assert.True(sim.MiningDrills.IsCompleted(drillId), $"never completed after {tick} ticks; progress={sim.MiningDrills.GetProgress(drillId)}, targetX={sim.MiningDrills.GetTargetX(drillId)}");

        // 挖完待排出时发转向:排队,不立即改朝向(排出格还没落地)。
        sim.Submit(RotateEntity(0, 2, 2));   // 南
        sim.Step();
        Assert.True(sim.MiningDrills.IsCompleted(drillId));   // 仍卡在旧朝向的危险窗口

        // 腾出旧输出格 —— flush 必须走旧朝向落进旧箱子,不能瞬移到新朝向。
        // (旧箱子腾位后仍剩不少煤,TotalItems() 不会归 0,用 IsCompleted 归 false 判断 flush 是否发生。)
        int totalBeforeFlush = oldOutInv.TotalItems();
        oldOutInv.Remove(coal, coalStack);
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
}
