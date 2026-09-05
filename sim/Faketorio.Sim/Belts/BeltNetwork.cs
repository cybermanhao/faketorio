using Faketorio.Sim.State;

namespace Faketorio.Sim.Belts;

// 传送带线的容器:持有 BeltLine 池 + tile→线 索引。放置传送带时 AddBelt
// 触发合并(设计文档第 5 节);每 tick 推进与状态哈希按池索引序遍历。
// 本类不知道 Simulation / Entities 的存在(Plan 3b 边界)。
public sealed class BeltNetwork
{
    private readonly BeltLinePool _pool = new();
    private readonly TileToLineIndex _tiles = new();

    public int Capacity => _pool.Capacity;
    public bool IsAliveAtIndex(int index) => _pool.IsAliveAtIndex(index);
    public BeltLine GetAtIndex(int index) => _pool.GetAtIndex(index);

    public BeltLineId GetLineAt(int x, int y) => _tiles.Get(x, y);
    public BeltLine GetLine(BeltLineId id) => _pool.Get(id);

    // 放置一格朝 direction 的传送带,返回它最终所属的线。
    // 前置条件:(x, y) 未被任何传送带线占用(见 Global Constraints)。
    public BeltLineId AddBelt(int x, int y, byte direction)
    {
        // 先校验方向:非法 direction 在任何分配/索引写入之前抛出,
        // 不会留下方向非法的孤儿线污染池与 tile 索引 / WriteState。
        var (dx, dy) = Delta(direction);

        var t = new BeltLine(direction,
            new List<(int X, int Y)> { (x, y) },
            new BeltLane(BeltLine.TileSubTiles),
            new BeltLane(BeltLine.TileSubTiles));
        var tid = _pool.Create(t);
        _tiles.Set(x, y, tid);

        var back = (x - dx, y - dy);    // B:T 的上游邻格
        var front = (x + dx, y + dy);   // F:T 的下游邻格

        var bId = _tiles.Get(back.Item1, back.Item2);
        var fId = _tiles.Get(front.Item1, front.Item2);

        bool bHit = bId.IsValid && _pool.IsAlive(bId)
            && _pool.Get(bId).Direction == direction
            && _pool.Get(bId).Tiles[0] == back;
        bool fHit = fId.IsValid && _pool.IsAlive(fId)
            && _pool.Get(fId).Direction == direction
            && _pool.Get(fId).Tiles[^1] == front;

        if (bHit && fHit)
        {
            var lUp = _pool.Get(bId);
            var lDown = _pool.Get(fId);
            int combinedLen = lUp.LengthSubTiles + BeltLine.TileSubTiles + lDown.LengthSubTiles;

            var newA = ConcatLanes(lUp.LaneA, lDown.LaneA, lDown.LengthSubTiles, combinedLen);
            var newB = ConcatLanes(lUp.LaneB, lDown.LaneB, lDown.LengthSubTiles, combinedLen);

            var newTiles = new List<(int X, int Y)>(lDown.Tiles.Count + 1 + lUp.Tiles.Count);
            newTiles.AddRange(lDown.Tiles);
            newTiles.Add((x, y));
            newTiles.AddRange(lUp.Tiles);

            var merged = new BeltLine(direction, newTiles, newA, newB);
            var newId = _pool.Create(merged);
            foreach (var (tx, ty) in newTiles)
                _tiles.Set(tx, ty, newId);

            _pool.Destroy(bId);
            _pool.Destroy(fId);
            _pool.Destroy(tid);
            return newId;
        }

        if (bHit)
        {
            var l = _pool.Get(bId);
            l.LaneA.ExtendFront(BeltLine.TileSubTiles);
            l.LaneB.ExtendFront(BeltLine.TileSubTiles);
            l.Tiles.Insert(0, (x, y));
            _tiles.Set(x, y, bId);
            _pool.Destroy(tid);
            return bId;
        }
        if (fHit)
        {
            var l = _pool.Get(fId);
            l.LaneA.ExtendBack(BeltLine.TileSubTiles);
            l.LaneB.ExtendBack(BeltLine.TileSubTiles);
            l.Tiles.Add((x, y));
            _tiles.Set(x, y, fId);
            _pool.Destroy(tid);
            return fId;
        }

        return tid;
    }

    // 规范序列化(spec 铁律 4)。先写分配器簿记,再按池索引序写每条存活线的
    // 内容:Direction、Tiles(数量 + 每格坐标)、两条 lane 的 WriteState。
    // 与 Simulation.WriteState 写 Entities 的模式一致。
    public void WriteState(IStateWriter writer)
    {
        _pool.WriteState(writer);
        for (int i = 0; i < _pool.Capacity; i++)
        {
            if (!_pool.IsAliveAtIndex(i)) continue;
            var line = _pool.GetAtIndex(i);
            writer.Write(i);
            writer.Write(_pool.GenerationAtIndex(i));
            writer.Write(line.Direction);
            writer.Write(line.Tiles.Count);
            foreach (var (tx, ty) in line.Tiles)
            {
                writer.Write(tx);
                writer.Write(ty);
            }
            line.LaneA.WriteState(writer);
            line.LaneB.WriteState(writer);
        }
    }

    // 拆除 (x, y) 这格传送带。返回被丢弃物品的总个数(两条 lane 合计)。
    // 前置条件:(x, y) 属于一条存活线(见 Global Constraints)。
    // 端点摘除与中间拆分的前半段就地截短(id 不变);只有中间拆分的后半段是
    // 全新线。断口处身体跨进被移格的物品按丢弃处理(见设计文档第 6 / 10 节)。
    public int RemoveBelt(int x, int y)
    {
        const int L = BeltLine.TileSubTiles;
        const int W = BeltLane.ItemWidthSubTiles;

        var id = _tiles.Get(x, y);
        var line = _pool.Get(id);
        int n = line.Tiles.Count;
        int k = line.Tiles.IndexOf((x, y));

        if (n == 1)
        {
            int count = line.LaneA.Count + line.LaneB.Count;
            _pool.Destroy(id);
            _tiles.Clear(x, y);
            return count;
        }

        if (k == 0) // 出口端
        {
            int count = ClearRange(line.LaneA, 0, L) + ClearRange(line.LaneB, 0, L);
            line.LaneA.ShrinkFront(L);
            line.LaneB.ShrinkFront(L);
            line.Tiles.RemoveAt(0);
            _tiles.Clear(x, y);
            return count;
        }

        if (k == n - 1) // 入口端:低端向前拓宽 W-1,收身体跨进被移格的物品
        {
            int lo = (n - 1) * L - (W - 1);
            int count = ClearRange(line.LaneA, lo, n * L) + ClearRange(line.LaneB, lo, n * L);
            line.LaneA.ShrinkBack(L);
            line.LaneB.ShrinkBack(L);
            line.Tiles.RemoveAt(n - 1);
            _tiles.Clear(x, y);
            return count;
        }

        return MiddleSplit(line, k, n, x, y);
    }

    // 循环把前沿落在 [from, to) 内的物品从 lane 摘除,返回摘除数。
    private static int ClearRange(BeltLane lane, int from, int to)
    {
        int c = 0;
        while (lane.TryRemoveItemInRange(from, to)) c++;
        return c;
    }

    // 中间拆分:被移格下标 0 < k < n-1。前半段(原线,id 不变)就地 ShrinkBack;
    // 后半段(全新线)从快照重建,出口落在 (k+1)*L 亚格边界。返回丢弃物品数。
    private int MiddleSplit(BeltLine line, int k, int n, int x, int y)
    {
        const int L = BeltLine.TileSubTiles;
        const int W = BeltLane.ItemWidthSubTiles;
        int cut = (k + 1) * L;
        int backLen = (n - 1 - k) * L;

        // 1) 后半段:前沿 >= cut 的物品,前沿减 cut(在原线被 mutate 之前读快照)
        var backA = SplitBackLane(line.LaneA, cut, backLen);
        var backB = SplitBackLane(line.LaneB, cut, backLen);
        var backTiles = line.Tiles.GetRange(k + 1, n - 1 - k);
        var backId = _pool.Create(new BeltLine(line.Direction, backTiles, backA, backB));
        foreach (var (tx, ty) in backTiles)
            _tiles.Set(tx, ty, backId);

        // 2) 前半段:原线。先移走去后半段的(不计数),再丢弃跨界/被移格上的(计数)
        int discarded = 0;
        foreach (var lane in new[] { line.LaneA, line.LaneB })
        {
            while (lane.TryRemoveItemInRange(cut, n * L)) { }
            while (lane.TryRemoveItemInRange(k * L - (W - 1), n * L)) discarded++;
            lane.ShrinkBack((n - k) * L);
        }
        line.Tiles.RemoveRange(k, n - k);

        // 3) 被移格
        _tiles.Clear(x, y);
        return discarded;
    }

    // 从 src 的绝对位置快照里取前沿 >= cut 的物品,前沿减 cut,重建一条长
    // backLen 的新 lane。前沿 < cut 的(前半段 / 被移格 / 跨界)一律不带进来。
    private static BeltLane SplitBackLane(BeltLane src, int cut, int backLen)
    {
        var back = new List<BeltLane.PositionedItem>();
        foreach (var p in src.ToAbsolutePositions())
            if (p.LeadingEdgeSubTiles >= cut) back.Add(p with { LeadingEdgeSubTiles = p.LeadingEdgeSubTiles - cut });
        return BeltLane.FromAbsolutePositions(backLen, back);
    }

    // 把 up(上游,拼在物理后侧)与 down(下游,拼在出口侧)两条带物品的
    // lane 拼成一条长 combinedLen 的新 lane。前沿绝对距离:down 的原样,
    // up 的每项整体后移 (256 + downLen)——越过新格 T 和整条 down。物品
    // 类型原样带过去,不参与位置计算。拼出的列表天然升序(up 最靠前项移位后
    // 仍远在 down 最靠后项之后),直接交给 FromAbsolutePositions。
    private static BeltLane ConcatLanes(BeltLane up, BeltLane down, int downLen, int combinedLen)
    {
        var pos = new List<BeltLane.PositionedItem>(down.Count + up.Count);
        pos.AddRange(down.ToAbsolutePositions());
        int shift = BeltLine.TileSubTiles + downLen;
        foreach (var p in up.ToAbsolutePositions())
            pos.Add(p with { LeadingEdgeSubTiles = p.LeadingEdgeSubTiles + shift });
        return BeltLane.FromAbsolutePositions(combinedLen, pos);
    }

    // 方向 -> 单位位移。屏幕坐标(y 向下):北 = -y,南 = +y。
    public static (int dx, int dy) Delta(byte d) => d switch
    {
        0 => (0, -1),
        1 => (1, 0),
        2 => (0, 1),
        3 => (-1, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(d)),
    };
}
