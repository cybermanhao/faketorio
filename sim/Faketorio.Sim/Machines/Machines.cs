using Faketorio.Sim.Entities;
using Faketorio.Sim.State;

namespace Faketorio.Sim;   // 扁平——不是 Faketorio.Sim.Machines,理由见 Global Constraints

// 加工状态机的运行时状态容器。刻意不知道 Simulation/Prototypes/Inventories——
// 只收裸 EntityId/int/long,配方匹配、库存读写、电网查询都在 Simulation.MachinesTick
// 里做(同 ElectricGrid 的隔离原则)。
public sealed class Machines
{
    private readonly Dictionary<EntityId, MachineRuntimeState> _states = new();
    private readonly OrderedEntityIdList _order = new();
    private readonly OrderedEntityIdList _awake = new();
    private readonly OrderedEntityIdList _asleep = new();

    public IReadOnlyList<EntityId> ActiveIds => _order.Ids;
    internal List<EntityId> ActiveIdsList => _order.IdsList;
    internal int StateCount => _states.Count;

    // 60 tick(1 秒游戏时间)的兜底安全网周期——见设计 spec §4/§9。
    public const int SafetyNetIntervalTicks = 60;

    public void RegisterMachine(EntityId id)
    {
        _states[id] = new MachineRuntimeState(-1, 0, false);
        _order.Add(id);
        _awake.Add(id);   // 新机器一律先醒着,走一次正常 tick 自己判断该不该睡。
    }

    public void UnregisterMachine(EntityId id)
    {
        _states.Remove(id);
        _order.Remove(id);
        _awake.Remove(id);
        _asleep.Remove(id);
    }

    public void MarkAwake(EntityId id)
    {
        if (!_states.ContainsKey(id)) return;   // 未注册(比如目标不是机器):空操作
        _asleep.Remove(id);
        _awake.Add(id);   // Add 本身对已存在的 id 是空操作(OrderedEntityIdList.Add)
    }

    public void MarkAsleep(EntityId id)
    {
        if (!_states.ContainsKey(id)) return;
        _awake.Remove(id);
        _asleep.Add(id);
    }

    internal EntityId[] AwakeSnapshot() => _awake.ToArray();
    internal EntityId[] AsleepSnapshot() => _asleep.ToArray();

    public bool IsAwake(EntityId id) => _states.ContainsKey(id) && !AsleepContains(id);

    private bool AsleepContains(EntityId id)
    {
        foreach (var x in _asleep.Ids) if (x == id) return true;
        return false;
    }

    public int GetCurrentRecipe(EntityId id) => _states.TryGetValue(id, out var s) ? s.CurrentRecipeProtoId : -1;
    public long GetProgress(EntityId id) => _states.TryGetValue(id, out var s) ? s.Progress : 0;
    public bool IsCompleted(EntityId id) => _states.TryGetValue(id, out var s) && s.Completed;

    public void SetRecipe(EntityId id, int recipeProtoId)
    {
        _ = _states[id];
        _states[id] = new MachineRuntimeState(recipeProtoId, 0, false);
    }

    public void AddProgress(EntityId id, long delta)
    {
        var s = _states[id];
        _states[id] = s with { Progress = s.Progress + delta };
    }

    public void MarkCompleted(EntityId id)
    {
        var s = _states[id];
        _states[id] = s with { Completed = true };
    }

    // clearRecipe: 熔炉传 true(配方每轮从输入内容现推,轮次间不粘滞);装配机传 false
    // (配方是玩家 SetRecipe 配置的持久选择,一直循环同一配方,直到玩家再发一次
    // SetRecipe——同真实 Factorio 装配机行为)。
    public void RestartCycle(EntityId id, bool clearRecipe)
    {
        _ = _states[id];
        int recipe = clearRecipe ? -1 : GetCurrentRecipe(id);
        _states[id] = new MachineRuntimeState(recipe, 0, false);
    }

    // 按 EntityId.Index 排序后写:index/代数/配方 id/进度/是否已完成。
    public void WriteState(IStateWriter writer)
    {
        writer.Write(_order.Count);
        foreach (var id in _order.Ids)
        {
            var s = _states[id];
            writer.Write(id.Index);
            writer.Write(id.Generation);
            writer.Write(s.CurrentRecipeProtoId);
            writer.Write(s.Progress);
            writer.Write(s.Completed ? (byte)1 : (byte)0);
        }
    }
}

internal readonly record struct MachineRuntimeState(int CurrentRecipeProtoId, long Progress, bool Completed);
