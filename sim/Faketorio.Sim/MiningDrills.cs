using Faketorio.Sim.Entities;
using Faketorio.Sim.State;

namespace Faketorio.Sim;   // 扁平——不是 Faketorio.Sim.MiningDrills,同 Player/Machines 的坑

// 采矿机运行时状态容器。刻意不知道 Simulation/Prototypes/Inventories/Belts/
// ElectricGrid/Resources——只收裸 EntityId/int/long/bool,同 ElectricGrid/
// Machines 的隔离原则。目标搜索、资源查询、电网登记、库存/传送带插入,
// 全部在 Simulation 的采矿 tick 方法里做。
public sealed class MiningDrills
{
    private readonly Dictionary<EntityId, DrillRuntimeState> _states = new();
    private readonly OrderedEntityIdList _order = new();

    public IReadOnlyList<EntityId> ActiveIds => _order.Ids;
    internal List<EntityId> ActiveIdsList => _order.IdsList;
    internal int StateCount => _states.Count;

    public void RegisterDrill(EntityId id)
    {
        _states[id] = new DrillRuntimeState(-1, -1, 0, false, 0);   // PendingRotation 默认 -1(无排队)
        _order.Add(id);
    }

    public void UnregisterDrill(EntityId id)
    {
        _states.Remove(id);
        _order.Remove(id);
    }

    public int GetTargetX(EntityId id) => _states.TryGetValue(id, out var s) ? s.TargetX : -1;
    public int GetTargetY(EntityId id) => _states.TryGetValue(id, out var s) ? s.TargetY : -1;
    public long GetProgress(EntityId id) => _states.TryGetValue(id, out var s) ? s.Progress : 0;
    public bool IsCompleted(EntityId id) => _states.TryGetValue(id, out var s) && s.Completed;
    public int GetPendingItemProtoId(EntityId id) => _states.TryGetValue(id, out var s) ? s.PendingItemProtoId : 0;

    // 排队转向的目标方向;-1 = 无排队。RotateEntity 在采矿机"挖完待排出"(IsCompleted)
    // 时不立即改 EntityData.Rotation(排出格还没落地),排到这里,等下次 flush 成功、
    // Completed 归 false 后(Simulation 的采矿 tick 循环)一次性应用。
    public int GetPendingRotation(EntityId id) => _states.TryGetValue(id, out var s) ? s.PendingRotation : -1;

    public void SetTarget(EntityId id, int x, int y)
    {
        _ = _states[id];   // 前置:已注册(throw-on-missing,同 P9 Machines 的一致性要求)
        var s = _states[id];
        _states[id] = s with { TargetX = x, TargetY = y, Progress = 0 };
    }

    public void AddProgress(EntityId id, long delta)
    {
        var s = _states[id];
        _states[id] = s with { Progress = s.Progress + delta };
    }

    // 到点产出:记下待放置的物品 id。TargetX/Y 不变(留给 Simulation 层在
    // flush 成功后调 ResetAfterFlush 决定要不要保留)。
    public void MarkCompleted(EntityId id, int pendingItemProtoId)
    {
        var s = _states[id];
        _states[id] = s with { Completed = true, PendingItemProtoId = pendingItemProtoId };
    }

    // flush 成功后调用:清 Completed/PendingItemProtoId/Progress。TargetX/Y
    // 由调用方决定——矿格挖空传 (-1,-1)(下 tick 重新搜),没挖空传原目标
    // (继续挖同一格)。
    public void ResetAfterFlush(EntityId id, int targetX, int targetY)
    {
        // 排队转向和"这轮挖完没"是两码事:flush 成功、Completed 归 false 的这一刻正是
        // Simulation 的采矿 tick 循环下 tick 消费排队转向的信号,所以这里必须把它带过去,
        // 不能被这次 reset 顺手清掉(用 new DrillRuntimeState(...) 5 参构造会让
        // PendingRotation 落回默认值 -1,等于消费窗口还没到就把排队值弄丢了)。
        int pending = _states[id].PendingRotation;   // 前置:已注册(throw-on-missing,同 SetTarget/AddProgress/MarkCompleted)
        _states[id] = new DrillRuntimeState(targetX, targetY, 0, false, 0, pending);
    }

    // 排队一个转向;后发覆盖先发。前置:已注册。
    public void SetPendingRotation(EntityId id, int rotation)
    {
        _ = _states[id];
        _states[id] = _states[id] with { PendingRotation = rotation };
    }

    // 消费排队转向(Simulation 的采矿 tick 循环在 !Completed 时调,应用完清空)。前置:已注册。
    public void ClearPendingRotation(EntityId id)
    {
        _ = _states[id];
        _states[id] = _states[id] with { PendingRotation = -1 };
    }

    // 按 EntityId.Index 排序后写:index/代数/目标坐标/进度/是否已完成/待放置物品 id/排队转向。
    public void WriteState(IStateWriter writer)
    {
        writer.Write(_order.Count);
        foreach (var id in _order.Ids)
        {
            var s = _states[id];
            writer.Write(id.Index);
            writer.Write(id.Generation);
            writer.Write(s.TargetX);
            writer.Write(s.TargetY);
            writer.Write(s.Progress);
            writer.Write(s.Completed ? (byte)1 : (byte)0);
            writer.Write(s.PendingItemProtoId);
            writer.Write(s.PendingRotation);
        }
    }
}

internal readonly record struct DrillRuntimeState(int TargetX, int TargetY, long Progress, bool Completed, int PendingItemProtoId, int PendingRotation = -1);
