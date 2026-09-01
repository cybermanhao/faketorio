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
        var t = new BeltLine(direction,
            new List<(int X, int Y)> { (x, y) },
            new BeltLane(BeltLine.TileSubTiles),
            new BeltLane(BeltLine.TileSubTiles));
        var tid = _pool.Create(t);
        _tiles.Set(x, y, tid);

        var (dx, dy) = Delta(direction);
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

    // 把 up(上游,拼在物理后侧)与 down(下游,拼在出口侧)两条带物品的
    // lane 拼成一条长 combinedLen 的新 lane。前沿绝对距离:down 的原样,
    // up 的每项整体后移 (256 + downLen)——越过新格 T 和整条 down。
    // 拼出的列表天然升序(up 最靠前项移位后仍远在 down 最靠后项之后),
    // 直接交给 FromAbsolutePositions。
    private static BeltLane ConcatLanes(BeltLane up, BeltLane down, int downLen, int combinedLen)
    {
        var pos = new List<int>(down.Count + up.Count);
        pos.AddRange(down.ToAbsolutePositions());
        int shift = BeltLine.TileSubTiles + downLen;
        foreach (int p in up.ToAbsolutePositions())
            pos.Add(p + shift);
        return BeltLane.FromAbsolutePositions(combinedLen, pos);
    }

    // 方向 -> 单位位移。屏幕坐标(y 向下):北 = -y,南 = +y。
    private static (int dx, int dy) Delta(byte d) => d switch
    {
        0 => (0, -1),
        1 => (1, 0),
        2 => (0, 1),
        3 => (-1, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(d)),
    };
}
