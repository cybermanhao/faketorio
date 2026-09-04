using Faketorio.Sim.Belts;
using Faketorio.Sim.Commands;
using Faketorio.Sim.Entities;
using Faketorio.Sim.Items;
using Faketorio.Sim.Prototypes;
using Faketorio.Sim.State;
using Faketorio.Sim.World;

namespace Faketorio.Sim;

public sealed class Simulation
{
    public PrototypeRegistry Prototypes { get; }
    public WorldGrid World { get; } = new();
    public EntityPool<EntityData> Entities { get; } = new();
    public BeltNetwork Belts { get; } = new();

    // 分层说明:BeltNetwork 刻意"不知道 Simulation / Entities";Inventories
    // 反过来直接收 EntityId。这是有意的偏差——EntityId 只是个裸 readonly
    // record struct(不依赖 EntityPool),拿它当键不引入对实体池的依赖。
    public Inventories Inventories { get; } = new();
    public ResourceGrid Resources { get; }

    private readonly long _worldSeed;

    public long Tick { get; private set; }
    public int RejectedCommandCount { get; private set; }

    private readonly CommandQueue _commands = new();

    public Simulation(PrototypeRegistry prototypes, long worldSeed = 0)
    {
        Prototypes = prototypes;
        _worldSeed = worldSeed;
        Resources = new ResourceGrid(worldSeed, prototypes);
    }

    public void Submit(in Command command) => _commands.Enqueue(command);

    public void Step()
    {
        var commands = _commands.BeginTick();
        for (int i = 0; i < commands.Length; i++)
            Apply(in commands[i]);
        // 传送带:推进(按 Belts 池索引序,确定)
        for (int bi = 0; bi < Belts.Capacity; bi++)
        {
            if (!Belts.IsAliveAtIndex(bi)) continue;
            var line = Belts.GetAtIndex(bi);
            int speed = ResolveBeltSpeed(line);
            line.LaneA.Advance(speed);
            line.LaneB.Advance(speed);
        }
        // 传送带:线间交接(拐角处把出口物品传给下游线;不检查方向)
        for (int bi = 0; bi < Belts.Capacity; bi++)
        {
            if (!Belts.IsAliveAtIndex(bi)) continue;
            var line = Belts.GetAtIndex(bi);
            var (dx, dy) = BeltNetwork.Delta(line.Direction);
            var (ex, ey) = line.Tiles[0];
            int fx = ex + dx, fy = ey + dy;
            var downId = Belts.GetLineAt(fx, fy);
            if (!downId.IsValid) continue;
            var down = Belts.GetLine(downId);
            if (down.Tiles[^1] != (fx, fy)) continue;
            while (line.LaneA.IsFrontReady && down.LaneA.TryInsertAtBack()) line.LaneA.RemoveFront();
            while (line.LaneB.IsFrontReady && down.LaneB.TryInsertAtBack()) line.LaneB.RemoveFront();
        }
        Tick++;
    }

    public ulong ComputeStateHash()
    {
        var writer = new Fnv1aHashWriter();
        WriteState(writer);
        return writer.Hash;
    }

    public void WriteState(IStateWriter writer)
    {
        writer.Write(Tick);
        writer.Write(RejectedCommandCount);
        writer.Write(_worldSeed);

        // 实体池分配器簿记(高水位/空闲栈/全部代数):恢复后 Create() 分配顺序需一致
        Entities.WriteState(writer);

        // 实体池:按索引序(确定)——存活实体的内容数据(与上面的分配器簿记是两回事)
        for (int i = 0; i < Entities.Capacity; i++)
        {
            if (!Entities.IsAliveAtIndex(i)) continue;
            writer.Write(i);
            writer.Write(Entities.GenerationAtIndex(i));
            ref var data = ref Entities.GetAtIndex(i);
            writer.Write(data.ProtoId);
            writer.Write(data.X);
            writer.Write(data.Y);
            writer.Write(data.Rotation);
        }

        // 世界网格:chunk 按键序(确定)
        var keys = World.SortedChunkKeys();
        for (int k = 0; k < keys.Count; k++)
        {
            writer.Write(keys[k]);
            var tiles = World.GetChunkByKey(keys[k]).Tiles;
            for (int i = 0; i < tiles.Length; i++)
            {
                writer.Write(tiles[i].Index);
                writer.Write(tiles[i].Generation);
            }
        }

        Belts.WriteState(writer);
        Inventories.WriteState(writer);
        Resources.WriteState(writer);
    }

    // 一条线按其出口格(Tiles[0])的传送带 prototype 速度跑(设计文档第 7 节)。
    // 严格:出口格上一定是传送带实体,不做 fallback。
    private int ResolveBeltSpeed(BeltLine line)
    {
        var (ex, ey) = line.Tiles[0];
        var eid = World.GetEntityAt(ex, ey);
        var protoId = Entities.Get(eid).ProtoId;
        return ((TransportBeltPrototype)Prototypes.GetById(protoId)).SpeedSubTilesPerTick;
    }

    private void Apply(in Command command)
    {
        switch (command.Type)
        {
            case CommandType.PlaceEntity:
            {
                if (!Prototypes.TryGetById(command.ProtoId, out var p) || p is not EntityPrototype proto
                    || command.Rotation > 3
                    || !World.IsAreaFree(command.X, command.Y, proto.TileWidth, proto.TileHeight))
                {
                    RejectedCommandCount++;
                    return;
                }
                var id = Entities.Create(new EntityData
                {
                    ProtoId = command.ProtoId,
                    X = command.X,
                    Y = command.Y,
                    Rotation = command.Rotation,
                });
                World.OccupyArea(command.X, command.Y, proto.TileWidth, proto.TileHeight, id);
                if (proto is TransportBeltPrototype)
                    Belts.AddBelt(command.X, command.Y, command.Rotation);
                if (proto is ContainerPrototype cp)
                    Inventories.AddContainer(id, cp.InventorySize);
                return;
            }
            case CommandType.RemoveEntity:
            {
                var id = World.GetEntityAt(command.X, command.Y);
                if (!id.IsValid || !Entities.IsAlive(id))
                {
                    RejectedCommandCount++;
                    return;
                }
                ref var data = ref Entities.Get(id);
                if (!Prototypes.TryGetById(data.ProtoId, out var p) || p is not EntityPrototype proto)
                {
                    RejectedCommandCount++;
                    return;
                }
                int bx = data.X, by = data.Y;
                bool isBelt = proto is TransportBeltPrototype;
                bool isContainer = proto is ContainerPrototype;
                World.ClearArea(data.X, data.Y, proto.TileWidth, proto.TileHeight);
                Entities.Destroy(id);
                if (isBelt)
                    Belts.RemoveBelt(bx, by);
                if (isContainer)
                    Inventories.RemoveContainer(id);   // M1: 返回的物品总数丢弃(策略层 P5 起再定)
                return;
            }
            default:
                RejectedCommandCount++;
                return;
        }
    }
}
