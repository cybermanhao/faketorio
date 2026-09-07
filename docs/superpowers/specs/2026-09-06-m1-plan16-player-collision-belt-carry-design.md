# M1 Plan 16 — 玩家碰撞盒 + 传送带带人移动 (Design Spec)

日期: 2026-09-06
状态: 已与用户确认
配套参考:
- [`docs/superpowers/specs/2026-09-06-m1-plan15-player-wasd-camera-follow-design.md`](2026-09-06-m1-plan15-player-wasd-camera-follow-design.md) — P15 加了 WASD 玩家操控;本子项改玩家移动的碰撞与受力
- [`docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`](2026-09-02-m1-remaining-roadmap.md) — 候选 ⑥

## 1. 目标

改 sim 层玩家移动的两处行为:

1. **玩家碰撞盒与放置占地解耦。** 每个实体除了放置用的 `tileWidth × tileHeight` 占地,另有一个更小(可为空)的**玩家碰撞盒**。玩家移动只撞碰撞盒,不撞完整占地。传送带 / 机械臂碰撞盒为空(完全可通行);密铺的 3×3 机器之间留一条能走的窄缝。
2. **传送带带人移动。** 玩家所在 tile 是传送带时,每 tick 沿传送带方向被施加一个位移(= 该带的 `speedSubTilesPerTick`),与玩家自己的行走位移向量相加 —— 顺向净速快、逆向慢、站着不动则漂移。这个带人位移可被一个 `Player.Anchored` 开关整体关掉,供将来的"锚定模块"覆盖。

**非目标**(留后续):玩家自身半径 / 滑墙(v1 玩家是点、整步拒绝);锚定模块本体(命令 / 装备 / UI);实体-实体碰撞;载具;非对称碰撞盒。

## 2. 现有代码事实(照此用,勿猜)

- `Simulation.PlayerWalk()`(`sim/Faketorio.Sim/Simulation.cs:178-185`),当前全文:
  ```csharp
  private void PlayerWalk()
  {
      if (!Player.Walking) return;
      var (dx, dy) = Player.WalkDelta(Player.WalkDir, _playerProto.WalkSpeedSubTilesPerTick);
      int nx = Player.X + dx, ny = Player.Y + dy;
      if (World.GetEntityAt(nx >> 8, ny >> 8).IsValid) return;   // 整步拒绝,不滑墙
      Player.MoveTo(nx, ny);
  }
  ```
  在 `Step()` 里每 tick 调一次(玩家 tick 阶段)。`PlayerMine()` 紧随其后。
- `Player`(`sim/Faketorio.Sim/Player/Player.cs`):`int X, int Y`(子格,1 tile = 256)、`byte WalkDir`、`bool Walking`、`bool Mining` 等,全 public 读;状态变更方法全 `internal`。`WriteState` 依次写 `X, Y, WalkDir, Walking(byte), Mining(byte), MineTargetX, MineTargetY, MineProgress, _craftQueue, Inventory`。`internal void MoveTo(int x, int y)`。
- `Player.WalkDelta(byte dir, int speed) -> (int dx, int dy)`:八向,`0=北(-Y)` 顺时针,对角分量取满速。
- `PlayerPrototype.WalkSpeedSubTilesPerTick` = 38。`_playerProto` 是 `Simulation` 的私有字段。
- `EntityPrototype`(`sim/Faketorio.Sim/Prototypes/EntityPrototype.cs`),抽象基类,现有字段:`int TileWidth = 1`、`int TileHeight = 1`、`string? MinableResult`、`int MiningTimeTicks`。子类:`ContainerPrototype`、`TransportBeltPrototype`、`InserterPrototype`、`MiningDrillPrototype`、`FuelGeneratorPrototype`、`ElectricPolePrototype`、`CraftingMachinePrototype`(抽象,其下 `FurnacePrototype` / `AssemblingMachinePrototype`)。
- `EntityData`(`sim/Faketorio.Sim/Entities/EntityData.cs`):`int ProtoId; int X; int Y;`(`X,Y` = footprint 左上角 tile)`byte Rotation;`。`Entities.Get(EntityId) -> ref EntityData`。`Entities.IsAlive(EntityId)`。
- `WorldGrid.GetEntityAt(int x, int y) -> EntityId`(tile 粒度)。`OccupyArea` 把实体占的**每个** tile 都写成该 `EntityId` —— 所以大实体在它覆盖的任一 tile 上 `GetEntityAt` 都返回同一个 `EntityId`。
- `BeltNetwork.GetLineAt(int x, int y) -> BeltLineId`;`.GetLine(BeltLineId) -> BeltLine`;`BeltLine.Direction`(byte 0/1/2/3 = 北/东/南/西);`BeltNetwork.Delta(byte) -> (int dx, int dy)`(北=(0,-1),东=(1,0),南=(0,1),西=(-1,0))。`BeltLineId.IsValid`。
- `Simulation.ResolveBeltSpeed(BeltLine line) -> int`(私有,`Simulation.cs:285`):返回出口格 `TransportBeltPrototype.SpeedSubTilesPerTick`。基础带 = **8**(`data/base/entities.json`:`"speedSubTilesPerTick": 8`)。
- `Simulation` 已有 `Belts`(`BeltNetwork`)、`World`(`WorldGrid`)、`Entities`、`Prototypes` 字段。
- `PrototypeLoader`:`entities.json` 逐条 `{ "type", "name", "tileWidth", "tileHeight", ... }`。有 `private static int GetInt(JsonElement el, string prop, int fallback)`。`ValidateFootprint<T>(T proto)` 在加载期校验占地为正 —— 新的 inset 校验加在同处。各 `EntityPrototype` 子类在 `PrototypeLoader` 里分别构造。
- `bench/golden.json` + `BenchScenarioTests.GOLDEN_TICK_800`(`sim/Faketorio.Sim.Tests/BenchScenarioTests.cs:76` 附近的 `private const ulong`)。golden 重生成:`dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --update-golden --golden bench/golden.json`;`baselineNsPerTick` 手动改回 `0`(见 P14/lane 先例)。bench `ScenarioBuilder`(`sim/Faketorio.Sim.Bench/Scenario/ScenarioBuilder.cs`)**从不**发 `MovePlayer` / 设 `Walking`;`Player` 构造后 `X=Y=0`。
- 确定性铁律:sim 状态无 `float`/`double`;`ComputeStateHash` FNV-1a;同 seed + 命令序列 → 每 tick 逐字节相同。

## 3. 设计

### 3.1 `EntityPrototype.CollisionInsetSubTiles`

`EntityPrototype` 加:
```csharp
// 玩家碰撞盒 = 完整占地矩形四边各向内缩这么多子格。0 = 碰撞盒 == 占地(默认,
// 与历史行为一致)。大到某轴 min >= max 时碰撞盒为空,该实体对玩家完全可通行。
// 只影响玩家移动;放置/占用仍用 TileWidth × TileHeight。
public int CollisionInsetSubTiles { get; init; }
```

- JSON key:`"collisionInsetSubTiles"`,可选,`GetInt(el, "collisionInsetSubTiles", 0)`,每个 `EntityPrototype` 子类构造处都读。
- **加载期校验**(`ValidateFootprint` 同处或紧邻):`CollisionInsetSubTiles < 0` → `InvalidDataException`(负 inset = 碰撞盒大于占地,会要求跨 tile 扫描,v1 不支持)。上界不校验 —— inset 吃满就是"可通行",是合法配置。

### 3.2 `data/base/entities.json` 配置

| 实体 | 占地 | `collisionInsetSubTiles` | 结果 |
|---|---|---|---|
| `transport-belt-basic` | 1×1 | `128` | 1×1 盒 → `min==max` → 空 → 完全可通行 |
| `inserter-basic` | 1×1 | `128` | 空 → 完全可通行 |
| `electric-mining-drill` | 2×2 | `64` | 中心 (2·256−128)=384 子格边长实心块,四周留 64 子格(0.25 tile)缝 |
| `stone-furnace` | 2×2 | `64` | 同上 |
| `burner-generator` | 2×2 | `64` | 同上 |
| `assembling-machine-1` | 3×3 | `96` | 中心 (3·256−192)=576 子格(2.25 tile)实心块,四周留 96 子格(0.375 tile);两台密铺间通道 192 子格(0.75 tile),玩家(点)可单列通过 |
| `wooden-chest` | 1×1 | `0`(不写) | 碰撞盒 == 占地,硬挡(现有行为) |
| `large-chest` | 2×3 | `0`(不写) | 硬挡 |
| `small-electric-pole` | 1×1 | `0`(不写) | 硬挡 |

> 实现期按 `data/base/entities.json` 里实际的 `name` / `tileWidth` / `tileHeight` 逐条落。表里名字若与实际不符以实际为准,规则是:传送带 / 机械臂 → inset = 半个占地(盒为空);多格机器 → inset ≈ `占地子格 / 8`(留约 0.25~0.375 tile 边缝);箱子 / 电线杆 → 不写(inset 0)。

### 3.3 玩家 = 点(v1)

玩家碰撞体就是子格点 `(Player.X, Player.Y)`。不引入玩家半径。滑墙留 v2。

### 3.4 `Simulation.PlayerWalk()` 重写

方法名不变(仍叫 `PlayerWalk`,仍在同一 tick 阶段调)。新实现:

```csharp
private void PlayerWalk()
{
    // 1. 玩家自己的行走位移(不走路则 0)
    int wdx = 0, wdy = 0;
    if (Player.Walking)
        (wdx, wdy) = Player.WalkDelta(Player.WalkDir, _playerProto.WalkSpeedSubTilesPerTick);

    // 2. 传送带带人位移:玩家当前所在 tile 是传送带、且未锚定
    int cdx = 0, cdy = 0;
    if (!Player.Anchored)
    {
        var lineId = Belts.GetLineAt(Player.X >> 8, Player.Y >> 8);
        if (lineId.IsValid)
        {
            var line = Belts.GetLine(lineId);
            var (bdx, bdy) = BeltNetwork.Delta(line.Direction);
            int carry = ResolveBeltSpeed(line);
            cdx = bdx * carry;
            cdy = bdy * carry;
        }
    }

    if (wdx == 0 && wdy == 0 && cdx == 0 && cdy == 0) return;

    int nx = Player.X + wdx + cdx;
    int ny = Player.Y + wdy + cdy;

    // 3. 整步拒绝(不滑墙):目标点落在任一实体碰撞盒内 → 不移动
    if (PlayerPointBlocked(nx, ny)) return;
    Player.MoveTo(nx, ny);
}

// 目标子格点是否被某实体的玩家碰撞盒挡住。
// inset >= 0 保证碰撞盒 ⊆ 占地,故点若在某盒内,该实体必占点所在 tile ——
// 查点自己那一个 tile 的 GetEntityAt 即可,无需扫相邻 tile。
private bool PlayerPointBlocked(int px, int py)
{
    var id = World.GetEntityAt(px >> 8, py >> 8);
    if (!id.IsValid) return false;

    ref readonly var data = ref Entities.Get(id);
    var proto = (EntityPrototype)Prototypes.GetById(data.ProtoId);
    int inset = proto.CollisionInsetSubTiles;

    int minX = data.X * 256 + inset;
    int maxX = (data.X + proto.TileWidth) * 256 - inset;
    int minY = data.Y * 256 + inset;
    int maxY = (data.Y + proto.TileHeight) * 256 - inset;
    if (minX >= maxX || minY >= maxY) return false;   // 碰撞盒为空 → 可通行

    return px >= minX && px < maxX && py >= minY && py < maxY;
}
```

要点:
- `256` 用现有常量(`sim` 侧若已有 `WorldGrid` 或别处的 sub-tile 常量则复用;没有就 `const int SubTilesPerTile = 256` 就近声明,值与 `BeltLine.TileSubTiles` 一致)。
- `ref readonly var data = ref Entities.Get(id)` —— `Entities.Get` 返回 `ref EntityData`,只读用 `ref readonly`。
- "顺快逆慢"是 `wd* + cd*` 向量加法的自然结果,无特判。站着不动在带上:`wd*=0`,`nx = X + cdx` → 每 tick 漂移 `carry` 子格。
- 判带用**玩家当前 tile**(tick 开始时的位置),不是目标 tile —— 标准做法,避免"半只脚上带"的分类问题。

### 3.5 `Player.Anchored`

`Player` 加:
```csharp
public bool Anchored { get; private set; }
internal void SetAnchored(bool value) => Anchored = value;
```
- `WriteState`:在 `Mining` 那个 `byte` 之后、`MineTargetX` 之前,加 `writer.Write((byte)(Anchored ? 1 : 0));`。**这会改状态哈希的字节布局** —— 所以即使 bench 玩家从不锚定,golden 也必然要重生成(多写了一个字节)。见 §4。
- 本子项**不加**任何设置 `Anchored` 的命令 / 路径 —— 它恒为 `false`,只是把缝留好。将来的锚定模块子项加 `CommandType.SetAnchor` 之类。

### 3.6 命令 / tick 语义

不新增命令。`PlayerWalk` 仍每 tick 跑,读 `Player.Walking`/`WalkDir`(P5 的持久状态,由 P15 的 `MovePlayer`/`StopPlayer` 维护)。表现层无改动需求(`WorldView` 已按 `Player.X/Y` 画玩家)。

## 4. 确定性 / golden 影响

- 全整数子格运算;`PlayerPointBlocked` 是整数比较。无 `float`/`double` 进 sim 状态。
- `CollisionInsetSubTiles` 是 prototype 静态配置,不进 `WriteState`。
- **`Player.Anchored` 进 `WriteState`(多一个 byte)→ 每 tick 哈希字节布局变 → golden 必然要重生成。** 实现最后一步:
  1. `dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --update-golden --golden bench/golden.json`
  2. 手动把 `bench/golden.json` 的 `baselineNsPerTick` 改回 `0`
  3. 用新的 tick-800 哈希更新 `BenchScenarioTests.GOLDEN_TICK_800`,注释说明因 `Player.Anchored` 入 `WriteState` + 玩家碰撞模型改动重基线
  4. `dotnet run ... --golden bench/golden.json`(不带 `--update-golden`)确认 `exit 0, gate PASS`
- bench 玩家全程 `Walking=false` 且停在 (0,0)。若 (0,0) 不是传送带(极大概率),`PlayerWalk` 每 tick 走到 `wdx=wdy=cdx=cdy=0` 的 early return,`Player.X/Y` 不动 —— 哈希变化**仅**来自那个新 byte。若 (0,0) 恰是传送带,玩家还会漂移 —— 也是合法变化,同样 `--update-golden` 收进去。两种情况处理一样。
- `determinism` 类测试(同 seed 跑两遍逐 tick 哈希相等)不受影响 —— 新逻辑是确定的。

## 5. 测试

`sim/Faketorio.Sim.Tests/SimulationTests.cs` 扩(或新建 `PlayerCollisionTests.cs`,跟现有布局一致即可):

1. **旧行为保持**:玩家朝一个 `inset=0` 的木箱走 → N tick 后 `Player.X/Y` 未越过箱子边界(被挡)。
2. **可通行传送带 + 漂移**:放一条向东的带,把玩家 `MoveTo` 到带上某子格(测试可用 internal `MoveTo`,或发 `MovePlayer` 走上去)。`Player.Walking=false`,跑 K tick → `Player.X` 增加 `K * ResolveBeltSpeed`,`Player.Y` 不变。
3. **顺向叠加**:玩家在东向带上、`WalkDir=东`、`Walking=true`,跑 1 tick → `ΔX = walkSpeed + carry`(46)。
4. **逆向**:玩家在东向带上、`WalkDir=西`、`Walking=true`,跑 1 tick → `ΔX = carry − walkSpeed`(8 − 38 = −30,仍向西移动)。
5. **锚定**:场景同 #2,但 `Player.SetAnchored(true)`(测试用 internal)→ 跑 K tick,`Player.X/Y` 不变。
6. **机器可穿边缝**:放两台 `assembling-machine-1`,原点相隔 3 tile(密铺,无 gap)。玩家点从缝一端(两台之间那条 `2*inset` 宽的走廊里)一路 `MovePlayer` 到另一端 → 走得通,`Player` 坐标穿过了缝的全长。
7. **机器中心仍挡**:玩家点正对一台 `assembling-machine-1` 的几何中心走 → 被挡(未穿过)。
8. **inset 边界**:构造玩家点恰在某机器碰撞盒 `maxX` 上(`px == maxX`)→ 不被挡(半开区间 `< maxX`);`px == maxX - 1` → 被挡。
9. **`FeedsRightLane` / 现有传送带 & 采矿链测试**不回归(带人逻辑不碰 lane / 物品)。
10. golden gate:重生成后 `exit 0, gate PASS`;`GOLDEN_TICK_800` 已同步。

`Faketorio.Presentation.Core` / `game/` **无改动**,无新前端测试。

## 6. 全局约束(实现期照此)

- 只改 `sim/`:`Simulation.cs`(`PlayerWalk` 重写 + `PlayerPointBlocked` 新增)、`Player.cs`(`Anchored` + `SetAnchored` + `WriteState` 加 byte)、`EntityPrototype.cs`(`CollisionInsetSubTiles`)、`PrototypeLoader.cs`(读 + 校验)、`data/base/entities.json`(逐条 inset)、`sim/Faketorio.Sim.Tests/*`、`bench/golden.json`、`BenchScenarioTests.cs`(`GOLDEN_TICK_800`)。
- sim 状态不引入 `float`/`double`。
- `CollisionInsetSubTiles` 纯新增 init-prop,默认 0 = 历史行为;不改 `IsAreaFree`/`OccupyArea`/`ValidateFootprint` 的占地语义(只在其旁加 inset 的负值校验)。
- 放置逻辑、传送带物品流、采矿 / 加工 / 电网 —— 一律不碰。
- `game/` / `Faketorio.Presentation.Core` 零改动。
- golden 是**主动重基线**(因 `Player.Anchored` 入 `WriteState`),不是"保持绿" —— 但重基线后门禁必须绿,且 `determinism` 测试必须绿。
- 提交信息结尾:
  ```
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_016Z7a7Yd8hWCT3UaWXorJQW
  ```

## 7. 交付物清单

- `sim/Faketorio.Sim/Prototypes/EntityPrototype.cs` — `CollisionInsetSubTiles`
- `sim/Faketorio.Sim/Prototypes/PrototypeLoader.cs` — 读 `collisionInsetSubTiles` + 负值校验
- `sim/Faketorio.Sim/Player/Player.cs` — `Anchored` / `SetAnchored` / `WriteState` +1 byte
- `sim/Faketorio.Sim/Simulation.cs` — `PlayerWalk` 重写 + `PlayerPointBlocked`
- `data/base/entities.json` — 逐条 `collisionInsetSubTiles`
- `sim/Faketorio.Sim.Tests/SimulationTests.cs`(或新 `PlayerCollisionTests.cs`)— §5 用例
- `bench/golden.json` — 重生成(`baselineNsPerTick` 保持 0)
- `sim/Faketorio.Sim.Tests/BenchScenarioTests.cs` — `GOLDEN_TICK_800` 更新 + 注释
