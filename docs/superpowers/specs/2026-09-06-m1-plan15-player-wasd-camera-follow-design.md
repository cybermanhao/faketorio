# M1 Plan 15 — Player WASD Control + Camera Follows Moving Player (Design Spec)

日期: 2026-09-06
状态: 已与用户确认
配套参考:
- [`docs/superpowers/specs/2026-09-06-m1-plan14-godot-presentation-v1-design.md`](2026-09-06-m1-plan14-godot-presentation-v1-design.md) — 表现层 v1(P14),本子项在其基础上扩展
- [`docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`](2026-09-02-m1-remaining-roadmap.md) — "表现层后续" 拆分(P15 是第一项)

## 1. 目标

给 P14 表现层加**玩家操控**:键盘 WASD 八向行走、相机在 Follow 模式下自动跟随移动的玩家、Free(地图)模式下 WASD 平移相机、外加一个最小手挖(按住键对光标格 `MineStart`/`MineStop`)。纯表现层改动,**sim 层零改动** —— 玩家移动 / 手挖的模拟逻辑 P5 已全部实现,本子项只接线输入 → 命令。

非目标(留给后续 P):视觉插值 lerp(P16)、背包 / 合成 / 机器面板 UI(P18)、真美术(P19)、可配置键位 UI、走进水/实体的拒绝反馈。

## 2. 现有代码事实(照此用,勿猜)

### sim 侧(P5 已实现,不改)

- `Player`(`sim/Faketorio.Sim/Player/Player.cs`):`X` / `Y`(子格 int,1 tile = 256)、`WalkDir`(byte 0–7)、`Walking`(bool)、`Mining`(bool)、`MineTargetX/Y`、`MineProgress`、`Inventory` 全部 public 可读。
- `Player.WalkDelta(byte dir, int speed)`:八向单位向量,`0=北(-Y)`,顺时针;对角分量取满速。`1=东北 2=东 3=东南 4=南 5=西南 6=西 7=西北`。
- `PlayerPrototype`:`WalkSpeedSubTilesPerTick`(默认 38 ≈ 0.15 tile/tick)、`ReachSubTiles`。
- 命令(`CommandType`,`Simulation.ApplyCommand`):
  - `MovePlayer`:`command.Rotation` = 八向 dir(`> 7` 拒绝并计数)。**持久状态**:调 `Player.SetWalk(dir)` 设 `Walking=true`+`WalkDir=dir`,不是每 tick 脉冲。
  - `StopPlayer`:`Player.StopWalk()`。
  - `MineStart`:`command.X` / `command.Y` = 目标格。调 `Player.SetMineTarget(x,y)`(仅当格变化才清零 `MineProgress`)。
  - `MineStop`:`Player.StopMining()`。
- `Simulation.PlayerWalk()`:`Walking` 时每 tick 按 `WalkDelta` 推进;`World.GetEntityAt(nx>>8, ny>>8).IsValid` → **整步拒绝**(不滑墙)。
- `Simulation.PlayerMine()`:`Mining` 时每 tick —— reach 检查(玩家点到目标格中心欧氏距离 `> ReachSubTiles` 直接 return)、优先实体后矿脉、`MiningTimeTicks` 阈值累进、背包满停在阈值、完成后实体销毁 / 矿脉 `Extract(1)` 继续挖。**表现层不需要复刻任何一条**,发命令即可。

### 表现层侧(P14 已实现)

- autoload `SimHost`:持唯一 `Simulation`,`_Process` 按 1/60 步长 `Step()`,`Submit(in Command)`,`Alpha => _acc.Alpha`。
- `game/CameraController.cs`(`partial class CameraController : Node`):`CameraMode Mode`(`Follow` 默认 / `Free`),`WorldTransform WorldXform`,`ICameraTarget? FollowTarget`,私有 `Vector2 _centerTile`。`M` 键(InputMap `map_toggle`)切模式。`Free` 中键拖拽平移 `_centerTile -= mm.Relative / _ppt`,滚轮缩放 `_ppt ∈ [FreeMinPpt=6, FreeMaxPpt=64]`。`Free→Follow` 有 sine ease-out 回弹(`_snapping` 标志)。每 `_Process` 写 `WorldXform.{PixelsPerTile, CameraCenterTile, ViewportSizePx}`。
- `game/ICameraTarget.cs`:`interface ICameraTarget { (long SubX, long SubY) WorldSub { get; } }`。
- `game/PlayerCameraTarget.cs`:`sealed class PlayerCameraTarget(SimHost host) : ICameraTarget { WorldSub => (host.Sim.Player.X, host.Sim.Player.Y); }`。已经是 `Follow` 的目标 —— **玩家一动,`Follow` 模式相机就跟,无需额外代码**。
- `game/BuildController.cs`(`Node`):鼠标左键放 wooden-chest / 右键拆(经 `Submit`)。拥有"光标格"计算逻辑(屏幕像素 → `WorldTransform.ScreenToTile`)。
- `game/WorldView.cs`(`Node2D`):立即模式 `_Draw`。已画:地面、网格、矿脉、实体占位、传送带物品、玩家圆点(`DrawCircle` 半径 `min(ppt*0.3, 20)` + `DrawArc` 描边)、光标格高亮 + 拒绝闪红、HUD 模式文字。
- `game/project.godot`:autoload `SimHost`;InputMap `map_toggle` = M;`default_clear_color` 深色。
- `Presentation.Core`:`WorldTransform`(唯一坐标映射,`ScreenToTile(Vec2)->(int,int)` 等)、`TickAccumulator`(`Alpha` 已就绪但 P15 不用)。

## 3. 架构

新增一个节点 + 扩两个已有节点,**无 sim 改动**。

```
Main (Node2D)
├── CameraController (Node)        ← 扩:Free 模式 WASD 平移相机
├── PlayerInputController (Node)   ← 新:Follow 模式 WASD→移动命令、按键手挖
├── BuildController (Node)         ← 不改
└── WorldView (Node2D)            ← 扩:玩家朝向刻度、手挖时光标格 reach 着色
```

### 3.1 `game/PlayerInputController.cs`(新)

`partial class PlayerInputController : Node`。`[Export]` 或 `_Ready` 里拿到 `SimHost`(autoload)与兄弟节点 `CameraController` 的引用(`GetNode`)。

**移动(每 `_Process`)**:
- 仅当 `_camera.Mode == CameraMode.Follow` 时处理移动;否则跳过(Free 模式 WASD 归相机)。
- 读四个 InputMap 动作 `player_up/down/left/right` 的 `Input.IsActionPressed` 布尔。
- 纯函数 `WalkInput.Resolve(bool up, bool down, bool left, bool right) -> int?`(见 §5)把组合映射成八向 dir 或 `null`(无输入 / 对向抵消)。
- 维护 `int? _lastSentDir`(`_lastSentDir == -1` 表示"已发过 StopPlayer / 初始态")。
  - 解析结果 `dir` 与 `_lastSentDir` 不同 且 非 null → `Submit(MovePlayer(dir))`,`_lastSentDir = dir`。
  - 解析结果 null 且 `_lastSentDir` 不是"已停" → `Submit(StopPlayer())`,记为已停。
- **不每帧发命令**,只在方向变化 / 起步 / 停步时发。

**手挖(每 `_Process`)**:
- 同样**仅 `Follow` 模式处理**;进入 `Free` 模式时若 `_mineActive` 则先 `Submit(MineStop())` 收尾(手挖是"站在原地对脚边的格子操作",地图模式下没有意义,且切模式时鼠标要用于拖拽)。
- 读 InputMap 动作 `player_mine`(hold)。
- 光标格 = 复用与 `BuildController` 同一套换算:`WorldTransform.ScreenToTile(mousePos)`(把这段抽成 `WorldView` 或一个静态 helper 供两个 controller 共用,避免两份实现漂移 —— 见 §4)。
- 状态机:
  - 键按下沿(本帧按、上帧没按)→ `Submit(MineStart(cx, cy))`,记 `_mineActive = true`、`_mineTile = (cx,cy)`。
  - 键持续按住 且 光标格变化 → `Submit(MineStart(cx, cy))`(sim 的 `SetMineTarget` 仅在格变化时清零进度,幂等安全),更新 `_mineTile`。
  - 键松开沿 → `Submit(MineStop())`,`_mineActive = false`。
- 不做 reach / 有没有矿的判断 —— sim `PlayerMine()` 自己会 no-op。表现层只负责"意图"。

**模式切换**:不监听、不干预。`Follow→Free` 切换时**不发 `StopPlayer`** —— 玩家保持当前 `WalkDir`/`Walking`(用户确认的行为:"起步后切地图模式四处看")。`_lastSentDir` 也不重置;切回 `Follow` 时若 WASD 状态和 `_lastSentDir` 不一致,下一帧的常规 diff 逻辑自然纠正。

### 3.2 `game/CameraController.cs`(扩)

`Free` 模式下,除已有的中键拖拽,新增 WASD 平移:
- 每 `_Process`,`Mode == Free` 且非 `_snapping` 时:读 `player_up/down/left/right`,合成屏幕方向向量 `(-left+right, -up+down)`。
- `_centerTile += dirVec * PanTilesPerSecond * (float)delta`,其中 `PanTilesPerSecond` 是常量(建议 ~18,按 tile/秒;屏幕手感随缩放变 —— 拉远时每秒扫过更多屏幕,可接受;若要恒定屏幕速度可改成 `* (BaselinePpt / _ppt)`,v1 先用固定 tile 速度)。
- 不新增 InputMap 动作 —— 与 `PlayerInputController` 读同一批 `player_*` 动作;靠 `Mode` 二选一,互不重叠(`Follow` 时相机忽略 WASD、`PlayerInputController` 处理;`Free` 时反之)。

`Follow` 模式:**一行不改**。已有的"每帧 `_centerTile = FollowTarget.WorldSub` 对应 tile"逻辑会随 `Player.X/Y` 变化自动跟随。

### 3.3 `game/WorldView.cs`(扩)

- **玩家朝向刻度**:画完玩家圆点后,若 `sim.Player.Walking`,从圆心沿 `Player.WalkDir` 方向(用 `Player.WalkDelta(dir, 1)` 或本地八向表)画一条短线,长度 ~`0.6 × dotRadius`,亮色。`Walking == false` 不画。
- **手挖光标格着色**:P14 已画光标格高亮。P15 加:当 `player_mine` 按住时,算玩家点到光标格中心的距离(`WorldTransform` 世界子格坐标 + 整数或浮点距离均可,这里是纯显示不影响 hash),与 `sim` 的 `PlayerPrototype.ReachSubTiles`(只读)比 —— 在 reach 内用一种 tint,超出用另一种(灰/红),给玩家"够不够得着"的反馈。`PlayerInputController` 不依赖这个判断,纯 UI。

### 3.4 `game/project.godot`(扩)

InputMap 增加动作(默认键):

| 动作 | 默认键 |
|---|---|
| `player_up` | W |
| `player_down` | S |
| `player_left` | A |
| `player_right` | D |
| `player_mine` | E(hold) |

`map_toggle` = M 保留。

## 4. 光标格换算去重

`BuildController` 和 `PlayerInputController` 都要"鼠标屏幕坐标 → 世界 tile"。P14 里这段在 `BuildController` 内。P15 抽成单一实现,二者共用,避免漂移。方案(实现期择一,倾向 A):

- **A**:`WorldView` 暴露 `public (int X, int Y) CursorTile => WorldXform.ScreenToTile(GetViewport().GetMousePosition().ToCore())`(或等价),两个 controller `GetNode` 到 `WorldView` 读它。
- **B**:静态 helper `PlayerInput.CursorTile(WorldTransform t, Vector2 mousePx)`,`BuildController` 也改用它。

无论哪种,`BuildController` 的现有行为不变(只是换到共用实现)。

## 5. `Presentation.Core` 纯逻辑 + 测试

把八向组合解析抽成纯函数,放 `presentation/Faketorio.Presentation.Core/`(零 Godot 依赖,xUnit 可测):

```csharp
public static class WalkInput
{
    // 返回八向 dir(0=北,顺时针,与 Player.WalkDelta 一致)或 null(无输入 / 上下、左右同时按导致该轴抵消且无剩余轴)。
    public static int? Resolve(bool up, bool down, bool left, bool right);
}
```

规则:
- `up^down` 得纵向分量(−1 上 / +1 下 / 0),`left^right` 得横向分量,同时按的对向抵消为 0。
- 两分量都 0 → `null`。
- 否则映射到八向:`(0,-1)→0 (1,-1)→1 (1,0)→2 (1,1)→3 (0,1)→4 (-1,1)→5 (-1,0)→6 (-1,-1)→7`。

**xUnit 用例**(`presentation/Faketorio.Presentation.Core.Tests/WalkInputTests.cs`):
- 全 16 种布尔组合 → 期望 dir。重点:`up+down` 且无横向 → `null`;`up+down+right` → `2`(东);四键全按 → `null`;单键 → 0/2/4/6;两相邻键 → 1/3/5/7。

其余(节点接线、命令时序、相机平移、朝向刻度)是 Godot 侧,靠 headless 编译-加载 + 人工 F5。

## 6. 命令时序 / 确定性

- 命令经 `SimHost.Submit` 进队,`Simulation` 在下一个 `Step()` 开头按提交顺序统一 apply(P14 已确认,不改)。
- `PlayerInputController` 每 `_Process`(渲染帧,可能 > 或 < 60Hz)最多提交少量命令(方向变 / 起停 / 手挖状态变)。一帧内多个 `Step()`(追帧)时,这些命令在第一个 `Step()` 生效,后续 `Step()` 沿用持久状态 —— 与 P5 的 `Walking`/`Mining` 持久语义一致。
- 表现层读 `Player.X/Y/WalkDir/Walking` 和 `ReachSubTiles` 全是只读,不影响 `ComputeStateHash`。
- **golden 不受影响**(bench `ScenarioSentinel` 不含玩家输入;P15 无 sim 改动)。

## 7. 全局约束(实现期照此)

- **只读**:`PlayerInputController` / `CameraController` / `WorldView` 只调 sim 读方法 + `SimHost.Submit`;世界变更一律走 `Submit(Command)`。不直接碰 `Player` 状态。
- sim 层**零改动**。`Presentation.Core` 仍零 Godot 依赖(新增 `WalkInput` 是纯 BCL)。
- `game/` 仍不进 `Faketorio.sln` / 不进 CI;`Presentation.Core[.Tests]` 进 sln + CI。
- `WorldTransform` 仍是唯一坐标映射,不引入第二套。P14 定的"渲染路径无 `Camera2D`"不回退。
- 提交信息结尾带:
  ```
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_016Z7a7Yd8hWCT3UaWXorJQW
  ```

## 8. 验收(§人工 F5)

headless `--quit-after` 编译-加载过后,人工:

1. Follow 模式:WASD 八向走,松开停;斜向按两键走对角;相机平滑跟随玩家不出屏。
2. 玩家圆点行走时有朝向短线,停下消失。
3. 按 M 切 Free:WASD 改为平移相机,玩家保持之前的行走状态(若之前在走,继续朝原方向走 —— 可走出画面);中键拖拽 + 滚轮缩放仍工作。
4. 再按 M 切回 Follow:相机回弹到玩家;WASD 恢复控制玩家。
5. 站到矿脉旁,把光标放到 reach 内的矿脉格,按住 E:光标格显示 reach 内着色,玩家背包该矿物品数量增长(开 F5 时可加个临时 HUD 打印 `Player.Inventory` 某格,或事后看)。光标移到 reach 外:着色变灰/红,按 E 无效果。
6. 对着一个可挖实体(如自己放的 wooden-chest)按住 E:到 `MiningTimeTicks` 后实体消失、进背包。

## 9. 交付物清单

- `game/PlayerInputController.cs`(新)
- `game/CameraController.cs`(扩:Free WASD 平移)
- `game/WorldView.cs`(扩:朝向刻度 + 手挖 reach 着色;可能 + `CursorTile` 暴露)
- `game/BuildController.cs`(若走 §4 方案 A/B:改用共用光标格换算,行为不变)
- `game/Main.tscn`(加 `PlayerInputController` 子节点)
- `game/project.godot`(InputMap + `player_up/down/left/right/mine`)
- `presentation/Faketorio.Presentation.Core/WalkInput.cs`(新)
- `presentation/Faketorio.Presentation.Core.Tests/WalkInputTests.cs`(新)
