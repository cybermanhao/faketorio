# M1 Plan 5 — 玩家 + 手挖 + 手搓 + 开局物资包 设计

日期: 2026-09-04
状态: 已与用户确认的设计基线
前置依赖: P1(模拟核心)、P4(库存,`Inventory`/`ItemStack`)、P6(矿脉,`ResourceGrid`/`ValueNoise.Isqrt`)——均已合并进 `main`(`ee36c52`)
配套参考: [`docs/superpowers/specs/2026-07-03-faketorio-design.md`](2026-07-03-faketorio-design.md)(§5 模拟层、§7 M1 自举路径)、[`docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`](2026-09-02-m1-remaining-roadmap.md)(P5 条目)

## 1. 目标

给模拟层一个**玩家**:实体化的位置(子格)、行走意图移动(带碰撞)、手挖(HandMine——挖实体或矿脉,进度累加,产出进玩家背包)、手搓(HandCraft——有界 FIFO 合成队列)、以及 sim 启动时的**开局物资包**。

这是 M1 可玩闭环的第一块:第一次把 P4 库存 + P6 矿脉 + `RecipePrototype` + 命令系统组合成可玩玩法,解开路线图 §7 的自举死锁(手挖供第一批启动煤、手搓补建材、开局物资包给首批建材)。

**不做**:碰撞的滑墙(整步拒绝即可)、合成取消 / 输入退还、快捷栏 / 光标堆叠 / held item(表现层,见 §11)、玩家受伤 / 死亡、多玩家、寻路。

## 2. 组件与文件结构

**新文件**

| 文件 | 职责 |
|---|---|
| `sim/Faketorio.Sim/Player/Player.cs` | `sealed class Player`:位置(子格 int)、`WalkDir`(0..7)/`Walking`、挖掘状态(`Mining` / `MineTargetX/Y` / `MineProgress`)、合成队列(`List<CraftJob>`)、玩家 `Inventory`、`WriteState`。状态变更方法 `internal`。 |
| `sim/Faketorio.Sim/Player/CraftJob.cs` | `record struct CraftJob(int RecipeProtoId, int Count, long Progress)`(`record struct` 以便队头结算用 `with` 改 `Count`/`Progress`)。 |
| `sim/Faketorio.Sim/Prototypes/PlayerPrototype.cs` | `PlayerPrototype : PrototypeBase`。 |
| `data/base/player.json` | 单条 `player`:参数 + 开局物资包。 |

**改动**

| 文件 | 改动 |
|---|---|
| `sim/Faketorio.Sim/Prototypes/RecipePrototype.cs` | 加 `IReadOnlyList<ResolvedAmount> ResolvedIngredients` / `ResolvedResults`(`{ get; internal set; }`);`readonly record struct ResolvedAmount(int ItemProtoId, int Amount)`。 |
| `sim/Faketorio.Sim/Items/Inventory.cs` | 加 `public bool CanInsert(int itemProtoId, int count, int stackSize)` —— 只读模拟两轮插入,不改状态(§6.2 手搓完成前预检用)。 |
| `data/base/map-gen.json` | 加 1 块贴近原点的启动矿斑(如 `coal` 中心 (1,-1) 半径 2)——玩家出生 (0,0) 的 reach(1536 子格 ≈ 6 tile)够不到 P6 现有的 4 块矿斑(都 ≈10 tile 远),自举第一挖需要能就地挖到东西。 |
| `sim/Faketorio.Sim/Prototypes/EntityPrototype.cs` | `ResourcePrototype` 加 `int MiningTimeTicks { get; init; } = 60`(矿脉每单位挖掘 tick 数)。 |
| `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs` | `Parse` 加 `"player"` arm;post-`AssignIds` pass 里解析每个 `RecipePrototype` 的 `Ingredients`/`Results` 名字 → proto id + 校验;校验 `PlayerPrototype`。 |
| `sim/Faketorio.Sim/Commands/Command.cs` | `CommandType` 加 `MovePlayer=3, StopPlayer=4, MineStart=5, MineStop=6, CraftEnqueue=7`。 |
| `sim/Faketorio.Sim/Simulation.cs` | `public Player Player { get; }`;构造函数建 `Player` + 填物资包;`Apply` 加 5 个 case;`Step` 加「玩家 tick」;`WriteState` 追加 `Player.WriteState`;`RemoveEntity` case 体抽成 `DestroyEntityAt` helper。 |
| `data/base/recipes.json` | 加 ≥ 2 条 `"crafting"` 配方(现只有 `iron-plate` 是 `"smelting"`)。 |
| `data/base/items.json` | 补 `iron-gear-wheel`(手搓配方产物 / 输入)。 |

`Player` 不进 `EntityPool`——不占地、不放置/拆除,是 `Simulation` 上的独立对象。背包是它直接持有的 `Inventory`(P4 的 `Inventory` 可 `new Inventory(slotCount)` 独立构造),自己进状态哈希。

## 3. `PlayerPrototype` + 数据 + 配方解析

```
public sealed class PlayerPrototype : PrototypeBase   // 单例,约定 name = "player"
{
    public int InventorySize            { get; init; } = 60;
    public int ReachSubTiles            { get; init; } = 1536;  // 6 tile × 256
    public int WalkSpeedSubTilesPerTick { get; init; } = 38;    // ≈0.15 tile/tick
    public int CraftQueueCap            { get; init; } = 32;
    public IReadOnlyList<ItemAmount> StartingInventory { get; init; } = Array.Empty<ItemAmount>();
}
```

`data/base/player.json`:
```json
[{ "type": "player", "name": "player",
   "inventorySize": 60, "reachSubTiles": 1536, "walkSpeedSubTilesPerTick": 38, "craftQueueCap": 32,
   "startingInventory": [
     { "name": "iron-plate", "amount": 8 },
     { "name": "wooden-chest", "amount": 1 }
   ] }]
```

`data/base/recipes.json` 追加(示意最终值在实施计划里定):
```json
{ "type": "recipe", "name": "iron-gear-wheel", "category": "crafting", "energyRequiredSeconds": 0.5,
  "ingredients": [ { "name": "iron-plate", "amount": 2 } ],
  "results": [ { "name": "iron-gear-wheel", "amount": 1 } ] },
{ "type": "recipe", "name": "wooden-chest", "category": "crafting", "energyRequiredSeconds": 0.5,
  "ingredients": [ { "name": "iron-plate", "amount": 2 } ],
  "results": [ { "name": "wooden-chest", "amount": 1 } ] }
```
`data/base/items.json` 追加 `{ "type": "item", "name": "iron-gear-wheel", "stackSize": 100 }`。

`data/base/map-gen.json` 的 `starterPatches` **末尾追加**一块贴近原点的:`{ "resource": "coal", "centerX": 1, "centerY": -1, "radius": 2, "centerAmount": 800 }`。玩家出生 (0,0),到 (1,-1) 格中心 ≈ 543 子格 < `ReachSubTiles` 1536,第一挖就能就地拿到启动煤。P6 现有 4 块矿斑都 ≈ 10 tile 远(reach 够不到),不改。**追加**保持 `StarterPatches[0]` 仍是 coal (6,-8);P6 的 `PrototypeLoaderTests.LoadsMapGenWithStarterPatches` 里 `Count == 4` 的断言改成 `5`(是这次唯一需要动的既有 P6 测试)。

### 3.1 解析 / 校验 pass(`PrototypeLoader`,`AssignIds()` 之后,复用 P6 建立的模式)

- 每个 `RecipePrototype`:`Ingredients` / `Results` 里每个 `ItemAmount` → `ResolvedAmount(ItemProtoId, Amount)`(按 `Name` 查 `ItemPrototype`,查不到 → `InvalidDataException`);`Amount < 1` → 抛。写回 `ResolvedIngredients` / `ResolvedResults`。
- **`player` prototype**(与 P6 的 `map-gen` 完全对称):
  - loader 侧:出现 **>1 条 `player` → 加载期抛 `InvalidDataException`**。0 条不在加载期报错(临时目录负例测试各自只放一个 prototype,不含 `player`)。
  - `Simulation` 构造期:registry 无 `player` → 抛 `InvalidOperationException`(§7)。
  - 存在 `player` 时,loader 校验其字段:`InventorySize < 1` / `ReachSubTiles < 1` / `WalkSpeedSubTilesPerTick < 1` / `CraftQueueCap < 1` → 抛;`StartingInventory` 每个 `Name` 能查到 `ItemPrototype`,否则抛。

## 4. `Player` 状态 + 移动

```
public sealed class Player
{
    public int X { get; private set; }         // 子格(256/tile),出生 (0,0)
    public int Y { get; private set; }
    public byte WalkDir { get; private set; }  // 0..7,0=北 顺时针;仅 Walking 时有意义
    public bool Walking { get; private set; }

    public bool Mining          { get; private set; }
    public int  MineTargetX     { get; private set; }
    public int  MineTargetY     { get; private set; }
    public long MineProgress    { get; private set; }

    public IReadOnlyList<CraftJob> CraftQueue { get; }
    public readonly Inventory Inventory;

    public Player(int inventorySize);
    public void WriteState(IStateWriter writer);
    // 状态变更方法 internal(只有 Simulation 调):StartWalk/StopWalk/StepWalk/
    //   StartMine/StopMine/AdvanceMine.../EnqueueCraft/AdvanceCraft... 具体在实施计划里定
}
```

### 4.1 命令

| 命令 | 字段 | 效果 |
|---|---|---|
| `MovePlayer` | `Rotation` = 方向 0..7 | `Walking = true; WalkDir = Rotation`。`Rotation > 7` → 拒绝(`RejectedCommandCount++`)。 |
| `StopPlayer` | — | `Walking = false`。 |

表现层按住方向键时每 tick 重提交 `MovePlayer`(同方向无副作用)。

### 4.2 每 tick 行走(玩家 tick,命令应用之后)

- `Walking == false` → 不动。
- 八向单位向量表(子格,方向 0..7 = 北/东北/东/东南/南/西南/西/西北),**对角分量也取满速**:
  ```
  0:( 0,-s)  1:(+s,-s)  2:(+s, 0)  3:(+s,+s)
  4:( 0,+s)  5:(-s,+s)  6:(-s, 0)  7:(-s,-s)   其中 s = WalkSpeedSubTilesPerTick
  ```
  (对角比正向快 √2 倍——M1 接受,spec 点明这个近似;Factorio 也按 8 向速度表,差异不影响自举。)
- 目标 `(nx, ny) = (X+dx, Y+dy)`。**碰撞**:目标所在 tile `(nx >> 8, ny >> 8)`;`World.GetEntityAt(tile)` 有效 → **整步拒绝**,本 tick 位置不变(不滑墙)。否则 `X = nx; Y = ny`。
- 玩家是点,无 footprint。

## 5. HandMine(手挖)

### 5.1 命令

| 命令 | 字段 | 效果 |
|---|---|---|
| `MineStart` | `X`/`Y` = 目标格 | 若 `(X,Y)` 与当前 `MineTarget` 不同:`MineTargetX/Y = (X,Y); MineProgress = 0`。`Mining = true`。 |
| `MineStop` | — | `Mining = false; MineProgress = 0`。 |

表现层按住挖掘键时每 tick 重提交 `MineStart`(同目标无副作用)。

### 5.2 每 tick 挖掘(玩家 tick,行走之后)

1. `Mining == false` → 无操作。
2. **reach 判定**:玩家点 `(X,Y)` 到目标格中心 `(tx*256+128, ty*256+128)` 的欧氏距离 `= ValueNoise.Isqrt((long)ddx*ddx + (long)ddy*ddy)`;`> ReachSubTiles` → 本 tick 不推进(`MineProgress` 保留),结束。
3. **解析目标**(优先实体):
   - `World.GetEntityAt(tx,ty)` 存活 且其 `EntityPrototype.MinableResult != null` → 挖实体,阈值 `= proto.MiningTimeTicks`。
   - 否则 `Resources.GetResourceAt(tx,ty)` 非空 → 挖矿脉,阈值 `= ResourcePrototype.MiningTimeTicks`。
   - 都不是 → 本 tick 空转(不推进,命令不算被拒),结束。
4. `MineProgress += 1`;`< 阈值` → 结束本 tick。
5. **达到阈值 → 结算一次**:
   - 产出 `id = MinableResult 对应 ItemPrototype.Id`,数量 1。`Inventory.Insert(id, 1, itemProto.StackSize)`。
   - **返回 0(背包满)** → `MineProgress` 停在阈值,不产出、不清目标,下 tick 重试。
   - **返回 1**:
     - 挖实体:`DestroyEntityAt(entityId, proto)`(§7,与 `RemoveEntity` 命令共用)。目标随即失效 → 下 tick 第 3 步空转。
     - 挖矿脉:`Resources.Extract(tx,ty,1)`(必返回 1,刚查过非空)。`MineProgress = 0`,继续(下 tick 若仍非空 + 在 reach + 目标未变则接着挖;矿脉挖空后第 3 步空转)。
6. 目标变化由第 5.1 的 `MineStart` 分支处理(`MineProgress = 0`)。

### 5.3 `Player.WriteState` 挖掘部分

`Mining`(byte)、`MineTargetX`(int)、`MineTargetY`(int)、`MineProgress`(long)。

## 6. HandCraft(手搓)

### 6.1 命令 / 入队(`Apply` 里即时)

`CraftEnqueue`:`ProtoId` = 配方 id,`X` = 份数。

1. `ProtoId` 越界 / 不是 `RecipePrototype` / `X < 1` / `recipe.Category != "crafting"` / `!recipe.Enabled` → 拒绝(`RejectedCommandCount++`)。
2. `CraftQueue.Count == PlayerPrototype.CraftQueueCap` → 拒绝。
3. **一次性扣 `X` 份全部输入**:对每个 `(itemId, amt) ∈ recipe.ResolvedIngredients`,要求 `Inventory.CountOf(itemId) >= amt * X`。任一不够 → 拒绝,不扣任何东西。全够 → 逐个 `Inventory.Remove(itemId, amt * X)`。
4. `CraftQueue.Add(new CraftJob(ProtoId, X, 0))`。

### 6.2 每 tick 合成(玩家 tick,挖掘之后)

1. 队列空 → 无操作。
2. 队头 `job`:`job.Progress += 1`(手搓速度恒 1×/tick,无速度字段)。
3. `job.Progress < recipe.EnergyRequiredTicks` → 结束本 tick。
4. **达到阈值 → 结算一份产出**:
   - **预检(全有或全无)**:对 `recipe.ResolvedResults` 里的产物,判断 `Inventory` 能否全部容纳。M1 手搓配方均**单产物**:`Inventory.CanInsert(itemId, amt, stackSize)` 单次判断。多产物时逐个 `CanInsert`(近似——产物间占槽相互影响,P9 机器加工再收紧;spec 记为已知简化点)。
   - 放不下 → job **阻塞**:`job.Progress` 停在阈值,不产出、不推进队列,下 tick 重试。
   - 全放得下 → 逐个 `Inventory.Insert(itemId, amt, stackSize)`(保证全成功);`job.Count -= 1`。
   - `job.Count == 0` → 移除队头;否则 `job.Progress = 0`,同一 job 继续下一份。

`Inventory` 新增 `public bool CanInsert(int itemProtoId, int count, int stackSize)`——只读模拟两轮插入(补未满同类槽 + 占空槽),不改状态,返回是否能全部放下。

### 6.3 `Player.WriteState` 合成部分

`CraftQueue.Count`(int),然后逐 job:`RecipeProtoId`(int)、`Count`(int)、`Progress`(long)。

## 7. Simulation 接线

- `public Player Player { get; }`。构造函数:registry 取 `PlayerPrototype`("player");无 → `throw new InvalidOperationException`。`Player = new Player(proto.InventorySize)`;遍历 `proto.StartingInventory` 解析 id 后 `Player.Inventory.Insert(id, amount, itemProto.StackSize)`(放不下静默丢弃,§3.1 已保证 `InventorySize ≥ 1`)。
- **`Step` 顺序**:
  ```
  应用命令(Apply loop)
  玩家 tick:① 行走(§4.2,含碰撞) ② 挖掘进度 + 结算(§5.2) ③ 合成队头进度 + 结算(§6.2)
  传送带推进(现有)
  传送带拐角交接(现有)
  Tick++
  ```
  玩家 tick 在传送带之前:手挖若移除一条 belt,`DestroyEntityAt` 已调 `Belts.RemoveBelt`,同 tick 后面的 belt 推进看到移除后的网络——和 `RemoveEntity` 命令路径一致。
- **`Apply`** 加 5 个 case(§4.1 / §5.1 / §6.1)。
- **`WriteState`**:`Resources.WriteState(writer)` 之后 `Player.WriteState(writer)`。
- **`DestroyEntityAt(EntityId id, EntityPrototype proto)`**:把现有 `RemoveEntity` case 体(`World.ClearArea` + `Entities.Destroy` + `if (isBelt) Belts.RemoveBelt` + `if (isContainer) Inventories.RemoveContainer`)抽成私有 helper,命令路径和 HandMine 共用。`RemoveEntity` case 改为「校验 + 取 proto + 调 `DestroyEntityAt`」。

## 8. 确定性 / `IStateWriter`

- 全 `int` / `long`:位置子格、速度、reach、进度累加器、八向单位向量常量表。距离用 `ValueNoise.Isqrt`(P6,整数)。**无 `float` / `double`**。
- `Player.WriteState` 定长顺序写(位置 → 朝向 → 挖掘 → 队列按索引序 → `Inventory.WriteState`),无 `Dictionary` 迭代。
- `Simulation.WriteState` 追加一行,位置固定(`Resources` 之后)。现有确定性测试是相对比较;追加空闲 `Player`(出生位置 + 物资包,两遍一致)不破坏 golden `RunScenario` / `RunBeltScenario` / `RunInventoryScenario` / `RunResourceScenario`。
- 命令按 `CommandQueue` 提交序应用(已有保证)。`CraftQueue` 是 `List<CraftJob>`,FIFO,按索引序写。

## 9. 测试

- **`PlayerTests`**(`Player` 类单元):八向行走位移正确(方向 0..7 各一);`Inventory` 直接可用;`WriteState` 对位置 / `WalkDir` / `Mining` / `MineProgress` / 队列 / 背包内容各自敏感。
- **`PrototypeLoaderTests`** 追加:`recipes.json` 的 `crafting` 配方名解析成 `ResolvedIngredients`/`ResolvedResults`;某配方引用不存在的 item → 抛;`player.json` 正常加载;负例各一个——`player` 出现 2 条、`InventorySize < 1`、`startingInventory` 引用不存在 item、`ReachSubTiles`/`WalkSpeedSubTilesPerTick`/`CraftQueueCap < 1`。
- **`SimulationTests`** 追加:
  - **移动**:`MovePlayer(dir=2)` 后 `Step` → `Player.X` += `WalkSpeed`;`StopPlayer` → 不动;`MovePlayer(Rotation=8)` → `RejectedCommandCount == 1`、不动。
  - **碰撞**:在玩家东侧一格放箱子 → `MovePlayer(dir=2)` + `Step` → 位置不变。
  - **HandMine 实体**:放 `wooden-chest` 于 reach 内 → `MineStart` 指向它 → 跑 `MiningTimeTicks` tick → 箱子销毁、背包 `CountOf(wooden-chest) == 1`、`Belts` 活跃线数不变、`RejectedCommandCount == 0`。
  - **HandMine 矿脉**:`MineStart` 指向贴近原点的 coal 矿斑 (1,-1)(§3 新增,出生点 reach 内)→ 跑 `ResourcePrototype.MiningTimeTicks` tick → 背包 +1 coal、`Resources.GetResourceAt(1,-1).Amount` 少 1;把背包塞满后继续 → 进度停在阈值、矿量不再减。
  - **reach**:`MineStart` 指向 P6 原有的远矿斑(如 (6,-8),≈10 tile > reach 6)→ 跑很多 tick,`MineProgress` 不推进。走近后(`MovePlayer` 若干 tick 进 reach)再挖 → 开始推进。
  - **HandCraft**:开局 `iron-plate` ×8 → `CraftEnqueue(iron-gear-wheel, 2)` → 立即 `CountOf(iron-plate) == 4`;跑 `2 * EnergyRequiredTicks` tick → `CountOf(iron-gear-wheel) == 2`、队列空;输入不够(`CraftEnqueue(..., 99)`)→ 被拒、背包不变;队列填满 → 再入队被拒;背包塞满后完成 → 队头 job 阻塞(队列不空、进度不涨过阈值对应的产出)。
- **`DeterminismTests`** 追加:`RunPlayerScenario(seed)` —— 建 sim,若干 tick 内提交 Move / Mine(挖启动矿斑)/ CraftEnqueue,逐 tick `ComputeStateHash`;同 seed + 同命令两遍全等;现有 4 个 golden 场景仍过。

## 10. 实施拆分

- **Task 1**:`PlayerPrototype` + `RecipePrototype.ResolvedAmount`/`Resolved*` + `ResourcePrototype.MiningTimeTicks` + `Inventory.CanInsert` + `PrototypeLoader`(`player` arm + 配方解析 pass + 校验)+ `data/base/player.json` / `recipes.json` / `items.json` / `map-gen.json`(近原点矿斑)+ `PrototypeLoaderTests` / `InventoryTests` 追加。不碰 `Simulation`。
- **Task 2**:`Player` 类(位置 / 朝向 / 背包 / `WriteState`)+ `CraftJob` + `MovePlayer` / `StopPlayer` 命令 + §4.2 行走 + 碰撞 + `Simulation` 接线(`Player` 属性 + 构造 + 物资包留空、玩家 tick 只做行走段、`WriteState` 追加、`DestroyEntityAt` 抽取)+ `PlayerTests` + `SimulationTests` 移动/碰撞。
- **Task 3**:HandMine(`MineStart` / `MineStop` + §5.2 全流程 + 实体经 `DestroyEntityAt` + 矿脉经 `Resources.Extract` + 背包满暂停 + reach)+ `SimulationTests` 挖掘。
- **Task 4**:HandCraft(`CraftEnqueue` + §6.1 入队扣输入 + §6.2 队头进度 + 完成阻塞,消费 Task 1 的 `Inventory.CanInsert`)+ `SimulationTests` 合成。
- **Task 5**:开局物资包填充(`Simulation` 构造)+ `DeterminismTests` 追加 + 全链路 `SimulationTests`(放箱子 → 手挖回收 → 手搓 → 闭环)。

每个 Task 走完整「实现→审查→(修复→复审)」闭环。

## 11. 表现层 / 未来(不在 P5)

- **快捷栏 / 光标堆叠 / held item**:表现层(Godot Control)。参考 `C:\code\godot-playgroud`(AlchemFarm)`Player.gd` 的快捷栏语义——8 槽存 bag index,`swap_bag` 时快捷栏引用**跟着物品走**(不跟槽位),`quickbar_badge` 给 1-indexed 角标,`bind`/`unbind`。sim 层不涉及。
- **面朝方向 → 目标格**:P5 的 `MineStart` 收显式 `(x,y)`;表现层按 `Player.WalkDir` + 光标算出目标格再提交。
- **合成取消 / 输入退还**:跨 tick 状态,推迟。
- **碰撞滑墙**、**玩家 footprint / 碰撞盒**:M1 整步拒绝 + 点模型够用。
- **走路踩矿 / 踩水**:P6 矿层不挡路;水域(§8 of P6 spec)将来进 `World.IsAreaFree` 时,玩家碰撞也要查它。
