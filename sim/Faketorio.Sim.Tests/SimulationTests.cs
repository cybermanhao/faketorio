using Faketorio.Sim.Belts;
using Faketorio.Sim.Commands;
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
}
