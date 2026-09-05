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
        Assert.True(sim.MiningDrills.GetProgress(drillId) >= 0); // sanity: didn't throw, state is readable
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
}
