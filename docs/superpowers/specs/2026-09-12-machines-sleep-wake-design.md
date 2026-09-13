# Machines 休眠/唤醒设计

## 0. 背景

`ElectricGrid.FindNetworkAt` 的 O(scale²) 根因修复(commit `67b5670`)之后,
UPS-vs-scale 实测确认 60 UPS 门槛从 scale≈90 顶到了 scale≈1531(约 8 万实体),
但增长曲线仍是超线性(k≈1.26,不是纯线性)——原因是 `Inserters`/`MiningDrills`/
`Machines` 三个 tick 阶段仍然对**全部注册实体**做每 tick 全量扫描,不管它们是否
"有事可做"。roadmap §5.3 把这个列为下一项性能候选,并标注"唤醒条件是确定性雷区"。

本设计只覆盖 **Machines(熔炉 + 装配机)** 一个系统,作为休眠/唤醒机制的第一次
落地,验证机制和测试方法之后再决定是否推广到 `MiningDrills`/`Inserters`(它们
的唤醒语义更复杂,尤其是机械臂"背后是传送带"这种连续变化源,留待后续单独
brainstorm)。

## 1. Machines 的四种闲置状态

对应 `Simulation.cs` 里 `MachineTickPreSettle`/`MachineTickPostSettle` 现有的
四个早退分支:

| 情况 | 检测位置 | 现有早退条件 |
|---|---|---|
| A. 装配机没配方 | PreSettle 第 2 步之后 | `Machines.GetCurrentRecipe(id) == -1`(目前**没有**早退,直接落到第 3 步无条件登记需求——这是本设计要新增的一个早退分支) |
| B. 有配方但原料不够 | PostSettle | `!satisfied` |
| C. 已完成但输出库存塞不下 | PreSettle | `!fits` |
| D. 熔炉配不出配方(扫完仍是 -1) | 同 A,熔炉专属 | 同 A |

四种情况的共同点:都在等下列三类外部事件之一发生:
1. 玩家 `SetRecipe` 命令
2. 机器输入库存(`role 1`)被塞入物品
3. 机器输出库存(`role 2`)被取走物品(仅 C 需要)

这三类事件的触发代码点是已知的、屈指可数的:`Simulation.Apply` 的 `SetRecipe`
分支、`TransferToEntity` 分支、`InserterTickPostSettle` 的放件/取件分支。

## 2. 决策:待机能耗保持不变(选项 2)

现状:四种闲置情况机器都在**无条件**登记待机电力需求(`RegisterDemand`),
包括情况 C 有专门注释说明这是刻意的。**本设计不改变这个行为**——睡眠只省
CPU,不省电。

代价:睡着的机器不能被完全踢出 tick 循环——待机需求登记要求"每 tick 摸一遍
所有睡着的机器"。所以循环次数仍是 O(全部注册机器),优化点在于"每次循环里
干的活变轻":睡着的机器只做一次 `RegisterDemand`(现在是 O(1),受益于
`ElectricGrid` 的空间索引优化)+ 一次安全网取模判断,跳过 flush 检查、
`CanInsert` 循环、熔炉配方匹配扫描这些更贵的逻辑。预期有实质性提速,但达不到
"睡着实体零成本"的效果。

## 3. 数据结构

`Machines` 内部维护两个 `OrderedEntityIdList`(复用现有模式,按 `EntityId.Index`
升序):
- `_awake` —— 本 tick 要跑完整 `MachineTickPreSettle`/`PostSettle` 的机器。
- `_asleep` —— 每 tick 只做"待机需求登记 + 安全网取模判断"的机器。

不变量:每台已注册机器永远恰好在两者之一。

新增方法:
```csharp
public void MarkAwake(EntityId id);   // _asleep -> _awake(已在 _awake 则空操作)
public void MarkAsleep(EntityId id);  // _awake -> _asleep(已在 _asleep 则空操作)
internal EntityId[] AwakeSnapshot();  // _awake 的一份拷贝,供 tick 循环安全遍历
internal EntityId[] AsleepSnapshot(); // _asleep 的一份拷贝,同上
```

`RegisterMachine`(现有的注册方法)新机器一律先放进 `_awake`——让它走一次正常
tick 自然判断该不该睡,不用单独写"注册时预判"逻辑。

`ActiveIds`/`ActiveIdsList`(现有的"全部已注册"访问器)保持不变语义,继续给
`WriteState` 等需要"遍历全部机器"的场景用,不受 `_awake`/`_asleep` 划分影响。

## 4. 每 tick 的算法

`MachinesTickPreSettle()` 改成:
```csharp
private void MachinesTickPreSettle()
{
    // 睡着的机器:轻量待机登记 + 安全网。快照后遍历,MarkAwake 可能在循环内
    // 修改 _asleep/_awake,不能直接 foreach 活列表。
    foreach (var id in Machines.AsleepSnapshot())
    {
        ref var data = ref Entities.Get(id);
        var proto = (CraftingMachinePrototype)Prototypes.GetById(data.ProtoId);
        ElectricGrid.RegisterDemand(id, data.X, data.Y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick);
        if (id.Index % Machines.SafetyNetIntervalTicks == Tick % Machines.SafetyNetIntervalTicks)
            Machines.MarkAwake(id);
    }

    // 醒着的机器:完整逻辑,原地判断要不要睡。
    foreach (var id in Machines.AwakeSnapshot())
    {
        ref var data = ref Entities.Get(id);
        var proto = (CraftingMachinePrototype)Prototypes.GetById(data.ProtoId);
        MachineTickPreSettle(id, proto, data.X, data.Y);
    }
}
```
`MachinesTickPostSettle()` 同理,只遍历 `AwakeSnapshot()`(睡着的机器在
PostSettle 阶段不需要任何操作——它们的待机需求已经在 PreSettle 登记过,
PostSettle 只推进进度,而睡着的机器按定义没有进度可推进)。

`MachineTickPreSettle`/`MachineTickPostSettle` 内部,在四个既有早退分支各自
早退前加一行 `Machines.MarkAsleep(id)`:
- 情况 A/D:PreSettle 第 2 步之后新增 `if (Machines.GetCurrentRecipe(id) == -1) { Machines.MarkAsleep(id); return; }`(这一步同时也是本设计里**新增的早退**——现状代码这里不早退,见第 1 节表格)。
- 情况 B:PostSettle 的 `if (!satisfied) { Machines.MarkAsleep(id); return; }`。
- 情况 C:PreSettle 的 `!fits` 分支,在 `return` 前加 `Machines.MarkAsleep(id)`(这一支已经在早退前调用 `RegisterDemand`,不受影响)。

`SafetyNetIntervalTicks` 定为 `60`(1 秒游戏时间的默认兜底周期),先写死常量,
不做成运行时可配置——性能验证后如果需要调,再改。

## 5. 唤醒触发点(5 处生产代码路径 + 1 条测试约定)

- `Simulation.Apply` 的 `SetRecipe` 分支:设置完配方后 `Machines.MarkAwake(id)`。
- `Simulation.Apply` 的 `TransferToEntity` 分支:成功塞进机器输入库存(`role 1`)后 `Machines.MarkAwake(targetId)`。
- `Simulation.Apply` 的 `TransferFromEntity` 分支:成功从机器输出库存(`role 2`)取走物品后 `Machines.MarkAwake(sourceId)`(brainstorm 阶段最初漏掉这一条——玩家手动从机器输出拿东西也会腾出空间,跟机械臂取件是同一类事件,场景中 `Machine_OutputBlocked_HoldsCompletedUntilSpaceFrees` 这条既有测试就是靠这条路径腾空间)。
- `InserterTickPostSettle` 的放件分支:成功塞进机器输入库存后 `Machines.MarkAwake(dropEntity)`。
- `InserterTickPostSettle` 的阶段 A 抓取分支:成功从机器输出库存(`role 2`)取走物品后 `Machines.MarkAwake(pickEntity)`。

**测试约定(必须遵守,否则安全网之外无法自愈)**:现有测试里大量地方直接调用
`Inventory.Insert`/`Remove` 操纵机器的输入/输出库存(绕过上面 5 个命令/机械臂
路径,模拟"外部喂料/取料"),这类直接操纵**摸不到任何唤醒钩子**——
`Inventories`/`Inventory` 刻意不知道 `Machines` 的存在,不会,也不应该,反向
通知。任何这样写的测试,如果期望机器在**少于 60 tick**(安全网周期)内做出
反应,必须在直接操纵库存之后紧跟一行 `sim.Machines.MarkAwake(machineId)`;
如果测试本来就要跑 60 tick 以上,可以依赖安全网自愈,不用额外调用。已知至少
两条既有测试属于"少于 60 tick 就断言"的情况,需要在实施时修——见实施计划。

## 6. 确定性 / WriteState

`_awake`/`_asleep` 的划分**不序列化**——`Machines.WriteState` 保持现状(遍历
`ActiveIdsList`,写全部机器的配方/进度/完成标记,与休眠状态无关)。反序列化后
(或 `dotnet ... --update-golden` 重建场景后)一律把所有已注册机器重新放进
`_awake`,让它们走一次正常 tick 自我判断——这是一次性的 O(全部机器) 开销(不
在 per-tick 路径上),不需要额外序列化字节,也不会让 golden hash 因为"休眠簿记"
本身发生变化(状态本身——位置/库存/配方/进度——不受影响;`ComputeStateHash`
不包含休眠簿记)。

行为本身的确定性时序**允许改变**(已在 brainstorm 中和用户确认):机械臂投料
后机器最快下一 tick 才会被唤醒处理(机械臂在 PostSettle 阶段跑,晚于本 tick 的
`MachinesTickPreSettle`);玩家直接 `TransferToEntity`/`SetRecipe` 是命令阶段
(整个 tick 最早),同一 tick 内的 `MachinesTickPreSettle` 就能看到,不延迟。
所有既有的 golden hash 断言(`BenchScenarioTests` 等)预期要重新生成,这是
本次改动预期内的结果,不是回归。

## 7. 遍历时的实现细节(必须处理)

`MachinesTickPreSettle`/`PostSettle` 现在的循环体会调用可能修改 `_awake`/
`_asleep` 的 `MarkAwake`/`MarkAsleep`。C# 不允许在 `foreach` 遍历一个 `List<T>`
的同时修改它(会抛 `InvalidOperationException`)。解决办法是循环开始前拍一份
快照数组(`AwakeSnapshot()`/`AsleepSnapshot()`,内部就是 `_ids.ToArray()`),
对快照做 `foreach`,修改只作用于 `Machines` 内部的活列表,不影响本次遍历。
快照拷贝成本是 O(当次遍历的实体数),不是 O(scale),可接受。

外部唤醒调用点(机械臂 `InserterTickPostSettle`、命令处理里的 `SetRecipe`/
`TransferToEntity`)不在 `Machines` 自己的循环体内触发,不受这个约束影响,可以
直接调用 `MarkAwake`。

## 8. 测试计划

- `MachinesTests`(如果不存在则新建)覆盖:
  - 装配机放置后无配方 → 下一 tick 进入 `_asleep`;`SetRecipe` 后下一 tick 回到 `_awake` 并能正常推进配方。
  - 熔炉原料不足 → 进入 `_asleep`;塞入原料(`TransferToEntity`)后能在预期 tick 内恢复推进。
  - 输出堵塞的机器完成一轮后 → 进入 `_asleep`;机械臂取走输出物品后恢复推进。
  - 安全网:构造一个"漏调 `MarkAwake`"的场景(测试直接操纵机器进入 `_asleep` 但不触发正常唤醒事件,注入原料),断言机器最多在 `SafetyNetIntervalTicks` 个 tick 内自己恢复推进,不会永久卡死。
  - 待机能耗:断言睡着的机器仍然每 tick 登记 `RegisterDemand`(电网需求总量不因为机器睡眠而减少)。
- `sim/Faketorio.Sim.Tests` 里所有跑到 5000 tick 的既有确定性/production 相关测试(`BenchScenarioTests` 等)预期通过(哈希会变,`GOLDEN_TICK_800`/`bench/golden.json` 需要重新生成——按现有流程,先跑 `--update-golden` 再人工确认合理性)。
- 用 `Faketorio.Sim.Bench` 在 scale 100/500/1500 三个点跑一遍分阶段 profiler,确认 `Machines` 阶段耗时明显下降,且其它阶段(`Inserters`/`MiningDrills`/`Electric`)不受影响(本次不碰它们)。

## 9. 明确不做(本次范围外)

- `MiningDrills`/`Inserters` 的休眠/唤醒——留给后续单独 brainstorm。
- 安全网周期可配置化——先写死 60,不做运行时参数。
- 待机能耗归零(选项 1)——已按用户决策采用选项 2(保持现状耗电)。
