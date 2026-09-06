using Faketorio.Sim.Entities;
using Faketorio.Sim.State;

namespace Faketorio.Sim;   // 扁平——不是 Faketorio.Sim.Inserters,同 Player/Machines/MiningDrills 的坑

// 机械臂运行时状态容器。刻意不知道 Simulation/Prototypes/Inventories/Belts/
// ElectricGrid——只收裸 EntityId/int/long,同 ElectricGrid/Machines/MiningDrills 的隔离原则。
// 抓取/放置格解析、库存/传送带读写、电网登记全部在 Simulation 的机械臂 tick 方法里做。
//
// SwingProgress 的三个阶段(由 HeldItemProtoId + 进度值区分,无单独的 phase 字段):
//   Held == 0 && Progress == 0        : 停在抓取角,空手 —— 尝试抓
//   Held != 0                         : 往外摆,拿着物品 —— 进度推进到 HalfSwing 后尝试放
//   Held == 0 && Progress > 0         : 往回摆,空手 —— 进度推进到 FullSwing 后归 0(能再抓)
public sealed class Inserters
{
    public const long HalfSwing = 1L << 16;      // Q16.One:半程(抓取角 → 放置角)
    public const long FullSwing = HalfSwing * 2; // 一整个周期(抓 → 摆出 → 放 → 摆回)

    private readonly Dictionary<EntityId, InserterState> _states = new();
    private readonly OrderedEntityIdList _order = new();

    public IReadOnlyList<EntityId> ActiveIds => _order.Ids;
    internal List<EntityId> ActiveIdsList => _order.IdsList;
    internal int StateCount => _states.Count;

    public void RegisterInserter(EntityId id)
    {
        _states[id] = new InserterState(0, 0);
        _order.Add(id);
    }

    public void UnregisterInserter(EntityId id)
    {
        _states.Remove(id);
        _order.Remove(id);
    }

    public int GetHeldItemProtoId(EntityId id) => _states.TryGetValue(id, out var s) ? s.HeldItemProtoId : 0;
    public long GetSwingProgress(EntityId id) => _states.TryGetValue(id, out var s) ? s.SwingProgress : 0;

    // 空手抓起:设手上物品,进度归 0(从抓取角开始往外摆)。前置:已注册。
    public void Grab(EntityId id, int itemProtoId)
    {
        _ = _states[id];   // throw-on-missing,同 P9 Machines / P10 MiningDrills 的一致性要求
        _states[id] = _states[id] with { HeldItemProtoId = itemProtoId, SwingProgress = 0 };
    }

    // 摆臂推进(往外或往回都用它)。前置:已注册。
    public void AddSwing(EntityId id, long delta)
    {
        var s = _states[id];
        _states[id] = s with { SwingProgress = s.SwingProgress + delta };
    }

    // 到放置角、放置成功:清手,进度钉在半程(接着从半程往回摆)。前置:已注册。
    public void Release(EntityId id)
    {
        _ = _states[id];
        _states[id] = _states[id] with { HeldItemProtoId = 0, SwingProgress = HalfSwing };
    }

    // 摆回抓取角:整个周期结束,进度归 0(下 tick 可以再抓)。前置:已注册。
    public void ArriveAtPickup(EntityId id)
    {
        _ = _states[id];
        _states[id] = _states[id] with { SwingProgress = 0 };
    }

    // 按 EntityId.Index 排序后写:index/代数/手上物品 id/摆臂进度。
    public void WriteState(IStateWriter writer)
    {
        writer.Write(_order.Count);
        foreach (var id in _order.Ids)
        {
            var s = _states[id];
            writer.Write(id.Index);
            writer.Write(id.Generation);
            writer.Write(s.HeldItemProtoId);
            writer.Write(s.SwingProgress);
        }
    }
}

internal readonly record struct InserterState(int HeldItemProtoId, long SwingProgress);
