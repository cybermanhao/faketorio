using Faketorio.Sim.Entities;
using Faketorio.Sim.World;

namespace Faketorio.Sim.Electric;

// 电网管理器。刻意不知道 Simulation/Entities/Inventories/Prototypes——只收
// 裸 EntityId + int 坐标 + long 能量。连通分量全量重算(拓扑变化才重算,
// 用 _topologyDirty 门控),不跨 tick 保留网络身份。
public sealed class ElectricGrid
{
    private readonly Dictionary<EntityId, PoleInfo> _poles = new();
    private bool _topologyDirty = true;
    private List<Network> _networks = new();

    public void RegisterPole(EntityId id, int x, int y, int maximumWireDistanceTiles, int supplyAreaDistanceTiles)
    {
        _poles[id] = new PoleInfo(x, y, maximumWireDistanceTiles, supplyAreaDistanceTiles);
        _topologyDirty = true;
    }

    public void UnregisterPole(EntityId id)
    {
        _poles.Remove(id);
        _topologyDirty = true;
    }

    // 方形(Chebyshev)供电覆盖区。网络索引序 + 网络内 EntityId.Index 序,
    // 重叠覆盖区第一个命中的赢——确定、可复现。
    public NetworkId FindNetworkAt(int x, int y)
    {
        EnsureTopology();
        for (int ni = 0; ni < _networks.Count; ni++)
            foreach (var poleId in _networks[ni].Poles)
            {
                var p = _poles[poleId];
                if (Math.Max(Math.Abs(x - p.X), Math.Abs(y - p.Y)) <= p.SupplyAreaDistanceTiles)
                    return new NetworkId(ni);
            }
        return NetworkId.Invalid;
    }

    private void EnsureTopology()
    {
        if (!_topologyDirty) return;
        _topologyDirty = false;

        var ids = new List<EntityId>(_poles.Keys);
        ids.Sort((a, b) => a.Index.CompareTo(b.Index));

        var parent = new Dictionary<EntityId, EntityId>();
        foreach (var id in ids) parent[id] = id;

        EntityId Find(EntityId x)
        {
            while (parent[x] != x) x = parent[x];
            return x;
        }
        void Union(EntityId a, EntityId b)
        {
            var ra = Find(a); var rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }

        for (int i = 0; i < ids.Count; i++)
            for (int j = i + 1; j < ids.Count; j++)
            {
                var a = _poles[ids[i]]; var b = _poles[ids[j]];
                long dx = a.X - b.X, dy = a.Y - b.Y;
                int dist = ValueNoise.Isqrt(dx * dx + dy * dy);
                int maxDist = Math.Min(a.MaximumWireDistanceTiles, b.MaximumWireDistanceTiles);
                if (dist <= maxDist) Union(ids[i], ids[j]);
            }

        var groups = new Dictionary<EntityId, List<EntityId>>();
        foreach (var id in ids)   // ids 已按 Index 升序,同组内追加顺序即升序
        {
            var root = Find(id);
            if (!groups.TryGetValue(root, out var list))
                groups[root] = list = new List<EntityId>();
            list.Add(id);
        }

        var networks = new List<Network>(groups.Count);
        foreach (var list in groups.Values)   // Dictionary 遍历序无所谓,下面显式排序
            networks.Add(new Network { Poles = list });
        networks.Sort((x, y) => x.Poles[0].Index.CompareTo(y.Poles[0].Index));
        _networks = networks;
    }
}

internal readonly record struct PoleInfo(int X, int Y, int MaximumWireDistanceTiles, int SupplyAreaDistanceTiles);

internal sealed class Network
{
    public List<EntityId> Poles = new();
}
