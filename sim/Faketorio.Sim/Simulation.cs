using Faketorio.Sim.Belts;
using Faketorio.Sim.Commands;
using Faketorio.Sim.Electric;
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
    public Player Player { get; }
    public ElectricGrid ElectricGrid { get; } = new();
    public Machines Machines { get; } = new();

    private readonly long _worldSeed;
    private readonly PlayerPrototype _playerProto;

    public long Tick { get; private set; }
    public int RejectedCommandCount { get; private set; }

    private readonly CommandQueue _commands = new();

    public Simulation(PrototypeRegistry prototypes, long worldSeed = 0)
    {
        Prototypes = prototypes;
        _worldSeed = worldSeed;
        Resources = new ResourceGrid(worldSeed, prototypes);
        if (!prototypes.TryGet<PlayerPrototype>("player", out var pp))
            throw new InvalidOperationException("No 'player' prototype in the registry");
        _playerProto = pp;
        Player = new Player(pp.InventorySize);
        foreach (var ia in pp.StartingInventory)
        {
            var item = prototypes.Get<ItemPrototype>(ia.Name);
            Player.Inventory.Insert(item.Id, ia.Amount, item.StackSize);   // 放不下静默丢弃(InventorySize >= 1 已校验)
        }
    }

    public void Submit(in Command command) => _commands.Enqueue(command);

    public void Step()
    {
        var commands = _commands.BeginTick();
        for (int i = 0; i < commands.Length; i++)
            Apply(in commands[i]);

        // 玩家 tick:① 行走(碰撞) ② 挖掘(Task 3) ③ 合成(Task 4)
        PlayerWalk();
        PlayerMine();
        PlayerCraft();

        // 电网 + 加工:① 发电机登记供给 ② 机器登记需求 ③ 结算 ④ 发电机烧油 ⑤ 机器推进+完成
        ElectricGeneratorsRegisterSupply();
        MachinesTickPreSettle();
        ElectricGrid.Settle();
        ElectricGeneratorsBurnFuel();
        MachinesTickPostSettle();

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
            while (line.LaneA.IsFrontReady && down.LaneA.TryInsertAtBack(line.LaneA.FrontItemProtoId)) line.LaneA.RemoveFront();
            while (line.LaneB.IsFrontReady && down.LaneB.TryInsertAtBack(line.LaneB.FrontItemProtoId)) line.LaneB.RemoveFront();
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
        Player.WriteState(writer);
        ElectricGrid.WriteState(writer);
        Machines.WriteState(writer);
    }

    private void PlayerWalk()
    {
        if (!Player.Walking) return;
        var (dx, dy) = Player.WalkDelta(Player.WalkDir, _playerProto.WalkSpeedSubTilesPerTick);
        int nx = Player.X + dx, ny = Player.Y + dy;
        if (World.GetEntityAt(nx >> 8, ny >> 8).IsValid) return;   // 整步拒绝,不滑墙
        Player.MoveTo(nx, ny);
    }

    private void PlayerMine()
    {
        if (!Player.Mining) return;
        int tx = Player.MineTargetX, ty = Player.MineTargetY;

        // reach:玩家点到目标格中心的欧氏距离(整数 sqrt,P6)
        long ddx = Player.X - (tx * 256 + 128);
        long ddy = Player.Y - (ty * 256 + 128);
        if (ValueNoise.Isqrt(ddx * ddx + ddy * ddy) > _playerProto.ReachSubTiles) return;

        // 解析目标:优先实体,其次矿脉
        var eid = World.GetEntityAt(tx, ty);
        EntityPrototype? entityProto = null;
        if (eid.IsValid && Entities.IsAlive(eid)
            && Prototypes.TryGetById(Entities.Get(eid).ProtoId, out var ep)
            && ep is EntityPrototype epx && epx.MinableResult is not null)
            entityProto = epx;

        ResourcePrototype? resProto = null;
        if (entityProto is null)
        {
            var cell = Resources.GetResourceAt(tx, ty);
            if (!cell.IsEmpty)
                resProto = (ResourcePrototype)Prototypes.GetById(cell.ResourceProtoId);
        }

        if (entityProto is null && resProto is null) return;   // 空转

        int threshold = entityProto?.MiningTimeTicks ?? resProto!.MiningTimeTicks;
        if (Player.MineProgress < threshold) Player.TickMineProgress();
        if (Player.MineProgress < threshold) return;

        string resultName = entityProto?.MinableResult ?? resProto!.MinableResult;
        var itemProto = Prototypes.Get<ItemPrototype>(resultName);
        if (Player.Inventory.Insert(itemProto.Id, 1, itemProto.StackSize) == 0) return;   // 背包满:停在阈值

        if (entityProto is not null)
        {
            DestroyEntityAt(eid, entityProto);   // 目标失效,下 tick 空转
        }
        else
        {
            Resources.Extract(tx, ty, 1);        // 必返回 1(刚查过非空)
            Player.ClearMineProgress();           // 继续挖
        }
    }

    private void PlayerCraft()
    {
        if (Player.CraftQueue.Count == 0) return;
        var job = Player.CraftQueue[0];
        var recipe = (RecipePrototype)Prototypes.GetById(job.RecipeProtoId);

        // 阻塞在阈值:一旦到达 EnergyRequiredTicks 就不再累加进度(否则被卡住的
        // job 每 tick 都往上加,Progress 会无界超过阈值——手挖同款"停在阈值"约定)。
        if (job.Progress < recipe.EnergyRequiredTicks) Player.TickCraftHeadProgress();
        if (Player.CraftQueue[0].Progress < recipe.EnergyRequiredTicks) return;

        // 全有或全无预检(M1 配方单产物)
        foreach (var res in recipe.ResolvedResults)
        {
            var ip = Prototypes.GetById(res.ItemProtoId);
            int stack = ((ItemPrototype)ip).StackSize;
            if (!Player.Inventory.CanInsert(res.ItemProtoId, res.Amount, stack)) return;   // 阻塞
        }
        foreach (var res in recipe.ResolvedResults)
        {
            int stack = ((ItemPrototype)Prototypes.GetById(res.ItemProtoId)).StackSize;
            Player.Inventory.Insert(res.ItemProtoId, res.Amount, stack);
        }
        Player.CompleteOneCraftUnit();
    }

    // 只读地算出 inv 还能吃下多少个 itemProtoId(镜像 Inventory.CanInsert 的两轮逻辑,
    // 但返回精确数量而非 bool)。ReadOnly/过滤不匹配 -> 0,不改任何状态。
    // TransferFromEntity 用它在 Remove 之前把搬运量夹到"玩家背包确实吃得下"的量,
    // 避免"先扣源、玩家吃不下再放回源"这条路径——源如果是 readOnly(P9 机器输出槽),
    // 放回去的 Insert 会静默失败(返回 0),物品就凭空消失。
    private static int AvailableSpace(Inventory inv, int itemProtoId, int stackSize)
    {
        if (inv.ReadOnly) return 0;
        if (inv.FilterItemProtoId != 0 && itemProtoId != inv.FilterItemProtoId) return 0;
        if (stackSize <= 0) return 0;

        int space = 0;
        for (int i = 0; i < inv.SlotCount; i++)
        {
            var slot = inv[i];
            if (slot.ItemProtoId == itemProtoId && slot.Count < stackSize)
                space += stackSize - slot.Count;
            else if (slot.IsEmpty)
                space += stackSize;
        }
        return space;
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
                if (proto is ElectricPolePrototype pole)
                    ElectricGrid.RegisterPole(id, command.X, command.Y, pole.MaximumWireDistanceTiles, pole.SupplyAreaDistanceTiles);
                if (proto is FuelGeneratorPrototype gen)
                {
                    Inventories.AddContainer(id, 1, filterItemProtoId: gen.FuelItemProtoId);
                    ElectricGrid.RegisterGenerator(id);
                }
                if (proto is CraftingMachinePrototype cmp)
                {
                    Inventories.AddContainer(id, cmp.InputSlots, role: 1);
                    Inventories.AddContainer(id, cmp.OutputSlots, role: 2);
                    Machines.RegisterMachine(id);
                }
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
                DestroyEntityAt(id, proto);
                return;
            }
            case CommandType.MovePlayer:
                if (command.Rotation > 7) { RejectedCommandCount++; return; }
                Player.SetWalk(command.Rotation);
                return;
            case CommandType.StopPlayer:
                Player.StopWalk();
                return;
            case CommandType.MineStart:
                Player.SetMineTarget(command.X, command.Y);
                return;
            case CommandType.MineStop:
                Player.StopMining();
                return;
            case CommandType.TransferToEntity:
            {
                long ddx = Player.X - (command.X * 256 + 128);
                long ddy = Player.Y - (command.Y * 256 + 128);
                if (ValueNoise.Isqrt(ddx * ddx + ddy * ddy) > _playerProto.ReachSubTiles
                    || !Prototypes.TryGetById(command.ProtoId, out var ip) || ip is not ItemPrototype itemProto
                    || command.Count <= 0)
                {
                    RejectedCommandCount++;
                    return;
                }
                var eid = World.GetEntityAt(command.X, command.Y);
                int targetRole = eid.IsValid && Prototypes.TryGetById(Entities.Get(eid).ProtoId, out var targetProto)
                    && targetProto is CraftingMachinePrototype ? 1 : 0;
                var targetInvId = eid.IsValid ? Inventories.GetInventoryId(eid, targetRole) : InventoryId.Invalid;
                if (!targetInvId.IsValid)
                {
                    RejectedCommandCount++;
                    return;
                }
                int amount = Math.Min(command.Count, Player.Inventory.CountOf(command.ProtoId));
                if (amount <= 0)
                {
                    RejectedCommandCount++;
                    return;
                }
                var targetInv = Inventories.Get(targetInvId);
                int inserted = targetInv.Insert(command.ProtoId, amount, itemProto.StackSize);
                Player.Inventory.Remove(command.ProtoId, inserted);
                return;
            }
            case CommandType.TransferFromEntity:
            {
                long ddx2 = Player.X - (command.X * 256 + 128);
                long ddy2 = Player.Y - (command.Y * 256 + 128);
                if (ValueNoise.Isqrt(ddx2 * ddx2 + ddy2 * ddy2) > _playerProto.ReachSubTiles
                    || !Prototypes.TryGetById(command.ProtoId, out var ip2) || ip2 is not ItemPrototype itemProto2
                    || command.Count <= 0)
                {
                    RejectedCommandCount++;
                    return;
                }
                var eid2 = World.GetEntityAt(command.X, command.Y);
                int sourceRole = eid2.IsValid && Prototypes.TryGetById(Entities.Get(eid2).ProtoId, out var sourceProto)
                    && sourceProto is CraftingMachinePrototype ? 2 : 0;
                var sourceInvId = eid2.IsValid ? Inventories.GetInventoryId(eid2, sourceRole) : InventoryId.Invalid;
                if (!sourceInvId.IsValid)
                {
                    RejectedCommandCount++;
                    return;
                }
                var sourceInv = Inventories.Get(sourceInvId);
                // 先算出玩家背包实际能吃下多少(不改状态),再夹到 min(请求量, 源库存现有量, 玩家能吃量)。
                // 这样 Remove 和 Insert 的数量必然一致,不需要"塞不下再放回去"——放回去这条路径
                // 在 sourceInv 是 readOnly(P9 机器输出槽)时会把差额静默丢掉(Insert 对 readOnly 恒返回 0)。
                int space = AvailableSpace(Player.Inventory, command.ProtoId, itemProto2.StackSize);
                int amount2 = Math.Min(Math.Min(command.Count, sourceInv.CountOf(command.ProtoId)), space);
                if (amount2 <= 0)
                {
                    RejectedCommandCount++;
                    return;
                }
                int removed = sourceInv.Remove(command.ProtoId, amount2);
                int inserted2 = Player.Inventory.Insert(command.ProtoId, removed, itemProto2.StackSize);
                System.Diagnostics.Debug.Assert(inserted2 == removed, "TransferFromEntity: amount was pre-clamped to available space, insert should never partially fail");
                return;
            }
            case CommandType.SetRecipe:
            {
                var mid = World.GetEntityAt(command.X, command.Y);
                if (!mid.IsValid || !Entities.IsAlive(mid)
                    || !Prototypes.TryGetById(Entities.Get(mid).ProtoId, out var mp) || mp is not AssemblingMachinePrototype amp
                    || !Prototypes.TryGetById(command.ProtoId, out var rp2) || rp2 is not RecipePrototype recipe2
                    || recipe2.Category != amp.Category)
                {
                    RejectedCommandCount++;
                    return;
                }
                Machines.SetRecipe(mid, command.ProtoId);
                return;
            }
            case CommandType.CraftEnqueue:
            {
                if (command.X < 1 || command.X > 1_000_000
                    || !Prototypes.TryGetById(command.ProtoId, out var rp) || rp is not RecipePrototype recipe
                    || recipe.Category != "crafting" || !recipe.Enabled
                    || Player.CraftQueue.Count >= _playerProto.CraftQueueCap)
                {
                    RejectedCommandCount++;
                    return;
                }
                // 检查能否付清 X 份的全部输入
                foreach (var ing in recipe.ResolvedIngredients)
                    if ((long)Player.Inventory.CountOf(ing.ItemProtoId) < (long)ing.Amount * command.X)
                    {
                        RejectedCommandCount++;
                        return;
                    }
                foreach (var ing in recipe.ResolvedIngredients)
                    Player.Inventory.Remove(ing.ItemProtoId, (int)((long)ing.Amount * command.X));
                Player.EnqueueCraft(command.ProtoId, command.X);
                return;
            }
            default:
                RejectedCommandCount++;
                return;
        }
    }

    // 移除一个实体:清占地 + 销毁 + belt/container 后处理。命令路径(RemoveEntity)
    // 和手挖(Task 3)共用。调用方保证 id 存活、proto 匹配。
    private void DestroyEntityAt(EntityId id, EntityPrototype proto)
    {
        ref var data = ref Entities.Get(id);
        int bx = data.X, by = data.Y;
        bool isBelt = proto is TransportBeltPrototype;
        bool isContainer = proto is ContainerPrototype;
        bool isPole = proto is ElectricPolePrototype;
        bool isGenerator = proto is FuelGeneratorPrototype;
        bool isMachine = proto is CraftingMachinePrototype;
        World.ClearArea(data.X, data.Y, proto.TileWidth, proto.TileHeight);
        Entities.Destroy(id);
        if (isBelt) Belts.RemoveBelt(bx, by);
        if (isContainer) Inventories.RemoveContainer(id);
        if (isPole) ElectricGrid.UnregisterPole(id);
        if (isGenerator)
        {
            Inventories.RemoveContainer(id);
            ElectricGrid.UnregisterGenerator(id);
        }
        if (isMachine)
        {
            Inventories.RemoveContainer(id, role: 1);
            Inventories.RemoveContainer(id, role: 2);
            Machines.UnregisterMachine(id);
        }
    }

    // 扫实体池找发电机(同 belt 推进/WriteState 的索引序扫法,不给 ElectricGrid
    // 塞 Inventories/Prototypes 依赖)。登记「有燃料就能出满功率,没燃料出 0」。
    private void ElectricGeneratorsRegisterSupply()
    {
        for (int i = 0; i < Entities.Capacity; i++)
        {
            if (!Entities.IsAliveAtIndex(i)) continue;
            ref var data = ref Entities.GetAtIndex(i);
            if (!Prototypes.TryGetById(data.ProtoId, out var p) || p is not FuelGeneratorPrototype gen) continue;
            var id = new EntityId(i, Entities.GenerationAtIndex(i));
            long buf = ElectricGrid.GetFuelBufferJ(id);
            var fuelInv = Inventories.Get(Inventories.GetInventoryId(id));
            bool hasFuel = buf > 0 || fuelInv.CountOf(gen.FuelItemProtoId) > 0;
            ElectricGrid.RegisterSupply(id, data.X, data.Y, UsagePriority.PrimaryOutput, hasFuel ? gen.PowerOutputJPerTick : 0);
        }
    }

    private void ElectricGeneratorsBurnFuel()
    {
        for (int i = 0; i < Entities.Capacity; i++)
        {
            if (!Entities.IsAliveAtIndex(i)) continue;
            ref var data = ref Entities.GetAtIndex(i);
            if (!Prototypes.TryGetById(data.ProtoId, out var p) || p is not FuelGeneratorPrototype gen) continue;
            var id = new EntityId(i, Entities.GenerationAtIndex(i));
            long actual = ElectricGrid.GetAllocatedSupply(id);
            long buf = ElectricGrid.GetFuelBufferJ(id);
            var fuelInv = Inventories.Get(Inventories.GetInventoryId(id));
            var fuelItemProto = (ItemPrototype)Prototypes.GetById(gen.FuelItemProtoId);
            while (buf < actual && fuelInv.CountOf(gen.FuelItemProtoId) > 0)
            {
                fuelInv.Remove(gen.FuelItemProtoId, 1);
                buf += fuelItemProto.FuelValueJ;
            }
            long delivered = Math.Min(actual, buf);
            ElectricGrid.SetFuelBufferJ(id, buf - delivered);
        }
    }

    // 加工:配方选定 + 电力需求登记(Settle() 之前)。两趟扫描的第一趟——全部机器
    // 先登记完需求,ElectricGrid.Settle() 才能看到本 tick 完整的需求总量。
    private void MachinesTickPreSettle()
    {
        for (int i = 0; i < Entities.Capacity; i++)
        {
            if (!Entities.IsAliveAtIndex(i)) continue;
            ref var data = ref Entities.GetAtIndex(i);
            if (!Prototypes.TryGetById(data.ProtoId, out var p) || p is not CraftingMachinePrototype proto) continue;
            var id = new EntityId(i, Entities.GenerationAtIndex(i));
            MachineTickPreSettle(id, proto, data.X, data.Y);
        }
    }

    private void MachineTickPreSettle(EntityId id, CraftingMachinePrototype proto, int x, int y)
    {
        // 第 1 步:flush 已完成的产出
        if (Machines.IsCompleted(id))
        {
            var doneRecipe = (RecipePrototype)Prototypes.GetById(Machines.GetCurrentRecipe(id));
            var outputInv = Inventories.Get(Inventories.GetInventoryId(id, 2));
            bool fits = true;
            foreach (var res in doneRecipe.ResolvedResults)
            {
                int stack = ((ItemPrototype)Prototypes.GetById(res.ItemProtoId)).StackSize;
                if (!outputInv.CanInsert(res.ItemProtoId, res.Amount, stack)) { fits = false; break; }
            }
            if (fits)
            {
                foreach (var res in doneRecipe.ResolvedResults)
                {
                    int stack = ((ItemPrototype)Prototypes.GetById(res.ItemProtoId)).StackSize;
                    outputInv.Insert(res.ItemProtoId, res.Amount, stack);
                }
                Machines.RestartCycle(id, clearRecipe: proto is FurnacePrototype);
                // 本 tick 继续走到第 2 步(不用等下一 tick),fits==true 时不 return。
            }
            else
            {
                // 输出堵塞:跳过第 2 步(不重新匹配/不能开始新一轮),但仍登记待机能耗。
                ElectricGrid.RegisterDemand(id, x, y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick);
                return;
            }
        }

        // 第 2 步:配方选定(仅当当前没有配方)
        if (Machines.GetCurrentRecipe(id) == -1 && proto is FurnacePrototype)
        {
            var inputInv = Inventories.Get(Inventories.GetInventoryId(id, 1));
            for (int rid = 0; rid < Prototypes.Count; rid++)
            {
                if (Prototypes.GetById(rid) is not RecipePrototype recipe || recipe.Category != proto.Category) continue;
                bool satisfied = true;
                foreach (var ing in recipe.ResolvedIngredients)
                    if (inputInv.CountOf(ing.ItemProtoId) < ing.Amount) { satisfied = false; break; }
                if (satisfied) { Machines.SetRecipe(id, recipe.Id); break; }
            }
        }
        // 装配机:不自动匹配,只用玩家此前 SetRecipe 设置的结果(可能仍是 -1)。

        // 第 3 步:电力需求登记(无条件——恒定待机能耗)
        ElectricGrid.RegisterDemand(id, x, y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick);
    }

    // 加工:进度推进 + 完成校验(Settle() 之后,可读 satisfaction)。两趟扫描的第二趟。
    private void MachinesTickPostSettle()
    {
        for (int i = 0; i < Entities.Capacity; i++)
        {
            if (!Entities.IsAliveAtIndex(i)) continue;
            ref var data = ref Entities.GetAtIndex(i);
            if (!Prototypes.TryGetById(data.ProtoId, out var p) || p is not CraftingMachinePrototype proto) continue;
            var id = new EntityId(i, Entities.GenerationAtIndex(i));
            MachineTickPostSettle(id, proto);
        }
    }

    private void MachineTickPostSettle(EntityId id, CraftingMachinePrototype proto)
    {
        int recipeId = Machines.GetCurrentRecipe(id);
        if (recipeId == -1 || Machines.IsCompleted(id)) return;   // 空转,或本 tick 刚 flush 失败仍在等

        var recipe = (RecipePrototype)Prototypes.GetById(recipeId);
        var inputInv = Inventories.Get(Inventories.GetInventoryId(id, 1));

        bool satisfied = true;
        foreach (var ing in recipe.ResolvedIngredients)
            if (inputInv.CountOf(ing.ItemProtoId) < ing.Amount) { satisfied = false; break; }
        if (!satisfied) return;   // 缺料:本 tick 冻结进度,不清零、不重置配方,等原料备齐

        long threshold = (long)recipe.EnergyRequiredTicks << 16;
        var satisfaction = ElectricGrid.GetSatisfaction(id);
        long delta = proto.CraftingSpeed.Mul(satisfaction.Raw);
        if (Machines.GetProgress(id) < threshold) Machines.AddProgress(id, delta);
        if (Machines.GetProgress(id) < threshold) return;   // 阻塞在阈值,不再累加

        // 完成前重校验(单线程 tick 内必然通过,是给未来机械臂/传送带的防御)+ 消耗原料
        bool stillSatisfied = true;
        foreach (var ing in recipe.ResolvedIngredients)
            if (inputInv.CountOf(ing.ItemProtoId) < ing.Amount) { stillSatisfied = false; break; }

        if (stillSatisfied)
        {
            foreach (var ing in recipe.ResolvedIngredients)
                inputInv.Remove(ing.ItemProtoId, ing.Amount);
            Machines.MarkCompleted(id);
        }
        else
        {
            Machines.RestartCycle(id, clearRecipe: proto is FurnacePrototype);
        }
    }
}
