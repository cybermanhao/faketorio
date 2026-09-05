# M1 Plan 9 — 加工状态机(熔炉 + 装配机)设计

日期: 2026-09-05
状态: 已与用户确认的设计基线
前置依赖: P4(库存,`Inventory`/`Inventories`)、P7(电网,`ElectricGrid`,已合并 `main` `bc9c8ae`)、`RecipePrototype`(P1 已有)
配套参考: [`docs/superpowers/specs/2026-07-03-faketorio-design.md`](2026-07-03-faketorio-design.md)(§5.3 加工状态机流程、实体休眠横切项)、[`docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`](2026-09-02-m1-remaining-roadmap.md)(P9 条目)、旧项目 `C:\code\godot-playgroud\game\scripts\machines\PlacedMachine.gd`(流程验证过的参考实现,不搬代码只借流程)

## 1. 目标

给熔炉、装配机两类实体一套**共用的加工状态机**:配方匹配 → 电力检查(登记需求 + 读 satisfaction)→ 进度推进(按 satisfaction 等比降速)→ 完成时原子放置输出(全有或全无预检)→ 输出堵塞进 pending → 完成前重校验输入。熔炉按输入槽里的物品自动匹配配方(同 Factorio 熔炉);装配机需要显式 `SetRecipe` 命令指定配方(同 Factorio 装配机)。两者共享同一个处理循环,区别只在"怎么获得 `current_recipe`"这一步。

M1 只交付这套状态机本身 + 两个最小 prototype(一炉一配)+ 打通 P7 电网作为唯一能源(不给机器另开燃料槽——那是燃料发电机专属的模式,P9 不重复)。

**不做**:实体休眠/活跃列表优化(横切项,路线图注明等 P9 落地后再单独立项,P9 自己用全量扫描)、机械臂/传送带到机器的自动物流(P11)、多产物配方(M1 数据里所有配方仍是单一产物;`ResolvedResults` 允许多个但没有数据实际用到)、配方切换时的库存找零/部分退还(§6 已经设计成不需要)。

## 2. 组件与文件结构

**新文件**

| 文件 | 职责 |
|---|---|
| `sim/Faketorio.Sim/Prototypes/CraftingMachinePrototype.cs` | `abstract class CraftingMachinePrototype : EntityPrototype`(共用字段)+ `sealed class FurnacePrototype`/`sealed class AssemblingMachinePrototype`(均无额外字段,类型本身就是行为开关)。 |
| `sim/Faketorio.Sim/Machines/Machines.cs` | 加工状态机的运行时状态管理器:`RegisterMachine`/`UnregisterMachine`/`SetRecipe`/状态读写/`WriteState`。刻意不知道 `Simulation`/`Prototypes`/`Inventories`——只收裸 `EntityId`/`int`/`long`,同 `ElectricGrid` 的隔离原则。 |
| `data/base/machines.json` | 一个熔炉 + 一个装配机的 prototype。 |

**改动**

| 文件 | 改动 |
|---|---|
| `sim/Faketorio.Sim/Items/Inventories.cs` | `AddContainer`/`GetInventoryId` 加 `int role = 0` 参数(向后兼容,现有箱子/发电机燃料槽调用不传即 role 0,行为不变)。内部 `_byEntity`(稀疏数组,entity index → InventoryId)换成 `Dictionary<(int EntityIndex, int Role), InventoryId>`;`_ownerByIndex`(pool index → owner)从 `EntityId[]` 换成 `(EntityId Owner, int Role)[]`,`WriteState` 逐条多写一个 `Role` 字段(哈希格式变化,见 §8——项目没有存档兼容承诺,只需要同会话同种子同哈希,不影响任何已有测试)。 |
| `sim/Faketorio.Sim/Commands/Command.cs` | `CommandType` 加 `SetRecipe = 10`。复用现有 `ProtoId`(配方 id)、`X`/`Y`(目标格)字段,不加新字段。 |
| `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs` | `Parse` 加 `"furnace"`/`"assembling-machine"` 两个 arm(都走 `ValidateFootprint<T>`);新 sibling pass `ResolveAndValidateCraftingMachines`(校验 `Category` 非空、`InputSlots`/`OutputSlots`/`EnergyUsageJPerTick` 为正)。 |
| `sim/Faketorio.Sim/Simulation.cs` | `Machines` 属性;`Apply` 加 `SetRecipe` case;`PlaceEntity`/`DestroyEntityAt` 给熔炉/装配机接入/摘出(`Machines.RegisterMachine` + `Inventories.AddContainer` 两次,role 1/2);`Step` 插入加工 tick 段;`WriteState` 追加 `Machines.WriteState`。 |

`Machines` 不关心谁是熔炉谁是装配机——只按 `EntityId` 存 `(CurrentRecipeProtoId, Progress, Completed)`,谁调 `SetRecipe`、谁自动匹配都是 `Simulation.MachinesTick()` 的事,`Machines` 自己不做配方匹配(那需要 `Prototypes` + `Inventories`,越出它的隔离边界)。

## 3. 原型继承结构

```csharp
public abstract class CraftingMachinePrototype : EntityPrototype
{
    public required string Category { get; init; }      // 必须匹配某些 RecipePrototype.Category
    public int InputSlots { get; init; }
    public int OutputSlots { get; init; }
    public Q16 CraftingSpeed { get; init; } = Q16.One;   // 进度倍率,M1 数据恒 1.0
    public long EnergyUsageJPerTick { get; init; }        // 每 tick 向电网登记的 PrimaryInput 需求
}

public sealed class FurnacePrototype : CraftingMachinePrototype { }           // 自动匹配配方
public sealed class AssemblingMachinePrototype : CraftingMachinePrototype { } // 需要 SetRecipe
```

行为分支由类型本身决定(`proto is FurnacePrototype` / `is AssemblingMachinePrototype`),不引入 bool 标志字段——延续 P7 `ElectricPolePrototype`/`FuelGeneratorPrototype` 的先例。`PrototypeLoader.Parse` 按 JSON `"type"` 字段(`"furnace"`/`"assembling-machine"`)分派到对应具体类,两者字段解析代码相同(共享一个私有 helper 读 `CraftingMachinePrototype` 的公共字段)。

`ResolveAndValidateCraftingMachines`(`AssignIds()` 之后新增的第四个 sibling pass,不并入既有三个)校验:`Category` 非空字符串;`InputSlots >= 1`;`OutputSlots >= 1`;`EnergyUsageJPerTick >= 0`(允许 0,表示无待机能耗的假想机器,虽然 M1 数据不会这样配)。不校验 `Category` 一定有对应 `RecipePrototype` 存在——允许先加机器后加配方的数据组织顺序,运行时找不到可匹配配方只是"一直空转",不是加载期错误。

## 4. 库存角色化(`Inventories` 扩展)

现有 `Inventories` 是"一个 `EntityId` 对一个 `InventoryId`"的一对一映射。机器需要两个独立的库存(输入、输出),互不共享槽位、互不过滤对方的物品类型。改造:

```csharp
public InventoryId AddContainer(EntityId entity, int slotCount, bool readOnly = false, int filterItemProtoId = 0, int role = 0);
public InventoryId GetInventoryId(EntityId entity, int role = 0);
```

- `role` 是调用方自选的小整数标签,`Inventories` 内部不解释它的含义(0 = 默认/箱子/发电机燃料槽,1 = 机器输入,2 = 机器输出——这些数值只在 `Simulation` 层有意义)。
- 内部存储从 `InventoryId[] _byEntity`(按 `entity.Index` 下标的稀疏数组)换成 `Dictionary<(int EntityIndex, int Role), InventoryId>`——一个实体在不同 role 下各自独立一份 `InventoryId`。`_ownerByIndex`(`WriteState`/`RemoveContainer` 用的反向映射,pool index → 所属实体)从 `EntityId[]` 换成 `(EntityId Owner, int Role)[]`,继续按池索引对齐(池索引本身依然稠密、无需变动)。
- `RemoveContainer(EntityId entity)` 签名不变会有歧义(不知道摘哪个 role 的库存)——改成 `RemoveContainer(EntityId entity, int role = 0)`,现有箱子/发电机调用点不传 role,行为不变。
- `WriteState` 每条记录从 `(index, generation, owner.Index, owner.Generation, readOnly, filter, 槽内容)` 变成 `(index, generation, owner.Index, owner.Generation, role, readOnly, filter, 槽内容)`——多写一个 `int`。这是哈希格式变化,但项目没有跨版本存档兼容承诺(`DeterminismTests` 只比较同一次运行内两遍相同命令序列的哈希是否相等,不比较任何硬编码哈希常量),不影响任何现有测试断言。

**`TransferToEntity`/`TransferFromEntity` 的 role 选择**(`Simulation.Apply` 里,不改 `Command` 结构):

```csharp
int TargetRoleFor(CommandType cmdType, EntityPrototype proto) => proto switch
{
    CraftingMachinePrototype => cmdType == CommandType.TransferToEntity ? 1 : 2,
    _ => 0,
}
```

`TransferToEntity` 对着机器 = 塞进输入库存(role 1);`TransferFromEntity` 对着机器 = 从输出库存拿(role 2)。命令的方向本身就决定了角色,不需要新字段、不需要玩家显式指定"我要塞输入还是输出"。机器的输入/输出库存都是普通的 `Inventory`(`role` 只影响 `Inventories` 怎么找到它,`Inventory` 本身不知道自己是输入还是输出),槽位内自动按物品类型堆叠、找空槽——和箱子的行为完全一致,不需要新的存储类型或"每个配方原料一个专属槽位"的定制逻辑。

## 5. `Machines` 子系统:每机器运行时状态

```csharp
namespace Faketorio.Sim.Machines;

public sealed class Machines
{
    private readonly Dictionary<EntityId, MachineRuntimeState> _states = new();

    public void RegisterMachine(EntityId id) => _states[id] = new MachineRuntimeState(-1, 0, false);
    public void UnregisterMachine(EntityId id) => _states.Remove(id);

    public int GetCurrentRecipe(EntityId id) => _states.TryGetValue(id, out var s) ? s.CurrentRecipeProtoId : -1;
    public long GetProgress(EntityId id) => _states.TryGetValue(id, out var s) ? s.Progress : 0;
    public bool IsCompleted(EntityId id) => _states.TryGetValue(id, out var s) && s.Completed;

    public void SetRecipe(EntityId id, int recipeProtoId) => _states[id] = new MachineRuntimeState(recipeProtoId, 0, false);
    public void AddProgress(EntityId id, long delta) { /* Progress += delta,前置:已注册 */ }
    // clearRecipe: 熔炉传 true(配方是每轮从输入内容里现推的,轮次之间不粘滞);
    // 装配机传 false(配方是玩家 SetRecipe 配置的持久选择,一轮做完/中断都不丢,
    // 一直循环同一配方,直到玩家再发一次 SetRecipe——同真实 Factorio 装配机行为)。
    public void RestartCycle(EntityId id, bool clearRecipe)
    {
        int recipe = clearRecipe ? -1 : GetCurrentRecipe(id);
        _states[id] = new MachineRuntimeState(recipe, 0, false);
    }
    public void MarkCompleted(EntityId id) { /* Completed = true,CurrentRecipeProtoId/Progress 不变 */ }

    public void WriteState(Faketorio.Sim.State.IStateWriter writer)
    {
        var ids = new List<EntityId>(_states.Keys);
        ids.Sort((a, b) => a.Index.CompareTo(b.Index));
        writer.Write(ids.Count);
        foreach (var id in ids)
        {
            var s = _states[id];
            writer.Write(id.Index); writer.Write(id.Generation);
            writer.Write(s.CurrentRecipeProtoId); writer.Write(s.Progress); writer.Write(s.Completed ? (byte)1 : (byte)0);
        }
    }
}

internal readonly record struct MachineRuntimeState(int CurrentRecipeProtoId, long Progress, bool Completed);
```

`Progress` 是 Q16.16 定点的"tick 数"(不是原始 tick 计数)——见 §6 第 4 步,这样 `CraftingSpeed`(机器倍率)和 `satisfaction`(电力倍率)两个 `Q16` 因子可以连乘后直接累加,不需要额外的取整/累计误差处理。`SetRecipe` 可以在任意时刻调用(哪怕机器正在加工中)——因为原料在完成前不会被扣除(§6 第 5 步),切配方只是丢弃已经累积的 `Progress`,不造成任何物品损失。

**配方粘滞性**:`CurrentRecipeProtoId` 对熔炉和装配机的语义不同。熔炉的配方是"现在输入槽里这堆矿能配出哪条配方"的即时推导结果,每轮做完(或中途原料被拿走导致重校验失败)都清空,下一轮重新推导——矿种换了下一轮自然跟着换,不需要玩家干预。装配机的配方是玩家用 `SetRecipe` 配置的持久设置,一轮做完或中途失败都**不清空**,机器会一直循环同一配方,直到玩家再发一次 `SetRecipe` 换配方——这是"装配机需要显式设定配方"这个选择本身隐含的行为(同 Factorio:设一次,一直循环产出,不是"一次性"配方)。两条路径共用同一个 `RestartCycle(id, clearRecipe)` API,`clearRecipe` 由 §6 的 `MachineTickPreSettle`/`MachineTickPostSettle` 按 `proto is FurnacePrototype` 传入。

`Machines` 是纯状态容器,不做配方匹配、不碰库存、不查电网——这些都在 `Simulation.MachinesTick()` 里,因为那一步需要 `Prototypes`(配方数据)、`Inventories`(库存读写)、`ElectricGrid`(电力查询)三方协作,任何一方单独都不够。

## 6. `Simulation.MachinesTick()`:每 tick 处理循环

需求登记必须在 `ElectricGrid.Settle()` 之前、读 satisfaction 必须在 `Settle()` 之后(同发电机"先登记供给、`Settle()`、再烧油"的顺序要求),所以从一开始就是**两趟全量扫描**(同发电机/传送带现有的扫描模式,不做活跃列表优化),不是一个函数里顺序写完:

```csharp
private void MachinesTickPreSettle()
{
    for (int i = 0; i < Entities.Capacity; i++)
    {
        if (!Entities.IsAliveAtIndex(i)) continue;
        ref var data = ref Entities.GetAtIndex(i);
        if (Prototypes.GetById(data.ProtoId) is not CraftingMachinePrototype proto) continue;
        MachineTickPreSettle(entityIdAtIndex(i), proto, data.X, data.Y);
    }
}

private void MachinesTickPostSettle()
{
    for (int i = 0; i < Entities.Capacity; i++)
    {
        if (!Entities.IsAliveAtIndex(i)) continue;
        ref var data = ref Entities.GetAtIndex(i);
        if (Prototypes.GetById(data.ProtoId) is not CraftingMachinePrototype proto) continue;
        MachineTickPostSettle(entityIdAtIndex(i), proto);
    }
}
```

**`MachineTickPreSettle(id, proto, x, y)`**(对齐 §1 引用的旧项目流程,能源检查换成电网登记):

1. **flush 已完成的产出**(`Machines.IsCompleted(id)` 为真):把 `CurrentRecipeProtoId` 对应配方的 `ResolvedResults` 原子插入输出库存(role 2)——全有或全无预检(`CanInsert` 遍历一遍,任何一项放不下就整体不放,同 `PlayerCraft` 的预检模式)。成功 → `Machines.RestartCycle(id, clearRecipe: proto is FurnacePrototype)`,**本 tick 内继续走到第 2 步**(不用等下一 tick,同参考实现"扣完 pending 后重新进入步骤 1")。失败(输出库存满)→ 跳过第 2 步(不重新匹配/不能开始新一轮),但**仍然执行第 3 步**——阻塞状态的机器也照常产生待机能耗(用户明确要求:不论有无配方在跑,恒定登记待机能耗)。
2. **配方选定**(`Machines.GetCurrentRecipe(id) == -1` 时才做;第 1 步 flush 失败时跳过整个第 2 步):
   - 熔炉(`proto is FurnacePrototype`):遍历所有 `RecipePrototype`(按 proto id 升序,确定性),找第一个 `Category == proto.Category` 且输入库存(role 1)当前内容能满足其 `ResolvedIngredients`(`CountOf` 逐项检查)的配方,`Machines.SetRecipe(id, recipe.Id)`。找不到 → 保持 `-1`。
   - 装配机(`proto is AssemblingMachinePrototype`):不做任何自动匹配,只读 `Machines.GetCurrentRecipe(id)`(由玩家此前发过的 `SetRecipe` 命令设置,§5 配方粘滞性——完成一轮后这里读到的还是同一个配方,不会变回 `-1`)。仍是 `-1`(从没 `SetRecipe` 过)→ 保持空转。
3. **电力需求登记**(无条件执行,不受第 1/2 步结果影响——恒定待机能耗):`ElectricGrid.RegisterDemand(id, x, y, UsagePriority.PrimaryInput, proto.EnergyUsageJPerTick)`。

**`MachineTickPostSettle(id, proto)`**(`ElectricGrid.Settle()` 已经跑过,satisfaction 可读):

4. **进度推进**(仅当 `CurrentRecipeProtoId != -1` 且未 `Completed`——第 1 步 flush 失败、或本 tick 没匹配到配方,这里都是 no-op):
   ```csharp
   var recipe = (RecipePrototype)Prototypes.GetById(Machines.GetCurrentRecipe(id));
   long threshold = (long)recipe.EnergyRequiredTicks << 16;
   var satisfaction = ElectricGrid.GetSatisfaction(id);
   long delta = proto.CraftingSpeed.Mul(satisfaction.Raw);  // Q16.16 定点"tick 数"增量
   if (Machines.GetProgress(id) < threshold) Machines.AddProgress(id, delta);
   if (Machines.GetProgress(id) < threshold) return;   // 阻塞在阈值,不再累加(同 PlayerMine/PlayerCraft 的"停在阈值"约定)
   ```
5. **完成前重校验 + 消耗原料**(紧接第 4 步,同一 tick 内):再次检查输入库存(role 1)是否仍满足 `recipe.ResolvedIngredients`(防御:未来若有机械臂/传送带能从机器输入库存里拿走物品,这里能抓到race)。
   - 仍满足:逐项 `inputInv.Remove(ingredient.ItemProtoId, ingredient.Amount)`,`Machines.MarkCompleted(id)`(`CurrentRecipeProtoId` 不变,下一 tick 的第 1 步会尝试放output)。
   - 不满足:`Machines.RestartCycle(id, clearRecipe: proto is FurnacePrototype)`(原料本来就没扣,无需归还;熔炉清空配方下一 tick 重新推导,装配机保留配方下一 tick 直接从 `Progress = 0` 重新开始同一配方——只要原料后来又备齐了)。

`Step()` 里的位置:

```
应用命令(含新的 SetRecipe)
玩家 tick(P5)
电网 tick:① 发电机登记供给 ② MachinesTickPreSettle() ③ ElectricGrid.Settle() ④ 发电机烧油 ⑤ MachinesTickPostSettle()
传送带推进 + 拐角交接(现有)
Tick++
```

## 7. `SetRecipe` 命令

`CommandType.SetRecipe = 10`,复用 `ProtoId`(配方 id)+ `X`/`Y`(目标格),不加字段。`Apply` 里:

1. `World.GetEntityAt(X, Y)` 无效,或该实体的 prototype 不是 `AssemblingMachinePrototype` → 拒绝(`RejectedCommandCount++`)。熔炉不接受 `SetRecipe`(自动匹配,显式设置没有意义)——目标是熔炉也算拒绝。
2. `Prototypes.TryGetById(ProtoId, ...)` 失败,或不是 `RecipePrototype`,或其 `Category` 不等于该装配机的 `Category` → 拒绝。
3. 否则 `Machines.SetRecipe(entityId, ProtoId)`(丢弃任何已有 `Progress`,§5 已说明这不造成物品损失)。

不做 reach 判定——`SetRecipe` 是配置操作(同现实世界点击 UI 设置配方),不是物理触碰,`TransferToEntity`/`FromEntity`/`HandMine` 那套 reach 检查是"手伸到那个格子"的物理约束,这里不适用。（备注:M1 没有 UI,`SetRecipe` 命令目前只能靠测试直接构造调用来验证,这与 P5/P7 的其它命令一致——都是先有模拟层能力,UI 在表现层里再接。）

## 8. 数据

`data/base/machines.json`:

```json
[
  { "type": "furnace", "name": "stone-furnace", "tileWidth": 2, "tileHeight": 2,
    "category": "smelting", "inputSlots": 1, "outputSlots": 1, "energyUsage": "90kW" },
  { "type": "assembling-machine", "name": "assembling-machine-1", "tileWidth": 3, "tileHeight": 3,
    "category": "crafting", "inputSlots": 2, "outputSlots": 1, "energyUsage": "75kW" }
]
```

（`energyUsage` 复用 P7 已有的 `Units.ParsePower` 解析成 `EnergyUsageJPerTick`,同 `fuel-generator` 的 `powerOutput` 字段解析方式。）

**不新增配方**——`data/base/recipes.json` 里的 `iron-plate` 配方本来就是 `category: "smelting"`(`energyRequiredSeconds: 3.2` → 192 ticks),这是 P5 就已经埋好的伏笔:`CraftEnqueue`(`Simulation.cs` Apply,`recipe.Category != "crafting"` 直接拒绝)从一开始就不接受这条配方,玩家永远无法手搓出 `iron-plate`;`player.json` 的开局物资包直接给 8 个 `iron-plate`(不是矿+配方),原因正是"这条配方要等熔炉才能跑"。P9 熔炉直接吃这条现成配方,不需要新建。`iron-gear-wheel`/`wooden-chest`(`crafting` 分类)留给装配机测试用(`SetRecipe` 指向其一,原料是 `iron-plate`——玩家开局自带的 8 个,或熔炉产出的都行)。

## 9. 确定性

- `Progress`/`threshold` 全部 `long`(Q16.16 定点"tick 数"),`CraftingSpeed`/satisfaction 相乘走 `Q16.Mul`,无 `float`/`double`。
- 熔炉自动匹配按 `RecipePrototype.Id` 升序遍历,第一个满足的配方赢——确定、可复现(即便未来同一 `Category` 下出现多条配方同时可满足,选哪条也不依赖任何未排序的容器)。
- 全量扫描按 `Entities` 池索引序(同发电机/传送带现有模式)。
- `Machines.WriteState`/`Inventories.WriteState` 都按 `EntityId.Index` 显式排序后写。
- `ElectricGrid.Settle()` 前后两趟扫描严格分离(§6),不依赖"某个机器凑巧在另一个机器登记需求之前处理完"这种时序偶然性。

## 10. 测试

- **`PrototypeLoaderTests`** 追加:`machines.json` 正常加载出 `FurnacePrototype`/`AssemblingMachinePrototype`;负例(`Category` 空、`InputSlots`/`OutputSlots`/`EnergyUsageJPerTick` 非法)。
- **`InventoriesTests`** 追加:同一 `EntityId` 用不同 `role` 各自建库存,互不干扰(role 1 塞满不影响 role 2 能塞);`GetInventoryId(entity, role)` 查无 → `Invalid`;`RemoveContainer(entity, role)` 只摘掉对应 role 的库存。
- **`MachinesTests`**(新文件):`RegisterMachine`/`UnregisterMachine`/`SetRecipe`/`AddProgress`/`MarkCompleted`/`RestartCycle`(`clearRecipe: true` 清空配方、`clearRecipe: false` 保留配方只清进度)的独立行为;`WriteState` 格式(按 index 排序)。
- **`SimulationTests`** 追加:
  - 熔炉自动匹配:输入库存塞入 `iron-ore`,不发任何命令,`Step()` 若干 tick 后输出库存出现 `iron-plate`。
  - 装配机需要 `SetRecipe`:不发命令时永远空转(输入塞满原料也不会消耗);发一次 `SetRecipe` 后连续喂两批原料,验证产出两次而不需要第二次 `SetRecipe`(配方粘滞性——完成一轮后 `CurrentRecipeProtoId` 不变,直接开始下一轮)。
  - `SetRecipe` 拒绝:目标是熔炉、目标不是机器、`Category` 不匹配都各自 `RejectedCommandCount++`;拒绝后原有配方(如果有)不受影响。
  - 电力不足降速:`ElectricGrid` 上该网络只登记一半供给,机器完成配方所需的 tick 数翻倍(验证 satisfaction 等比降速而非二元阻塞)。
  - 待机能耗:机器空转(没匹配到配方/没设配方)时仍能观察到 `ElectricGrid.GetSatisfaction`/网络需求侧统计包含它的 `EnergyUsageJPerTick`(验证第 3 步无条件登记)。
  - 输出堵塞进 pending:输出库存预先塞满,配方到期后 `Completed` 但不清空,`Progress` 不再变化,且待机能耗仍照常登记;腾出输出空间后下一 tick 自动 flush 并按机器类型重新匹配/继续同一配方。
  - 重校验失败:配方进度中途,人为清空输入库存(直接调 `Inventories.Get(...).Remove`,模拟"物品被拿走"),到期时重校验失败,原料不需要归还(因为本来没扣);熔炉的 `CurrentRecipeProtoId` 清空(下一 tick 重新推导),装配机的 `CurrentRecipeProtoId` 保留(下一 tick 原料备齐后直接用同一配方重开)。
- **`DeterminismTests`** 追加:放熔炉 + 电线杆 + 发电机,`TransferToEntity` 塞矿 + 塞煤,跑够完成一整个配方周期的 tick 数,同命令两遍逐 tick 哈希全等;现有 golden 场景仍过。

## 11. 实施拆分

- **Task 1**:`CraftingMachinePrototype`/`FurnacePrototype`/`AssemblingMachinePrototype` + `PrototypeLoader` 解析(两个 type + `ResolveAndValidateCraftingMachines`)+ `data/base/machines.json` + `Inventories` 的 `role` 参数改造(`AddContainer`/`GetInventoryId`/`RemoveContainer`/`WriteState`)+ loader/`Inventories` 测试。不碰 `Simulation`,不改 `recipes.json`(§8:`iron-plate` 配方已经是 `smelting` 分类,现成可用)。
- **Task 2**:`Machines` 子系统(`RegisterMachine`/`UnregisterMachine`/`SetRecipe`/进度读写/`WriteState`)+ `MachinesTests`。不碰 `Simulation`。
- **Task 3**:`Command.SetRecipe` + `Simulation.MachinesTick()`(两趟扫描,§6 全部步骤)+ `Simulation` 接线(`Machines` 属性、`PlaceEntity`/`DestroyEntityAt` 钩子、`Step` 加工段、`WriteState` 追加、`Apply` 的 `SetRecipe` case、`TransferToEntity`/`FromEntity` 的 role 分派)+ `SimulationTests` + `DeterminismTests`。

每个 Task 走完整「实现→审查→(修复→复审)」闭环。Task 3 依赖 Task 1(prototype/role 库存)+ Task 2(`Machines`),必须最后做。
