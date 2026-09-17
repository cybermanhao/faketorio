# 建造成本机制设计

**日期**：2026-09-17
**状态**：待用户审阅
**范围**：sim 层新命令 `BuildFromInventory` + 7 种建筑的物品/配方/可拆数据 + `game/BuildController.cs` 接入

配套参考：[`docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`](2026-09-02-m1-remaining-roadmap.md)（"表现层后续"候选之一：完整命令 UI，本专项是它的前置——先把"放置消耗库存"这个核心经济机制在 sim 层落地，UI（快捷栏）作为后续子项目在这之上接选择界面）

---

## 0. 背景与动机

当前 `PlaceEntity` 命令完全免费——不检查、不消耗玩家库存，只检查占地够不够。这跟这个游戏"自动化制造需要的东西"的核心循环矛盾：建筑本身应该是要花代价造出来的东西，不是凭空变出来的。

用户明确要求：放置建筑必须消耗库存物品。这不是纯表现层能完成的——库存检查/扣除必须落在 sim 层（确定性核心），否则游戏画面看到的库存和 sim 记录的状态会对不上。

查证现状发现两个对这次改动有直接影响的既有事实：

1. **`ItemPrototype.PlaceResult` 字段早就存在，但从没被读过**（`sim/Faketorio.Sim/Prototypes/ItemPrototype.cs:6`）。`data/base/items.json` 里 `wooden-chest` 物品条目已经写了 `"placeResult": "wooden-chest"`，数据模型本来就预留了"物品 → 可放置实体"这层映射。
2. **手挖机制（`MineStart`/`MineStop`，E 键）已经完整支持"长按 + 交互距离 + 返还物品"**（`Simulation.cs:260-300` 的 `PlayerMine()`）——对任何设了 `MinableResult` 的实体都适用，不挑目标是矿脉还是已放置的建筑。`wooden-chest` 现在其实已经能被 E 键长按拆掉并拿回物品，只是没有别的实体有这个字段。这意味着"拆除返还建筑成本物品"**不需要新设计机制**，只需要给每种建筑的 `EntityPrototype` 补上 `MinableResult`，并把 `game/BuildController.cs` 的右键拆除从瞬间生效的 `RemoveEntity` 改接到这套已有的手挖逻辑上。

---

## 1. 不在本次范围

- 快捷栏/背包/机器面板等 UI（下一个子项目，依赖本专项落地的 `BuildFromInventory` 命令）
- 配方树的多级深化（比如需要电路板之类的中间产物）——这轮只加"消耗基础物品直接造建筑"这一层
- `PlaceEntity` 命令本身的任何行为改动——它保持现状完全免费，专供 `ScenarioBuilder`（bench 性能测试）/`SubmitStartupScene`（默认演示场景）用，不受本次改动影响
- 物品丢弃、物品在地上掉落之类的机制

---

## 2. 数据层改动

### 2.1 新增物品原型（`data/base/items.json`）

7 个目前没有对应物品的可放置实体，各加一条 `ItemPrototype`（`wooden-chest` 已有，只需补 `minableResult`，见 2.3）：

| 物品名 | stackSize | placeResult |
|---|---|---|
| electric-mining-drill | 50 | electric-mining-drill |
| stone-furnace | 50 | stone-furnace |
| assembling-machine-1 | 50 | assembling-machine-1 |
| transport-belt-basic | 50 | transport-belt-basic |
| inserter-basic | 50 | inserter-basic |
| small-electric-pole | 50 | small-electric-pole |
| burner-generator | 50 | burner-generator |

### 2.2 新增配方（`data/base/recipes.json`，HandCraft 用）

数值是第一版草稿，追求"能自举、不过分繁琐"，具体配方你审的时候可以直接改：

| 产物 | 配方 | 说明 |
|---|---|---|
| transport-belt-basic | 1 iron-plate + 1 iron-gear-wheel | 便宜——玩家要摆很多条 |
| inserter-basic | 1 iron-plate + 1 iron-gear-wheel | 同上 |
| small-electric-pole | 1 iron-plate | 最便宜的建筑 |
| stone-furnace | 5 stone | 用现有但至今没有配方消耗过的 `stone` 物品 |
| electric-mining-drill | 5 iron-plate + 5 iron-gear-wheel | 中高成本，稀缺产出 |
| assembling-machine-1 | 5 iron-plate + 3 iron-gear-wheel | 中高成本 |
| burner-generator | 5 iron-plate + 2 iron-gear-wheel | 中高成本 |

（`iron-gear-wheel` 配方已存在：2 iron-plate → 1 齿轮；`iron-plate` 配方已存在：1 iron-ore → 1 铁板，`category: smelting`。**已核实**`Simulation.cs` 的 `CraftEnqueue` 处理逻辑硬性要求 `recipe.Category == "crafting"`（`smelting` 类直接拒绝）——iron-plate 只能靠已放置的熔炉产，不能手搓，纯手搓链只能到齿轮/建筑这一层，起步铁板走 §2.4 的开局物资包 + 熔炉。）

### 2.3 新增/补充 `minableResult`（`data/base/*.json`，各实体原型文件）

7 个新增实体 + wooden-chest 补齐（large-chest 保留现状不变——不在这轮新增建筑范围内，需要的话可以单独加）：

| 实体 | minableResult | miningTimeSeconds（草稿） |
|---|---|---|
| electric-mining-drill | electric-mining-drill | 2.0 |
| stone-furnace | stone-furnace | 1.5 |
| assembling-machine-1 | assembling-machine-1 | 2.0 |
| transport-belt-basic | transport-belt-basic | 0.3 |
| inserter-basic | inserter-basic | 0.3 |
| small-electric-pole | small-electric-pole | 0.3 |
| burner-generator | burner-generator | 1.5 |

### 2.4 开局物资包扩充（`data/base/player.json` 的 `startingInventory`）

解自举死锁：熔炉/采矿机都要电，电要发电机+电线杆+煤。直接给一套能马上跑起来的最小闭环，而不是设计一条纯手搓链：

| 物品 | 数量 | 用途 |
|---|---|---|
| electric-mining-drill | 1 | 起步矿 |
| stone-furnace | 1 | 起步炼 |
| burner-generator | 1 | 供电 |
| small-electric-pole | 2 | 连电网 |
| coal | 20 | 发电机燃料（现有 `startingInventory` 结构已支持 amount 字段） |
| iron-plate | 8 | 保留现有值，手搓起步用 |
| wooden-chest | 1 | 保留现有值 |

玩家开局手动摆好这 5 个建筑（矿→炉的产出链 + 发电机→杆 供电）即可开始自动化；后续要扩建的新建筑走 `BuildFromInventory` 消耗手搓/自产的物品。

---

## 3. sim 层：新命令 `BuildFromInventory`

### 3.1 `CommandType` 新增值

```csharp
public enum CommandType : byte
{
    PlaceEntity = 1,
    RemoveEntity = 2,
    MovePlayer = 3,
    StopPlayer = 4,
    MineStart = 5,
    MineStop = 6,
    CraftEnqueue = 7,
    TransferToEntity = 8,
    TransferFromEntity = 9,
    SetRecipe = 10,
    RotateEntity = 11,
    BuildFromInventory = 12,   // 新增
}
```

`Command` 复用现有字段：`ProtoId` = 物品原型 id（不是实体 id）、`X`/`Y` = 目标格、`Rotation` = 建筑朝向。

### 3.2 处理逻辑（`Simulation.Apply` 新分支）

```
1. 查 command.ProtoId 是不是合法 ItemPrototype，且 PlaceResult 不为空、
   对应的实体原型存在且是 EntityPrototype —— 任一不满足直接 RejectedCommandCount++，return。
2. 距离检查：Isqrt((Player.X - 目标格中心)²) > ReachSubTiles → 拒绝
   （公式跟 TransferToEntity/PlayerMine 一致，抄同一段代码）。
3. 占地检查：World.IsAreaFree(x, y, w, h) → 不满足则拒绝（跟 PlaceEntity 现有检查一致）。
4. 库存检查：Player.Inventory.CountOf(itemProtoId) > 0 → 不满足则拒绝。
5. 全部通过:
   a. Player.Inventory.Remove(itemProtoId, 1)
   b. 调用抽出来的共享 helper 创建实体(同 PlaceEntity 现有的"创建 EntityData + 按原型类型分派注册"
      那段逻辑——belt/container/pole/generator/machine/drill/inserter 各自的 Register 调用)
```

### 3.3 代码复用

把 `PlaceEntity` 分支里"实体已经通过检查、开始创建并注册"那段代码（`Simulation.cs:373-401` 附近，`Entities.Create` 到最后一个 `if (proto is InserterPrototype) ...RegisterInserter(id);`）抽成一个私有方法，如：

```csharp
private EntityId CreateAndRegisterEntity(EntityPrototype proto, int protoId, int x, int y, byte rotation)
```

`PlaceEntity` 和 `BuildFromInventory` 的检查通过后都调用它，不重复实体创建/注册代码。

### 3.4 `PlaceEntity` 保持不变

不加任何检查，不消耗任何库存，`ScenarioBuilder`/`SubmitStartupScene` 继续用它免费摆场景。两个命令类型语义清晰分离：`PlaceEntity` = 世界搭建原语（面向场景/测试代码），`BuildFromInventory` = 真实玩家建造动作（面向游戏交互）。

---

## 4. 表现层：`game/BuildController.cs`

### 4.1 左键放置

本专项先把命令接上（`BuildFromInventory` 替换硬编码 `PlaceEntity` + wooden-chest），选择"放哪种建筑"的 UI（快捷栏）留给下一个子项目——这一轮暂时保留左键固定放 wooden-chest 的行为，但改走新命令（验证命令本身工作正常），下一轮子项目换成真正的选择界面。

### 4.2 右键拆除

从：
```csharp
_host.Submit(new Command { Type = CommandType.RemoveEntity, X = x, Y = y });
```
改成：按下右键 → `Sim.Submit(new Command { Type = CommandType.MineStart, X = x, Y = y })`；松开右键 → `Sim.Submit(new Command { Type = CommandType.MineStop })`。跟现有 E 键手挖（`PlayerInputController.cs` 里应该已经有同样的 MineStart/MineStop 提交逻辑，可以直接照抄那段的模式）复用同一套 sim 端处理，零新 sim 代码。

`CommandType.RemoveEntity` 保留在 sim 层（仍有 xUnit 测试覆盖），只是游戏内不再有任何路径触发它。

---

## 5. 测试计划

**sim 层（xUnit，TDD）**：
- `BuildFromInventory_Succeeds_ConsumesItemAndCreatesEntity`
- `BuildFromInventory_OutOfReach_Rejected`
- `BuildFromInventory_NoMatchingItem_Rejected`（物品没有 `PlaceResult`，或者 `ProtoId` 根本不是合法物品）
- `BuildFromInventory_InsufficientInventory_Rejected`
- `BuildFromInventory_AreaNotFree_Rejected`
- `BuildFromInventory_MultipleEntityTypes_RegistersCorrectly`（belt/container/pole/generator/machine/drill/inserter 各跑一遍,确认抽出来的共享 helper 对每种类型都注册对了——回归测试,防止重构抽错)
- `PlaceEntity_StillFree_NoInventoryCheck`（回归测试，确认 `PlaceEntity` 行为完全不受影响）
- 手挖新增建筑的回归测试（复用现有 `PlayerMine`/`MineStart` 测试模式，确认新 `MinableResult` 数据生效）

**golden hash**：`BuildFromInventory` 是新命令类型，只要 `bench/golden.json` 用的默认场景（`ScenarioBuilder`，走 `PlaceEntity`）不使用这个新命令，golden 不受影响；改完后仍需 `dotnet test`/bench 全跑一遍确认。

**game 层**：人工 F5 验收——左键消耗库存放置成功、库存不足时左键被拒绝（`LastCommandRejected` 红色闪烁应该照常触发）、右键长按能拆并返还物品、松手/走出交互距离能正确中断。

---

## 6. 开放问题（写进 spec 但留给实现阶段核实）

- `PlayerInputController.cs` 现有 E 键 `MineStart`/`MineStop` 提交逻辑的具体代码形态（本 spec §4.2 假设可以直接照抄，写 plan 时应先读一遍实际代码确认）。
