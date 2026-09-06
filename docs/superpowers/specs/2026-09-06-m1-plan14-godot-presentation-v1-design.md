# M1 Plan 14 — Godot 表现层 v1(只读渲染 + 双模式相机 + 最小放置命令)设计

日期: 2026-09-06
状态: 已与用户确认的设计基线
前置依赖: P1–P13(P13 在 PR #1,合并后本子项开工;若 P13 尚未合并,本子项从 `main` 拉分支时 rebase)。sim 层公开面稳定:`Simulation`(`Submit(in Command)` / `Step()` / `ComputeStateHash()` / `Tick` / `Prototypes` / `Entities` / `Belts` / `Player` / `Resources` / `World`)、`EntityData{ProtoId,X,Y,Rotation}`、`Command{Type,ProtoId,X,Y,Rotation,Count}`、`CommandType.{PlaceEntity,RemoveEntity}`、`PrototypeLoader.LoadFromDirectory(path)`。
配套参考: [`docs/superpowers/specs/2026-07-03-faketorio-design.md`](2026-07-03-faketorio-design.md)(§2 分层与命令 tick 边界、§6 表现层、§9 C#↔Godot 边界风险)、[`docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`](2026-09-02-m1-remaining-roadmap.md)("下一步⑤ 转向表现层")

## 1. 目标

给已完成的 `Faketorio.Sim`(纯 C#/.NET 8 确定性模拟层)加**第一个 Godot 表现层子项**,范围收窄到"能证明架构成立的最小端到端闭环":

- **只读渲染**:启动能看到世界在动 —— 网格 gizmo、地块底色、矿脉、实体(占位色块)、传送带上的物品、玩家。
- **双模式相机骨架**:默认 `Follow`(跟随一个目标抽象,v1 目标是静止的玩家);`M` 切 `Free`(= 地图模式:拖拽平移 + 滚轮缩放,可缩很远)。
- **最小放置命令**:左键在光标格 `Submit(PlaceEntity, wooden-chest)`,右键 `Submit(RemoveEntity)`。硬编码单一 proto,无 UI、无快捷栏、无旋转。打通 输入→`Command`→`Sim.Step()`→渲染 整条链。

语言 **C#**(Godot 4.5.1 Mono),spec §6 已定。单进程:Godot 工程 `<ProjectReference>` `Faketorio.Sim`,一个 autoload 单例持有唯一 `Simulation` 并按固定步长调 `Step()`。

**不做**(§8 展开):视觉插值(lerp)、完整命令 UI、玩家 WASD 控制、真美术 / sprite / 动画帧、保留模式渲染(`TileMapLayer`/`MultiMesh`)、战争迷雾 / charting、抽象地图渲染、导出打包、多进程 / sim 独立线程、`ResourceGrid.WriteState` 只序列化脏 chunk。

## 2. 组件与文件结构

**新增(纯 C# 类库,零 Godot 依赖,进 `Faketorio.sln` + CI `test` job):**

```
presentation/
  Faketorio.Presentation.Core/
    Faketorio.Presentation.Core.csproj          net8.0,无依赖
    TickAccumulator.cs
    WorldTransform.cs
    Vec2.cs / RectI.cs                            自带的 POCO(不引 Godot)
  Faketorio.Presentation.Core.Tests/
    Faketorio.Presentation.Core.Tests.csproj      xUnit,ProjectReference -> Presentation.Core
    TickAccumulatorTests.cs
    WorldTransformTests.cs
```

**新增(Godot 工程,不进 `Faketorio.sln`,不进 CI):**

```
game/
  project.godot                                  Godot 4.5.1 Mono;InputMap:map_toggle=M、pan=中键、zoom=滚轮
  Game.csproj                                    <ProjectReference> ../sim/Faketorio.Sim + ../presentation/Faketorio.Presentation.Core
  Main.tscn                                       根 Node2D + Camera2D + WorldView + BuildController 节点
  SimHost.cs                                      autoload 单例
  CameraController.cs
  ICameraTarget.cs + PlayerCameraTarget.cs
  WorldView.cs
  BuildController.cs
  RenderPalette.cs                                prototype 类型 -> 颜色/尺寸 的占位表
```

**改动(sim 层,唯一一处):**

| 文件 | 改动 |
|---|---|
| `sim/Faketorio.Sim/World/ResourceGrid.cs` | 新增 `public ResourceCell PeekResourceAt(int x, int y)` —— 已生成 chunk 直接读;未生成则**临时 `Generate` 一个瞬时 `ResourceChunk` 读完丢弃,不进 `_chunks`、不置 `_keysDirty`**。纯新增,不动 `GetResourceAt` / `Extract` / `WriteState` / `Ensure`。 |
| `sim/Faketorio.Sim.Tests/ResourceGridTests.cs` | 加 `PeekResourceAt` 的 3 个断言(见 §7)。 |

**改动(CI):**

| 文件 | 改动 |
|---|---|
| `.github/workflows/ci.yml` | `test` job 的 `dotnet test sim/Faketorio.Sim.Tests -c Release` 改成 `dotnet test Faketorio.sln -c Release`(一次跑全部测试项目,顺带纳入 P12/P13 已有的和新的 `Presentation.Core.Tests`)。`bench` job 不变。 |
| `Faketorio.sln` | 加入 `Faketorio.Presentation.Core` + `Faketorio.Presentation.Core.Tests`。**不**加入 `game/Game.csproj`(Godot 自管,加进 sln 会让 `dotnet build Faketorio.sln` 依赖 Godot .NET SDK,破坏 CI)。 |
| `.gitignore` | 已有 `game/.godot/` 和 `*.uid`。补 `game/.mono/`、`game/bin/`、`game/obj/`(若 Godot 生成)。 |

## 3. `Faketorio.Presentation.Core`

### 3.1 `TickAccumulator`

```csharp
namespace Faketorio.Presentation.Core;

// 把变帧率的 delta 秒累积,吐出这一帧要跑几个固定步长的 sim tick。
public sealed class TickAccumulator
{
    public const double TickSeconds = 1.0 / 60.0;
    public int MaxCatchUpTicks { get; init; } = 5;

    private double _acc;

    // 返回 [0, MaxCatchUpTicks] 个要执行的 tick;超出的累积时间被丢弃(不追实时)。
    public int Advance(double deltaSeconds)
    {
        _acc += deltaSeconds;
        int n = 0;
        while (_acc >= TickSeconds && n < MaxCatchUpTicks) { _acc -= TickSeconds; n++; }
        if (_acc >= TickSeconds) _acc = _acc % TickSeconds;   // 夹紧:丢弃追不上的部分
        return n;
    }

    // 距下一个 tick 的分数进度 [0, 1),给未来 lerp 用;v1 不消费。
    public double Alpha => _acc / TickSeconds;
}
```

### 3.2 `WorldTransform`

sim 坐标单位:tile 用 `int`;格内偏移用**亚格**(`Player.X/Y` 是亚格;`BeltLine.TileSubTiles = 256`)。`WorldTransform` 只做坐标换算,参数由 `CameraController` 每帧喂入。

```csharp
namespace Faketorio.Presentation.Core;

public readonly record struct Vec2(double X, double Y);
public readonly record struct RectI(int MinX, int MinY, int MaxX, int MaxY);   // 含 Min,不含 Max

public sealed class WorldTransform
{
    public const int SubTilesPerTile = 256;   // 与 BeltLine.TileSubTiles 一致

    public double PixelsPerTile { get; set; }
    public Vec2 CameraCenterTile { get; set; }   // 相机中心的 world tile 坐标(可含小数)
    public Vec2 ViewportSizePx { get; set; }

    public Vec2 WorldSubToScreen(long subX, long subY)
    {
        double tileX = subX / (double)SubTilesPerTile;
        double tileY = subY / (double)SubTilesPerTile;
        return new Vec2(
            (tileX - CameraCenterTile.X) * PixelsPerTile + ViewportSizePx.X / 2,
            (tileY - CameraCenterTile.Y) * PixelsPerTile + ViewportSizePx.Y / 2);
    }

    public Vec2 TileToScreen(int tileX, int tileY)
        => WorldSubToScreen((long)tileX * SubTilesPerTile, (long)tileY * SubTilesPerTile);

    public (int TileX, int TileY) ScreenToTile(Vec2 screenPx)
    {
        double tileX = (screenPx.X - ViewportSizePx.X / 2) / PixelsPerTile + CameraCenterTile.X;
        double tileY = (screenPx.Y - ViewportSizePx.Y / 2) / PixelsPerTile + CameraCenterTile.Y;
        return ((int)Math.Floor(tileX), (int)Math.Floor(tileY));
    }

    // 可见 tile 矩形 + marginTiles 圈边距(骑边缘的大 footprint 实体 / 传送带物品 / tween 稳定)。
    public RectI VisibleTileRect(int marginTiles = 3)
    {
        var tl = ScreenToTile(new Vec2(0, 0));
        var br = ScreenToTile(ViewportSizePx);
        return new RectI(tl.TileX - marginTiles, tl.TileY - marginTiles,
                         br.TileX + marginTiles + 1, br.TileY + marginTiles + 1);
    }
}
```

（`game/` 里 `Vec2 ↔ Godot.Vector2` 各写一个 2 行的转换扩展方法。）

## 4. `SimHost`(autoload 单例,`game/`)

- 构造:`_dataPath = ProjectSettings.GlobalizePath("res://../data/base")`(`game/` 在 repo 根下 → `res://..` = repo 根);`Sim = new Simulation(PrototypeLoader.LoadFromDirectory(_dataPath), Seed)`。`Seed` 是一个 `[Export] long`(默认一个固定值)。
- `_Ready()`:`SubmitStartupScene()` —— 一段硬编码的 `Submit(PlaceEntity ...)`:几台电力采矿机 + 一段传送带 + 一台熔炉 + 一台机械臂 + 一个箱子 + 一根电线杆 + 一台发电机 + `SetFuelBufferJ` 灌燃料(直接调 `Sim.ElectricGrid.SetFuelBufferJ`,和 bench 的 `ScenarioBuilder` 同款一次性建造期注入),让画面上一开始就有个在运转的小工厂。这段只在 `_Ready` 跑一次。
- `_Process(double delta)`:`for (int n = _acc.Advance(delta); n-- > 0;) Sim.Step();`。
- 公开:`Simulation Sim { get; }`、`double Alpha => _acc.Alpha`、`void Submit(in Command c) => Sim.Submit(c)`、`long Seed { get; }`。
- **唯一调 `Sim.Step()` 的地方。**

## 5. `CameraController`(`Camera2D`,`game/`)

```csharp
public enum CameraMode { Follow, Free }
public interface ICameraTarget { (long SubX, long SubY) WorldSub { get; } }
// PlayerCameraTarget: WorldSub => (SimHost.Sim.Player.X, SimHost.Sim.Player.Y)
```

- `CameraMode Mode`(默认 `Follow`)、`ICameraTarget? FollowTarget`(v1 注入 `PlayerCameraTarget`)。
- `M` 键 toggle 模式。切到 `Follow` 时用一个短 tween 把相机平滑插回目标;切到 `Free` 时保持当前位置。
- **`Follow`**:每帧相机中心 = `FollowTarget.WorldSub` 换算的位置;缩放锁在近景窄区间(如 `pixelsPerTile ∈ [32, 64]`),滚轮在区间内微调;禁止拖拽平移。
- **`Free`**:中键按住拖拽平移(无边界);滚轮缩放放宽(如 `pixelsPerTile ∈ [4, 64]`)。
- 每帧把 `PixelsPerTile` / `CameraCenterTile` / `ViewportSizePx` 写进一个共享的 `WorldTransform` 实例(`WorldView` 和 `BuildController` 读同一个)。
- 节点顺序:`Main.tscn` 里 `CameraController` 在 `WorldView` 之前 → `WorldView._Draw` 读到本帧更新过的变换。
- 公开:`WorldTransform Transform { get; }`、`CameraMode Mode { get; }`。

## 6. `WorldView`(`Node2D`,立即模式 `_Draw`)

- `_Process`:`QueueRedraw()`。
- `_Draw()`:`var vis = _cam.Transform.VisibleTileRect(marginTiles: 3);` 然后按序绘制(后画盖前画),全部剔除到 `vis`:

  1. **地块底色**:`vis` 内每格一个浅色 `DrawRect`。
  2. **网格 gizmo**(`ShowGrid`,默认 `true`):沿 tile 边界 `DrawLine`;每 8 格中线加粗、每 32 格(chunk)再加粗;原点画十字。`PixelsPerTile < 6` 时不画(线太密)。
  3. **矿脉**:`vis` 内每格 `SimHost.Sim.Resources.PeekResourceAt(x, y)`,非空 → 按矿 proto 上色的半透 `DrawRect`。`WorldView` 自己维护一个显示侧 chunk 缓存(`Dictionary<long, ResourceCell[]>`,和 sim 的 `_chunks` 无关,不喂哈希),避免每帧对同一虚拟 chunk 反复 `Peek`。
  4. **实体**:遍历 `SimHost.Sim.Entities`(`Capacity` / `IsAliveAtIndex` / `GetAtIndex → EntityData`),`X/Y`(tile)落在 `vis` 内的:按 `Prototypes.GetById(ProtoId)` 的运行时类型查 `RenderPalette` 取颜色,画一个 `TileWidth × TileHeight` 大的实心 `DrawRect`;再按 `Rotation`(0/1/2/3 = 北/东/南/西)在实体中心画一个小三角表示朝向。传送带单独一档颜色 + 沿 `Direction` 的箭头。
  5. **传送带上物品**:遍历 `SimHost.Sim.Belts`(`Capacity` / `GetAtIndex → BeltLine`),每条 `BeltLine` 的 `LaneA` / `LaneB` 各 `ToAbsolutePositions()`,`LeadingEdgeSubTiles` 沿 `Tiles` 序列(`Tiles[0]` 是出口)反算 world 亚格 → 屏幕,画小方块(按 `ItemProtoId` 上色)。lane A/B 给一个固定垂直偏移分开。
  6. **玩家**:`Sim.Player.X/.Y`(亚格)→ 屏幕,画一个显眼圆 + 朝向。
  7. **光标格高亮**:`BuildController` 暴露当前 hover tile 和"上一命令是否被拒";描边该 tile,被拒则描红一帧。

- `RenderPalette`:`game/` 里一张手填的表(`prototype 运行时类型 → (Color, 说明)` 或按 `name` 前缀)。**无 sprite / 贴图 / sprite sheet。**
- `_Draw` 内不分配(复用 `WorldTransform`、复用绘制用的临时数组)。

## 7. 测试

### 7.1 `Faketorio.Presentation.Core.Tests`(xUnit,进 CI)

**`TickAccumulatorTests`:**
- 匀速 `Advance(1/60)` 反复 → 每次返回 1,`Alpha` 在 0 附近。
- `Advance(1/120)` 两次 → 第一次 0、第二次 1;中间 `Alpha ≈ 0.5`。
- `Advance(1.0)`(1 秒,远超上限)→ 返回 `MaxCatchUpTicks`(5),之后 `_acc < TickSeconds`(追不上的部分被丢弃)。
- `Advance(0)` → 0,`Alpha` 不变。

**`WorldTransformTests`:**
- `WorldSubToScreen` / `ScreenToTile` 往返:随机 tile → `TileToScreen` → `ScreenToTile` 回到原 tile(格中心取样)。
- 不同 `PixelsPerTile`(4 / 32 / 64)下,视口中心的屏幕坐标映射回相机中心 tile。
- `VisibleTileRect(margin)`:视口 `800×600` @ `32 px/tile`、相机中心 `(0,0)` → 矩形约 `[-12-3, 12+3]`;`margin` 参数确实扩大矩形。
- `ScreenToTile` 的 `Math.Floor` 边界:`x = 31.9 px @ 32px/tile, center (0,0)` → tile 0;`x = 32.1` → tile 1。

### 7.2 sim 层新增(`Faketorio.Sim.Tests`)

**`ResourceGrid_PeekResourceAt`:**
- 未生成 chunk 上 `PeekResourceAt(x,y)` 的返回值 == 之后 `GetResourceAt(x,y)` 的返回值(内容一致)。
- `PeekResourceAt` 调用前后 `GeneratedChunkCount` 不变;在一个 `Simulation` 上对若干虚拟坐标 `Peek` 后 `ComputeStateHash()` 与 `Peek` 之前**完全相同**(证明零副作用)。
- 已生成 chunk(先 `GetResourceAt` 触发)上 `PeekResourceAt` == `GetResourceAt`。

### 7.3 回归(必须继续绿)

- 全套测试(现 ~433 + P14 新增约 10):`dotnet test Faketorio.sln -c Release` 全绿。
- `dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --golden bench/golden.json` → `exit 0, gate PASS`(`PeekResourceAt` 是纯新增,不该动任何哈希)。

### 7.4 Godot 工程 `game/`(手动验收清单,写进实现计划)

1. Godot 编辑器打开 `game/`,F5 → 无报错;看到网格 + 启动场景摆的小工厂 + 玩家。
2. 传送带上有物品在移动(sim 在 tick)。
3. `Free` 模式:滚轮缩放、中键拖拽平移正常。`M` → `Follow`:相机平滑回到玩家并锁定。
4. 左键在空光标格 → 下一帧出现一个箱子;右键该格 → 消失。
5. 左键点已被占的格 → 命令被拒、光标闪红、世界不变(`RejectedCommandCount` +1)。
6. `Free` 模式拖到远处虚拟区域 → 看到程序化矿脉(经 `PeekResourceAt`);此前/此后各打一次 `Sim.ComputeStateHash()`,**相同**(渲染读世界无副作用)。

### 7.5 不做

Godot 场景的自动化测试(gdUnit4 等),留到表现层长大。

## 8. 非目标 + 未来接缝

**非目标:**

- **视觉插值(lerp)**:v1 snap;`TickAccumulator.Alpha` 只预留不消费。lerp 是独立子项(要解决"上一 tick 状态 + 传送带物品跨 tick 身份匹配")。
- **完整命令 UI**:无快捷栏、背包/机器面板、旋转命令、实体选择器。`BuildController` 硬编码单一 proto。
- **玩家控制**:WASD 走玩家 + 相机跟随移动的玩家,是下一个子项。v1 玩家静止。
- **真美术**:无 sprite / 贴图 / sprite sheet / 动画帧。全占位色块。"骨骼变换 vs 预渲染帧表"的选择留到美术子项(它决定帧资产数量,与 snap-vs-lerp 无关)。
- **保留模式渲染**:无 `TileMapLayer` / `MultiMesh` / 每实体节点。立即模式 `_Draw` 到瓶颈再迁移。
- **战争迷雾 / 雷达 / charting**:需要 sim 层不存在的勘测状态,是独立 sim 子项(阶段 B 更配)。v1 世界完全可见。
- **抽象地图渲染**(缩太远换廉价画法):依赖 charting 数据,和战争迷雾同源。
- **导出打包**:v1 只从 Godot 编辑器跑;`data/` 打进导出包、导出态 `data` 路径解析,留到要出 build 时。
- **`ResourceGrid.WriteState` 只序列化脏 chunk**:会改 `bench/golden.json`,单独做;v1 用 `PeekResourceAt` 绕过。
- **多进程 / sim 独立线程**:单进程,`_Process` 里同步 `Step()`。不做 pause 感知、不做 sim 落后实时的告警。

**未来接缝(v1 结构里留好的口子):**

| 接缝 | 将来接什么 |
|---|---|
| `CameraController.FollowTarget : ICameraTarget?` | 跟随火车 / remote view / 跳到告警位置 —— 换目标,不加模式 |
| `WorldTransform.VisibleTileRect(marginTiles)` | 保留模式迁移:"视口+边距、跨 chunk 边界才刷新" |
| `TickAccumulator.Alpha` | 视觉插值 lerp |
| `WorldView` 的"可见矩形 → 画什么" | 战争迷雾 / charting:已勘测从快照画、未勘测画空 |
| `WorldView` + `RenderPalette` 的类型→颜色表 | 换成类型→sprite 表 = 美术子项 |
| `CameraController` `Free` 的远缩放阈值 | 抽象地图渲染切换 |

## 9. 全局约束(实现计划照抄进 Global Constraints)

- **只读**:`WorldView` / `CameraController` / `BuildController` 只调 sim 读方法和 `SimHost.Submit`,绝不直接改任何 sim 状态。世界变更一律走 `Submit(Command)`。
- **无副作用读**:渲染器读世界不得触发 sim 状态变化 / 影响 `ComputeStateHash`。`ResourceGrid` 加 `PeekResourceAt`(不进 `_chunks`);`WorldView` 只用 `Peek`。`WorldGrid.GetEntityAt` 本就无副作用可直接用。
- **`PeekResourceAt` 是纯新增**:不改 `GetResourceAt` / `Extract` / `Ensure` / `WriteState` 的任何现有行为;全套 sim 测试和 `bench/golden.json` 实现前后不变。
- sim 层不引入 `float` / `double`(`PeekResourceAt` 内不需要)。表现层的 `double`(累加器、坐标)只在 `Presentation.Core` 和 `game/`,不回流 sim。
- **tick 语义**:命令经 `Submit` 在下一个 `Sim.Step()` 开头按提交顺序统一 apply(`Simulation` 已实现,不改)。`Sim.Step()` 的调用次数由壁钟 delta 决定,与确定性铁律("同 seed + 同命令序列 → 同哈希")正交。
- `Faketorio.Presentation.Core` 零 Godot 依赖、零其它依赖(只 net8.0 BCL + xUnit for tests)。`game/Game.csproj` 只依赖 `Faketorio.Sim` + `Faketorio.Presentation.Core` + Godot SDK。
- `game/` 不进 `Faketorio.sln`、不进 CI。`Presentation.Core[.Tests]` 进 sln + CI `test` job;`test` job 命令改为 `dotnet test Faketorio.sln -c Release`。
- CI actions / 平台不变(`ubuntu-latest`、`actions/*@v4`、`dotnet 8.0.x`)。
- 提交信息结尾带 `Co-Authored-By:` / `Claude-Session:` trailer。

## 10. 开放项(留给实现计划)

- Godot 4.5.1 Mono 生成的 `.csproj` 文件名(`Game.csproj` 还是按文件夹名 `game.csproj`)、`project.godot` 的 `[dotnet]` 段配置、autoload 注册方式。
- `Camera2D` 的 `zoom` 语义(Godot 4 里 `zoom` 增大 = 放大)与 `PixelsPerTile` 的换算关系。
- `SubmitStartupScene` 摆的小工厂的确切坐标 / prototype / 数量(要能自洽运转:采矿机脚下有矿、发电机灌了燃料、机械臂够得着)。参考 bench 的 `IronUnitPart` / `PowerDistrictPart` 的建造手法。
- `data/base` 路径:编辑器态 `res://../data/base` 确认可用;若 Godot 的 CWD 或 `GlobalizePath` 行为不符,回退方案(`OS.GetExecutablePath` 附近找 / 环境变量)。
- `RenderPalette` 的具体配色。
- `Presentation.Core` 的 `Vec2`/`RectI` 是自带还是复用 `System.Numerics.Vector2`(倾向自带 `double` 版,避免 `float`)。
