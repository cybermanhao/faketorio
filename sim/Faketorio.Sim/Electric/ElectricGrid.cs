using Faketorio.Sim;
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

    private readonly Dictionary<NetworkId, Registration> _registrations = new();
    private readonly Dictionary<EntityId, long> _allocatedSupply = new();
    private readonly Dictionary<EntityId, Q16> _satisfaction = new();

    private readonly Dictionary<EntityId, long> _fuelBufferJ = new();

    public void RegisterGenerator(EntityId id) => _fuelBufferJ[id] = 0;
    public void UnregisterGenerator(EntityId id) => _fuelBufferJ.Remove(id);
    public long GetFuelBufferJ(EntityId id) => _fuelBufferJ.TryGetValue(id, out var v) ? v : 0;
    public void SetFuelBufferJ(EntityId id, long value) => _fuelBufferJ[id] = value;

    // 只序列化燃料缓冲——连通分量/结算结果都是每 tick 派生数据,不持久化。
    public void WriteState(Faketorio.Sim.State.IStateWriter writer)
    {
        var ids = new List<EntityId>(_fuelBufferJ.Keys);
        ids.Sort((a, b) => a.Index.CompareTo(b.Index));
        writer.Write(ids.Count);
        foreach (var id in ids)
        {
            writer.Write(id.Index);
            writer.Write(id.Generation);
            writer.Write(_fuelBufferJ[id]);
        }
    }

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

    // 登记本 tick 的供给/需求。查不到网络(没接上电线杆)->直接记 0/Zero,不进结算。
    // 每个实体每 tick 只登记一次(生产者/消费者各自 tick 逻辑只调一次)。
    public void RegisterSupply(EntityId id, int x, int y, UsagePriority priority, long maxJThisTick)
    {
        var net = FindNetworkAt(x, y);
        if (!net.IsValid) { _allocatedSupply[id] = 0; return; }
        GetOrCreateRegistration(net).Supply.Add((id, priority, maxJThisTick));
    }

    public void RegisterDemand(EntityId id, int x, int y, UsagePriority priority, long amountJ)
    {
        var net = FindNetworkAt(x, y);
        if (!net.IsValid) { _satisfaction[id] = Q16.Zero; return; }
        GetOrCreateRegistration(net).Demand.Add((id, priority, amountJ));
    }

    public long GetAllocatedSupply(EntityId id) => _allocatedSupply.TryGetValue(id, out var v) ? v : 0;
    public Q16 GetSatisfaction(EntityId id) => _satisfaction.TryGetValue(id, out var v) ? v : Q16.Zero;

    // 每 tick 调一次。先清空上一轮的结果——否则本 tick 没重新登记的实体会
    // 读到上一轮的陈旧 allocated/satisfaction 而不是"未登记"的默认值 0/Zero
    // (M1 里发电机每 tick 都会重新登记,不会踩到;但 ElectricGrid 独立测试
    // 一旦某个实体某 tick 不登记,必须正确掉回默认值,不能读到旧数据)。
    // 每个网络独立结算,结果互不影响,故 _registrations 的处理顺序不影响
    // 正确性;仍按 NetworkId.Index 排序处理,避免任何疑虑。
    public void Settle()
    {
        _allocatedSupply.Clear();
        _satisfaction.Clear();
        var nets = new List<NetworkId>(_registrations.Keys);
        nets.Sort((a, b) => a.Index.CompareTo(b.Index));
        foreach (var net in nets)
            SettleNetwork(_registrations[net]);
        _registrations.Clear();
    }

    private static readonly UsagePriority[] SupplyTierOrder =
        { UsagePriority.Solar, UsagePriority.PrimaryOutput, UsagePriority.SecondaryOutput };
    private static readonly UsagePriority[] DemandTierReverseOrder =
        { UsagePriority.Tertiary, UsagePriority.SecondaryInput, UsagePriority.PrimaryInput };

    private void SettleNetwork(Registration reg)
    {
        long totalDemand = 0;
        foreach (var (_, _, amount) in reg.Demand) totalDemand += amount;

        long remaining = totalDemand;
        foreach (var tier in SupplyTierOrder)
        {
            var tierEntries = reg.Supply.FindAll(e => e.Priority == tier);
            long tierCapacity = 0;
            foreach (var e in tierEntries) tierCapacity += e.Amount;
            long used = Math.Min(tierCapacity, remaining);
            if (tierCapacity > 0)
            {
                // 按 e.Amount * used / tierCapacity 独立算每个生产者的份额,不保证 Σ == used
                // (例如 3 个 Amount=1 的生产者、used=2、capacity=3:每个独立算 1*2/3=0,
                // Σ=0 != used=2,凭空"丢电"——下游 shortfall/satisfaction 已经按 used 记账,
                // 会跟 GetAllocatedSupply 的实际读数对不上)。
                //
                // 用最大余数法(Hamilton apportionment)分配:先各自向下取整,
                // 再把 used 减去各 floor 之和剩下的 leftover 个单位,按"取整时丢掉的余数"
                // 从大到小(平局按 EntityId.Index 升序,保证确定性)逐个 +1 分给对应生产者。
                // 这样 Σ allocated 精确等于 used;并且由于 used <= tierCapacity,每个
                // floor(e.Amount*used/tierCapacity) 严格 < e.Amount(当 used < tierCapacity 时),
                // 所以 +1 之后仍然 <= e.Amount,不会有生产者被分到超过自己声明产能的份额
                // ——直接把"最后一个吃全部余数"会破坏这个上限(3 个 Amount=1,used=2 时,
                // 前两个 floor 都是 0,最后一个会被塞进整个 2,超过它自己的产能 1)。
                var shares = new long[tierEntries.Count];
                var remainders = new long[tierEntries.Count];
                long sumFloor = 0;
                for (int i = 0; i < tierEntries.Count; i++)
                {
                    var e = tierEntries[i];
                    shares[i] = e.Amount * used / tierCapacity;
                    remainders[i] = e.Amount * used % tierCapacity;
                    sumFloor += shares[i];
                }
                long leftover = used - sumFloor;
                var order = new List<int>();
                for (int i = 0; i < tierEntries.Count; i++) order.Add(i);
                order.Sort((a, b) =>
                {
                    int cmp = remainders[b].CompareTo(remainders[a]);   // 余数大的优先
                    return cmp != 0 ? cmp : tierEntries[a].Id.Index.CompareTo(tierEntries[b].Id.Index);
                });
                for (int k = 0; k < leftover; k++) shares[order[k]] += 1;
                for (int i = 0; i < tierEntries.Count; i++) _allocatedSupply[tierEntries[i].Id] = shares[i];
            }
            remaining -= used;
        }
        long shortfall = remaining;

        foreach (var tier in DemandTierReverseOrder)
        {
            var tierEntries = reg.Demand.FindAll(e => e.Priority == tier);
            long tierDemand = 0;
            foreach (var e in tierEntries) tierDemand += e.Amount;
            long absorbed = Math.Min(tierDemand, shortfall);
            Q16 satisfaction = tierDemand > 0 ? Q16.FromRatio(tierDemand - absorbed, tierDemand) : Q16.One;
            foreach (var e in tierEntries) _satisfaction[e.Id] = satisfaction;
            shortfall -= absorbed;
        }
    }

    private Registration GetOrCreateRegistration(NetworkId net)
    {
        if (!_registrations.TryGetValue(net, out var reg))
            _registrations[net] = reg = new Registration();
        return reg;
    }
}

internal readonly record struct PoleInfo(int X, int Y, int MaximumWireDistanceTiles, int SupplyAreaDistanceTiles);

internal sealed class Network
{
    public List<EntityId> Poles = new();
}

internal sealed class Registration
{
    public List<(EntityId Id, UsagePriority Priority, long Amount)> Supply = new();
    public List<(EntityId Id, UsagePriority Priority, long Amount)> Demand = new();
}
