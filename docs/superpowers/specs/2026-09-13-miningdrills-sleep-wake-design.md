# MiningDrills 休眠/唤醒设计

## 0. 背景

`Machines` 休眠/唤醒(commit 范围 `782f1ef..487c858`)跑通了事件驱动唤醒 + 60 tick
安全网这套机制,但实测发现收益在默认压测场景里几乎被噪声吃掉——`Machines` 只占
tick 成本 ~7%,真正的大头是 `Electric`(~46%)、`Inserters`(~23%)、
`MiningDrills`(~18%)。这次把机制推广到 `MiningDrills`,预期收益比 `Machines`
那次大得多。`Inserters` 留给下一轮单独 brainstorm(它有"背后是持续变化的传送带"
这个 `MiningDrills` 没有的难点)。

## 1. MiningDrills 的三种状态

对应 `Simulation.cs` 里 `MiningDrillTickPreSettle`(当前 `:802-867`)已有的分支:

| 情况 | 检测位置 | 处理方式 |
|---|---|---|
| A. 无目标(脚下矿脉挖空) | 第 2 步,`MiningDrills.GetTargetX(id) == -1` 且搜索失败 | **睡,永久**(见 §2) |
| B. 完成但产出堵在箱子/大箱里 | 第 1 步,`outLineId` 无效(非传送带)分支,`placed == false` | **睡,事件唤醒**(见 §3) |
| C. 完成但产出堵在传送带里 | 第 1 步,`outLineId` 有效分支,`placed == false` | **不睡**,维持现状每 tick 正常跑(brainstorm 时用户明确选择:没有干净的"腾出空间"事件可挂钩,这轮不做) |

## 2. 情况 A:无目标 = 永久睡眠

这个游戏里资源不会重新生成(`ResourceGrid` 是静态生成的纯函数层,唯一会减少
资源的操作是 `Resources.Extract`,没有任何路径会给一个格子"添加"资源)。所以
"无目标"一旦发生,在不发生外部事件的情况下**永远不会自己解除**——不需要任何
唤醒触发点,直接睡死即可。

**唯一的例外**:玩家用 `RotateEntity` 转动这台采矿机,可能让它的 2x2 footprint
覆盖到新的矿格(比如原来朝北挖空了,转向朝东可能踩到没挖过的矿)。`RotateEntity`
命令处理(`Simulation.cs:465` 起)针对采矿机的分支,在调用现有的
`MiningDrills.SetPendingRotation(...)` 之后,追加一行 `MiningDrills.MarkAwake(id)`。
唤醒后走一次正常 `MiningDrillTickPreSettle`:消费排队转向(`!IsCompleted` 时,
情况 A 下 `IsCompleted` 恒为 false,条件恒真)→ 用新朝向重新搜索目标 → 找到就
继续干活(保持醒着),没找到就在本轮 `MarkAsleep` 里自然睡回去。

不需要区分"这次睡眠是不是案例 A"——统一走 §4 的睡眠判定,判定逻辑本身就会
正确地对"搜索仍然失败"重新睡回去。

## 3. 情况 B:产出堵在箱子/大箱里 = 事件唤醒

不同于 `Machines`(机器知道"这是我自己的库存",直接用机器自身 `EntityId` 做
唤醒调用的 key),采矿机的产出目标是**别人的**库存(箱子/大箱),而且这个库存
理论上可能被多台采矿机共享(同一个箱子接两台采矿机的产出)。唤醒必须按"格子
坐标"而不是"生产者是谁"来路由。

### 新数据结构(`MiningDrills` 内部)

```csharp
private readonly Dictionary<(int X, int Y), List<EntityId>> _blockedOutputWaiters = new();
```

睡眠时(§4 的 `MarkAsleep` 调用点之一):把自己的产出坐标 `(outX, outY)` 追加进
`_blockedOutputWaiters[(outX, outY)]`(键不存在则新建列表)。

唤醒时:

```csharp
// 某个坐标腾出了库存空间——唤醒所有在这个坐标排队等待的采矿机,并把这个 key 从表里移除。
public void WakeWaitersAt(int x, int y)
{
    if (!_blockedOutputWaiters.TryGetValue((x, y), out var waiters)) return;
    foreach (var id in waiters) MarkAwake(id);
    _blockedOutputWaiters.Remove((x, y));
}
```

(`MarkAwake` 已经是"从 `_asleep` 移到 `_awake`,已注册/已醒着则空操作"的幂等
语义,不需要在这里去重。)

`UnregisterDrill` 需要同时清理:一台被卸载的采矿机如果还挂在某个坐标的等待
列表里,要从对应列表里移除(否则 `WakeWaitersAt` 会对着一个已经不存在状态的
`EntityId` 调 `MarkAwake`——`MarkAwake` 本身对未注册 id 是空操作,不会崩溃,
但列表会无限增长,是个真实的内存泄漏,必须清理)。因为不知道卸载的这台机器
之前堵在哪个坐标(`DrillRuntimeState` 不存这个,睡眠是通过 `MarkAsleep`+坐标
副作用实现的,不是状态机字段),`UnregisterDrill` 需要遍历 `_blockedOutputWaiters`
的所有 value 列表删除该 id——这是 O(等待表总条目数),不是 O(scale²)(采矿机
卸载是低频操作,不在 per-tick 路径上,可接受)。

### 唤醒触发点(2 处,都是"从某个库存移除物品"的地方)

- `InserterTickPostSettle` 阶段 A 抓取的**非传送带**分支(`Simulation.cs:935`
  起的方法内,`pickLineId.IsValid` 为 false 那一支):`inv.Remove(itemId, 1)`
  成功后,调 `MiningDrills.WakeWaitersAt(pickX, pickY)`。
- `TransferFromEntity` 命令处理(`Simulation.cs:413` 起):`sourceInv.Remove(...)`
  成功后,调 `MiningDrills.WakeWaitersAt(command.X, command.Y)`。

**已知不覆盖的边界情况**:如果 `(outX, outY)` 这个坐标当前"什么都没有"(既非
传送带也非实体——`outInvId.IsValid` 为 false),现有代码同样落进 `placed==false`
分支,同样会睡+注册等待。如果之后有人在这个坐标新建一个箱子(`PlaceEntity`),
不会触发 `WakeWaitersAt`——这个缺口这轮不处理,靠 §5 的安全网兜底(最多 60 tick
后自愈)。

## 4. 睡眠判定点

`MiningDrillTickPreSettle` 的两处早退分支各自在早退前加 `MiningDrills.MarkAsleep(id)`:

- 第 1 步 `else`(非传送带分支)`placed == false`:除了现有的
  `ElectricGrid.RegisterDemand(...)` + `return`,插入进 `_blockedOutputWaiters[(outX, outY)]`
  + `MiningDrills.MarkAsleep(id)`。
- 第 2 步之后(新增早退,现状代码这里没有早退,直接落到第 3 步):
  ```csharp
  if (MiningDrills.GetTargetX(id) == -1)
  {
      ElectricGrid.RegisterDemand(id, x, y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick);
      MiningDrills.MarkAsleep(id);
      return;
  }
  ```

情况 C(堵在传送带)**不**加 `MarkAsleep`——`outLineId.IsValid` 为 true 那一支
`placed == false` 时维持现状(`RegisterDemand` + `return`,不早退去 sleep)。

## 5. 每 tick 的算法(同 Machines 模式)

`Simulation` 新增一个私有字段(同 `Machines` 修复 Finding 1 时加的
`_safetyNetWokenThisTick` 一个模式,采矿机这边用独立的字段名避免和 `Machines`
的共享冲突):

```csharp
private readonly HashSet<EntityId> _safetyNetWokenDrillsThisTick = new();
```

`MiningDrillsTickPreSettle()` 改成:

```csharp
private void MiningDrillsTickPreSettle()
{
    foreach (var id in MiningDrills.AsleepSnapshot())
    {
        ref var data = ref Entities.Get(id);
        var proto = (MiningDrillPrototype)Prototypes.GetById(data.ProtoId);
        ElectricGrid.RegisterDemand(id, data.X, data.Y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick);
        if (id.Index % MiningDrills.SafetyNetIntervalTicks == Tick % MiningDrills.SafetyNetIntervalTicks)
        {
            MiningDrills.MarkAwake(id);
            _safetyNetWokenDrillsThisTick.Add(id);   // Machines final-review Finding 1 同款 bug 预防
        }
    }

    foreach (var id in MiningDrills.AwakeSnapshot())
    {
        if (_safetyNetWokenDrillsThisTick.Contains(id)) continue;   // 本 tick 已经登记过需求,跳过重复登记
        ref var data = ref Entities.Get(id);
        // 排队转向消费逻辑不变(见 §2)
        if (!MiningDrills.IsCompleted(id))
        {
            int pendingRot = MiningDrills.GetPendingRotation(id);
            if (pendingRot >= 0) { data.Rotation = (byte)pendingRot; MiningDrills.ClearPendingRotation(id); }
        }
        var proto = (MiningDrillPrototype)Prototypes.GetById(data.ProtoId);
        MiningDrillTickPreSettle(id, proto, data.X, data.Y, data.Rotation);
    }
}
```

**`_safetyNetWokenDrillsThisTick` 直接照抄 `Machines` 最终审查抓出来的 Finding 1
修复模式**:安全网当 tick 唤醒的采矿机,这一 tick 的待机需求已经在 asleep 循环
里登记过一次,awake 循环必须跳过,否则会对同一实体同一 tick 重复
`RegisterDemand`,重演 `Machines` 那个真 bug。这次直接把修复后的模式抄过来,
不需要重新发现。

`MiningDrillsTickPostSettle()` 同理,只遍历 `AwakeSnapshot()` 且跳过
`_safetyNetWokenDrillsThisTick`。

## 6. 遍历安全性 + 确定性

同 `Machines`:`AwakeSnapshot()`/`AsleepSnapshot()` 是 `ToArray()` 快照拷贝,不能
在循环体内直接 `foreach` 活列表(`MarkAwake`/`MarkAsleep` 会修改它)。
`_awake`/`_asleep`/`_blockedOutputWaiters` 都不序列化,`MiningDrills.WriteState`
保持现状不变;反序列化/构建场景后所有已注册采矿机一律先进 `_awake`,自然重新
判定该不该睡(一次性 O(全部采矿机) 开销,不在 per-tick 路径上)。

行为时序允许改变(同 `Machines` 先例,已有共识):睡眠机制会让"矿挖完排出"这类
事件的具体发生 tick 移动,`golden` hash 预期会变,需要按既有流程重新生成
(`--update-golden`)并重新校准 `baselineNsPerTick`。

## 7. 待机能耗

跟 `Machines` 保持一致的决定(选项 2):睡着的采矿机仍然每 tick 登记
`RegisterDemand`,睡眠只省 CPU,不省电。

## 8. 测试计划

- `MiningDrillsTests`(单元级,类似 `MachinesTests` 的模式)覆盖:
  - `RegisterDrill` 新机器起始状态是 `_awake`。
  - `MarkAwake`/`MarkAsleep` 幂等、对未注册 id 空操作。
  - `WakeWaitersAt` 唤醒指定坐标的全部等待者并清空该 key;对没有等待者的坐标空操作。
  - `UnregisterDrill` 正确从 `_blockedOutputWaiters` 的等待列表里移除(不留残留引用)。
- `SimulationTests.cs` 集成测试:
  - 采矿机挖空脚下矿脉后进入 `_asleep`,且没有任何后续事件能自发唤醒它(断言长时间运行后仍是 `IsCompleted==false` 且未进入 `_awake`,不同于安全网会反复"闪"醒一下又睡回去这件事——断言目标是"进度没有任何变化",不是"从来没被 `IsAwake` 检测到过 true")。
  - `RotateEntity` 转向后露出新矿格,断言同 tick/近同 tick 内 `MarkAwake` 生效并重新开始挖矿(同 `Machines` 的 `SetRecipe_WakesUpSleepingAssembler_SameTick` 精确断言风格,不用宽松的 eventual-consistency 循环——这是 `Machines` 最终审查 Finding 2 教训的直接应用)。
  - 采矿机产出堵在箱子里 → 睡;机械臂/`TransferFromEntity` 腾出空间 → 精确断言在腾出的那一 tick(或紧接着那一 tick)唤醒生效,不用宽松循环(同上,Finding 2 教训)。
  - 产出堵在传送带里的采矿机在长时间运行后仍然 `IsAwake == true`(确认情况 C 确实不睡)。
  - 安全网自愈测试:模拟"漏唤醒"场景(直接操纵目标库存腾出空间,不调 `WakeWaitersAt`),断言最多 `SafetyNetIntervalTicks` 个 tick 内自己恢复。
  - 待机能耗:断言睡着的采矿机仍然每 tick 登记 `RegisterDemand`。
- 全量回归 + `Faketorio.Sim.Bench` 在 scale 100/1000/1500 做一遍分阶段 profiler,重点看 `MiningDrills` 阶段耗时变化(预期比 `Machines` 那次的 ~1.5% 总体提升明显得多,因为 `MiningDrills` 占 tick 成本 ~18% 而不是 ~7%,且默认压测场景里相当比例的采矿机因为 fed-drill 覆盖率只有 ~13% 而长期处于"无目标"或"矿脉快挖空"状态,案例 A 的覆盖面天然就大)。

## 9. 明确不做(本次范围外)

- `Inserters` 的休眠/唤醒——留给后续单独 brainstorm。
- 情况 C(产出堵在传送带里)的休眠——本轮明确不做,brainstorm 时用户已确认。
- "产出目标坐标从空地变成新建箱子"这个边界情况的快速唤醒——靠安全网兜底,不单独处理(见 §3)。
- 安全网周期可配置化——同 `Machines`,写死 60,不做运行时参数。
