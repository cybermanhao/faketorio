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

    public void RegisterDrill(EntityId id)
    {
        _states[id] = new DrillRuntimeState(-1, -1, 0, false, 0);
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
        => _states[id] = new DrillRuntimeState(targetX, targetY, 0, false, 0);

    // 按 EntityId.Index 排序后写:index/代数/目标坐标/进度/是否已完成/待放置物品 id。
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
        }
    }
}

internal readonly record struct DrillRuntimeState(int TargetX, int TargetY, long Progress, bool Completed, int PendingItemProtoId);
