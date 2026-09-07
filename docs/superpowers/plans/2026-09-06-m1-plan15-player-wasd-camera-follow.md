# M1 Plan 15 — Player WASD Control + Camera Follows Moving Player Implementation Plan

> **状态:✅ 已合并进 `main`(本地 `--no-ff`)。** SDD 全 5 任务 + 整分支审查完成;人工 F5 验收通过。整分支审查(opus)1 个 Important(Free 模式 reach 着色虚假提示 → 改用 `sim.Player.Mining` 门控)+ 4 个 folded minor,全部在修复 wave `c66a9f4` 处理,定向复审 clean。sim 零改动,golden 不变。跨 plan 进度看 `docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`。

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire keyboard input in the Godot presentation layer so the player walks 8-way with WASD, the Follow camera tracks the moving player, Free (map) mode pans with WASD, and holding a key hand-mines the cursor tile — all via `Submit(Command)`, zero sim changes.

**Architecture:** One new node `game/PlayerInputController.cs` (Follow-mode: resolves held WASD → 8-way dir via a pure `WalkInput.Resolve` in `Presentation.Core`, emits `MovePlayer`/`StopPlayer` only on change; hold `player_mine` → `MineStart`/`MineStop` on the cursor tile). `game/CameraController.cs` gains Free-mode WASD panning alongside its existing middle-drag. `game/WorldView.cs` gains a `WalkDir` facing tick on the player dot and a reach tint on the cursor tile while mining. `game/project.godot` gains InputMap actions `player_up/down/left/right/mine`.

**Tech Stack:** C# / .NET 8, Godot 4.5.1 Mono (`Godot.NET.Sdk/4.5.1`), xUnit 2.9. `Presentation.Core` zero-dependency; `game/` not in CI (headless compile-load only; human F5 deferred — user away from computer).

**Spec:** [`docs/superpowers/specs/2026-09-06-m1-plan15-player-wasd-camera-follow-design.md`](../specs/2026-09-06-m1-plan15-player-wasd-camera-follow-design.md)

## Global Constraints

- **Read-only presentation:** `PlayerInputController` / `CameraController` / `WorldView` call only sim read methods + `SimHost.Submit`. All world mutation goes through `Submit(Command)`. Never touch `Player` state directly.
- **Zero sim changes.** No file under `sim/` is created or modified by this plan. `bench/golden.json` unchanged; `BenchScenarioTests.GOLDEN_TICK_800` unchanged.
- `Faketorio.Presentation.Core` stays zero-Godot-dependency, zero third-party (net8.0 BCL only; test project adds xUnit). New `WalkInput` is pure BCL.
- `game/` is **not** in `Faketorio.sln`, **not** in CI — verified by `dotnet build game/Game.csproj` + Godot headless load. `Presentation.Core[.Tests]` **are** in `Faketorio.sln` + CI `test` job.
- `WorldTransform` remains the sole coordinate mapping. No `Camera2D` in the render path (P14 decision, do not regress).
- `MovePlayer` command carries the 8-way direction in `Command.Rotation` (byte; sim rejects `> 7`). `Command` fields: `Type, ProtoId, X, Y, Rotation, Count`.
- `CommandType`: `MovePlayer = 3`, `StopPlayer = 4`, `MineStart = 5`, `MineStop = 6` (namespace `Faketorio.Sim.Commands`).
- Sim 8-way dir convention (from `Player.WalkDelta`): `0=N(-Y)`, clockwise — `1=NE 2=E 3=SE 4=S 5=SW 6=W 7=NW`.
- Human F5 acceptance (spec §8) is **deferred** — do not block on it. Per-task: `dotnet build` / xUnit / headless load. Final review runs the golden gate once as a guard.
- Commit message trailer — every `git commit -m` in this plan ends with these two lines verbatim (shown as `<trailer>` in the task commit steps; substitute this exact text):
  ```
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_016Z7a7Yd8hWCT3UaWXorJQW
  ```

## 参考:现有代码事实(照此用,勿猜)

- `game/SimHost.cs`: autoload `/root/SimHost`. `public Simulation Sim { get; }`, `public void Submit(in Command c)`, `public double Alpha`. Sole `Sim.Step()` caller (`_Process`: `for (int n = _acc.Advance(delta); n-- > 0;) Sim.Step();`).
- `game/CameraController.cs`: `partial class CameraController : Node`. `public CameraMode Mode { get; private set; }` (`enum CameraMode { Follow, Free }`). `public WorldTransform WorldXform { get; }`. Private `double _ppt`, `Vector2 _centerTile`, `bool _snapping`, `bool _panning`. `_UnhandledInput` handles `map_toggle`/M, wheel zoom, middle-drag pan (Free only). `_Process(double delta)`: Follow → `_centerTile = TargetTile()` (or eased during `_snapping`); then writes `WorldXform.{PixelsPerTile=_ppt, CameraCenterTile=new Vec2(_centerTile.X,_centerTile.Y), ViewportSizePx}`.
- `game/BuildController.cs`: `partial class BuildController : Node`. `public (int X, int Y) HoverTile { get; private set; }` recomputed each `_Process` as `_cam.WorldXform.ScreenToTile(GetViewport().GetMousePosition().ToCore())`. `public bool LastCommandRejected`. `_Ready` does `GetNode<SimHost>("/root/SimHost")` and `GetNode<CameraController>("../CameraController")`.
- `game/WorldView.cs`: `partial class WorldView : Node2D`. `_Ready` gets `_host`, `_cam` (`../CameraController`), `_build` (`../BuildController`). `_Process`: `FillPendingOreChunks(); QueueRedraw();`. `_Draw` has a first-frame guard `if (t.ViewportSizePx.X < 1 || t.ViewportSizePx.Y < 1) return;` where `t = _cam.WorldXform`. Section 6 draws the player dot:
  ```csharp
  var p = t.WorldSubToScreen(sim.Player.X, sim.Player.Y).ToGodot();
  float pr = Mathf.Min(ppt * 0.3f, 20f);
  DrawCircle(p, pr, new Color("#e8e8e8"));
  DrawArc(p, pr, 0f, Mathf.Tau, 24, new Color(0.1f, 0.1f, 0.1f, 0.9f), 1.5f, true);
  ```
  Section 7 draws the hover tile: `var (hx, hy) = _build.HoverTile; var s = t.TileToScreen(hx, hy).ToGodot(); DrawRect(new Rect2(s, new Vector2(ppt, ppt)), _build.LastCommandRejected ? new Color(1,0.3f,0.3f) : new Color(1,1,1,0.8f), false, 2f);`. `ppt` in `_Draw` is `(float)t.PixelsPerTile`. `sim` is `_host.Sim`.
- `game/Main.tscn`: `[gd_scene load_steps=4 format=3]`, 3 `ext_resource` scripts (ids "1"=CameraController, "2"=BuildController, "3"=WorldView). Root `[node name="Main" type="Node2D"]` with children `CameraController` (`type="Node"`), `BuildController` (`type="Node"`), `WorldView` (`type="Node2D"`).
- `game/project.godot` `[input]` section currently has only `map_toggle` (keycode 77 = M).
- `game/GodotExtensions.cs`: `Vec2.ToGodot()` → `Vector2`; `Vector2.ToCore()` → `Vec2`.
- `Presentation.Core/WorldTransform.cs`: `WorldSubToScreen(long, long) -> Vec2`, `TileToScreen(int, int) -> Vec2`, `ScreenToTile(Vec2) -> (int, int)`, props `PixelsPerTile` (double), `CameraCenterTile` (Vec2), `ViewportSizePx` (Vec2). `const int SubTilesPerTile = 256`.
- `sim` `Player` (read-only from presentation): `int X, int Y` (sub-tiles), `byte WalkDir`, `bool Walking`, `bool Mining`, `int MineTargetX/Y`, `Inventory Inventory`.
- `sim` `PlayerPrototype` (name `"player"`): `int ReachSubTiles` (default 1536 = 6 tiles), `int WalkSpeedSubTilesPerTick` (38). Access: `Sim.Prototypes.Get<PlayerPrototype>("player")`.
- Godot 4 C# input: `Input.IsActionPressed("action_name")` → bool. Actions must exist in `project.godot` `[input]` or `InputMap`. `Input.IsActionJustPressed` / `IsActionJustReleased` for edges.
- `dotnet` test filter: `dotnet test presentation/Faketorio.Presentation.Core.Tests -c Release --filter "FullyQualifiedName~WalkInput"`.
- Headless load: `& "C:\Program Files\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe" --headless --path game --quit-after 240` → expect clean exit 0.
- Golden gate: `dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --golden bench/golden.json` → `exit 0`, `gate PASS`.

---

## File Structure

| File | New/Mod | Responsibility |
|---|---|---|
| `presentation/Faketorio.Presentation.Core/WalkInput.cs` | New | Pure fn: 4 held-key bools → 8-way dir `int?` (null = no input / cancelling). |
| `presentation/Faketorio.Presentation.Core.Tests/WalkInputTests.cs` | New | xUnit `[Theory]` over all 16 bool combos. |
| `game/project.godot` | Mod | Add InputMap actions `player_up/down/left/right` (WASD), `player_mine` (E). |
| `game/PlayerInputController.cs` | New | Follow-mode: WASD → `MovePlayer`/`StopPlayer` on change; hold `player_mine` → `MineStart`/`MineStop` on cursor tile. |
| `game/Main.tscn` | Mod | Add `PlayerInputController` child node (+ its `ext_resource`). |
| `game/CameraController.cs` | Mod | Free-mode: WASD pans `_centerTile` (alongside middle-drag). |
| `game/WorldView.cs` | Mod | Player-dot `WalkDir` facing tick; cursor-tile reach tint while mining. |

Task order: **1** pure logic (fully testable) → **2** input map + movement node + scene wiring → **3** mining on the same node → **4** camera pan → **5** rendering polish. Each task compiles and loads headless on its own.

---

## Task 1: `WalkInput.Resolve` pure function + tests

**Files:**
- Create: `presentation/Faketorio.Presentation.Core/WalkInput.cs`
- Test: `presentation/Faketorio.Presentation.Core.Tests/WalkInputTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Faketorio.Presentation.Core.WalkInput.Resolve(bool up, bool down, bool left, bool right) -> int?` — returns 8-way dir `0..7` (`0=N`, clockwise) or `null` when there is no net movement (no keys, or opposing keys on both axes with nothing left).

- [ ] **Step 1: Write the failing test**

Create `presentation/Faketorio.Presentation.Core.Tests/WalkInputTests.cs`:

```csharp
using Faketorio.Presentation.Core;

namespace Faketorio.Presentation.Core.Tests;

public class WalkInputTests
{
    // args: up, down, left, right, expected (-1 encodes null — xUnit InlineData can't hold int?)
    [Theory]
    [InlineData(false, false, false, false, -1)] // nothing
    [InlineData(true,  false, false, false, 0)]  // N
    [InlineData(true,  false, false, true,  1)]  // NE
    [InlineData(false, false, false, true,  2)]  // E
    [InlineData(false, true,  false, true,  3)]  // SE
    [InlineData(false, true,  false, false, 4)]  // S
    [InlineData(false, true,  true,  false, 5)]  // SW
    [InlineData(false, false, true,  false, 6)]  // W
    [InlineData(true,  false, true,  false, 7)]  // NW
    [InlineData(true,  true,  false, false, -1)] // up+down cancel, no horizontal
    [InlineData(false, false, true,  true,  -1)] // left+right cancel, no vertical
    [InlineData(true,  true,  true,  false, 6)]  // vertical cancels -> W
    [InlineData(true,  true,  false, true,  2)]  // vertical cancels -> E
    [InlineData(true,  false, true,  true,  0)]  // horizontal cancels -> N
    [InlineData(false, true,  true,  true,  4)]  // horizontal cancels -> S
    [InlineData(true,  true,  true,  true,  -1)] // everything cancels
    public void Resolve_MapsHeldKeysToEightWayDir(bool up, bool down, bool left, bool right, int expected)
    {
        int? actual = WalkInput.Resolve(up, down, left, right);
        Assert.Equal(expected == -1 ? (int?)null : expected, actual);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test presentation/Faketorio.Presentation.Core.Tests -c Release --filter "FullyQualifiedName~WalkInput"`
Expected: FAIL — `WalkInput` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

Create `presentation/Faketorio.Presentation.Core/WalkInput.cs`:

```csharp
namespace Faketorio.Presentation.Core;

/// 把四个方向键的按住状态解析成八向行走方向。纯逻辑,零依赖,给 game/ 的
/// PlayerInputController 用。方向约定与 sim 的 Player.WalkDelta 一致:
/// 0=北(-Y),顺时针 —— 1=东北 2=东 3=东南 4=南 5=西南 6=西 7=西北。
public static class WalkInput
{
    /// 返回八向 dir 或 null(无输入,或某轴上下/左右同时按导致抵消且另一轴也无输入)。
    public static int? Resolve(bool up, bool down, bool left, bool right)
    {
        int dx = (right ? 1 : 0) - (left ? 1 : 0);
        int dy = (down ? 1 : 0) - (up ? 1 : 0);   // 屏幕 y 向下:down = +1

        return (dx, dy) switch
        {
            (0, 0)   => null,
            (0, -1)  => 0,
            (1, -1)  => 1,
            (1, 0)   => 2,
            (1, 1)   => 3,
            (0, 1)   => 4,
            (-1, 1)  => 5,
            (-1, 0)  => 6,
            (-1, -1) => 7,
            _ => null,   // 不可达:dx、dy 各 ∈ {-1,0,1},9 种组合已全覆盖
        };
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test presentation/Faketorio.Presentation.Core.Tests -c Release --filter "FullyQualifiedName~WalkInput"`
Expected: PASS — 16 cases.

- [ ] **Step 5: Run the full solution test suite (guard against regressions)**

Run: `dotnet test Faketorio.sln -c Release`
Expected: PASS — all sim + presentation tests green (sim count unchanged from `main`; presentation +16).

- [ ] **Step 6: Commit**

```bash
git add presentation/Faketorio.Presentation.Core/WalkInput.cs presentation/Faketorio.Presentation.Core.Tests/WalkInputTests.cs
git commit -m "feat(presentation): WalkInput.Resolve — 四方向键 → 八向 dir 纯函数

<trailer>"
```

---

## Task 2: InputMap actions + `PlayerInputController` movement + scene wiring

**Files:**
- Modify: `game/project.godot` (`[input]` section)
- Create: `game/PlayerInputController.cs`
- Modify: `game/Main.tscn`

**Interfaces:**
- Consumes: `WalkInput.Resolve(bool,bool,bool,bool) -> int?` (Task 1); `SimHost.Submit(in Command)`, `SimHost.Sim`; `CameraController.Mode`.
- Produces: node `PlayerInputController` under `Main`, sibling of `CameraController`/`BuildController`/`WorldView`. `partial class PlayerInputController : Node` in namespace `Faketorio.Game`. No public members other than Godot overrides — later tasks do not read from it.

- [ ] **Step 1: Add InputMap actions to `game/project.godot`**

In the `[input]` section, after the existing `map_toggle={...}` block, add five actions. Keycodes: W=87, A=65, S=83, D=68, E=69. Match the exact `Object(InputEventKey, ...)` shape already used by `map_toggle` (only `keycode` differs):

```
player_up={
"deadzone": 0.5,
"events": [Object(InputEventKey,"resource_local_to_scene":false,"resource_name":"","device":-1,"window_id":0,"alt_pressed":false,"shift_pressed":false,"ctrl_pressed":false,"meta_pressed":false,"pressed":false,"keycode":87,"physical_keycode":0,"key_label":0,"unicode":0,"location":0,"echo":false,"script":null)
]
}
player_down={
"deadzone": 0.5,
"events": [Object(InputEventKey,"resource_local_to_scene":false,"resource_name":"","device":-1,"window_id":0,"alt_pressed":false,"shift_pressed":false,"ctrl_pressed":false,"meta_pressed":false,"pressed":false,"keycode":83,"physical_keycode":0,"key_label":0,"unicode":0,"location":0,"echo":false,"script":null)
]
}
player_left={
"deadzone": 0.5,
"events": [Object(InputEventKey,"resource_local_to_scene":false,"resource_name":"","device":-1,"window_id":0,"alt_pressed":false,"shift_pressed":false,"ctrl_pressed":false,"meta_pressed":false,"pressed":false,"keycode":65,"physical_keycode":0,"key_label":0,"unicode":0,"location":0,"echo":false,"script":null)
]
}
player_right={
"deadzone": 0.5,
"events": [Object(InputEventKey,"resource_local_to_scene":false,"resource_name":"","device":-1,"window_id":0,"alt_pressed":false,"shift_pressed":false,"ctrl_pressed":false,"meta_pressed":false,"pressed":false,"keycode":68,"physical_keycode":0,"key_label":0,"unicode":0,"location":0,"echo":false,"script":null)
]
}
player_mine={
"deadzone": 0.5,
"events": [Object(InputEventKey,"resource_local_to_scene":false,"resource_name":"","device":-1,"window_id":0,"alt_pressed":false,"shift_pressed":false,"ctrl_pressed":false,"meta_pressed":false,"pressed":false,"keycode":69,"physical_keycode":0,"key_label":0,"unicode":0,"location":0,"echo":false,"script":null)
]
}
```

- [ ] **Step 2: Create `game/PlayerInputController.cs` (movement only)**

```csharp
using Godot;
using Faketorio.Presentation.Core;
using Faketorio.Sim.Commands;

namespace Faketorio.Game;

/// 键盘操控玩家。只读 sim + Submit。
///
/// 移动:仅在 Follow 相机模式处理(Free 模式下 WASD 归 CameraController 平移相机)。
/// sim 的 MovePlayer 是**持久状态**(设 Walking+WalkDir),不是每 tick 脉冲 ——
/// 所以只在解析出的方向变化 / 起步 / 停步时发命令,不每帧发。
///
/// 手挖(Task 3 加):按住 player_mine → 对光标格 MineStart/MineStop。
public partial class PlayerInputController : Node
{
    private SimHost _host = null!;
    private CameraController _cam = null!;

    // 上一次发给 sim 的方向。-1 = 已发 StopPlayer(或初始态),不会重复发 Stop。
    private int _lastSentDir = -1;

    public override void _Ready()
    {
        _host = GetNode<SimHost>("/root/SimHost");
        _cam = GetNode<CameraController>("../CameraController");
    }

    public override void _Process(double delta)
    {
        UpdateMovement();
    }

    private void UpdateMovement()
    {
        if (_cam.Mode != CameraMode.Follow)
            return;   // Free 模式:WASD 归相机,这里不动;也不发 Stop(玩家保持当前行走状态)

        int? dir = WalkInput.Resolve(
            Input.IsActionPressed("player_up"),
            Input.IsActionPressed("player_down"),
            Input.IsActionPressed("player_left"),
            Input.IsActionPressed("player_right"));

        if (dir is int d)
        {
            if (d != _lastSentDir)
            {
                _host.Submit(new Command { Type = CommandType.MovePlayer, Rotation = (byte)d });
                _lastSentDir = d;
            }
        }
        else if (_lastSentDir != -1)
        {
            _host.Submit(new Command { Type = CommandType.StopPlayer });
            _lastSentDir = -1;
        }
    }
}
```

- [ ] **Step 3: Wire `PlayerInputController` into `game/Main.tscn`**

Bump `load_steps` `4` → `5`. Add an `ext_resource` (id `"4"`) and a child node **before** `WorldView` (so `WorldView._Ready`'s `GetNode` siblings are unaffected — order among `Node` siblings does not matter for `GetNode`, but keep the file tidy):

```
[gd_scene load_steps=5 format=3]

[ext_resource type="Script" path="res://CameraController.cs" id="1"]
[ext_resource type="Script" path="res://BuildController.cs" id="2"]
[ext_resource type="Script" path="res://WorldView.cs" id="3"]
[ext_resource type="Script" path="res://PlayerInputController.cs" id="4"]

[node name="Main" type="Node2D"]

[node name="CameraController" type="Node" parent="."]
script = ExtResource("1")

[node name="BuildController" type="Node" parent="."]
script = ExtResource("2")

[node name="PlayerInputController" type="Node" parent="."]
script = ExtResource("4")

[node name="WorldView" type="Node2D" parent="."]
script = ExtResource("3")
```

- [ ] **Step 4: Build the game project**

Run: `dotnet build game/Game.csproj -c Debug`
Expected: `0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Headless load**

Run: `& "C:\Program Files\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe" --headless --path game --quit-after 240`
Expected: clean exit 0, no `SCRIPT ERROR` / `Parse Error` / unresolved `GetNode` in output. (Confirms the new node loads, `_Ready` resolves `/root/SimHost` and `../CameraController`, and the InputMap actions parse.)

- [ ] **Step 6: Commit**

```bash
git add game/project.godot game/PlayerInputController.cs game/Main.tscn
git commit -m "feat(game): PlayerInputController —— Follow 模式 WASD → MovePlayer/StopPlayer

<trailer>"
```

---

## Task 3: Hand-mine on the cursor tile

**Files:**
- Modify: `game/PlayerInputController.cs`

**Interfaces:**
- Consumes: `CameraController.WorldXform` (`WorldTransform`) for `ScreenToTile`; `SimHost.Submit`; `CommandType.MineStart` / `MineStop`.
- Produces: nothing new for later tasks.

- [ ] **Step 1: Add mining state + logic to `PlayerInputController`**

Add fields:

```csharp
    private bool _mineHeld;
    private (int X, int Y) _mineTile;
```

Add to `_Process` after `UpdateMovement();`:

```csharp
        UpdateMining();
```

Add the method:

```csharp
    // 按住 player_mine → 对光标格 MineStart;光标格变了重发(sim 的 SetMineTarget 幂等,
    // 仅在格变化时清零进度);松开发 MineStop。仅 Follow 模式;进 Free 模式若正在挖则收尾。
    // 不判 reach / 有没有矿 —— sim 的 PlayerMine() 自己会 no-op。
    private void UpdateMining()
    {
        if (_cam.Mode != CameraMode.Follow)
        {
            if (_mineHeld) { _host.Submit(new Command { Type = CommandType.MineStop }); _mineHeld = false; }
            return;
        }

        bool held = Input.IsActionPressed("player_mine");
        if (!held)
        {
            if (_mineHeld) { _host.Submit(new Command { Type = CommandType.MineStop }); _mineHeld = false; }
            return;
        }

        var (cx, cy) = _cam.WorldXform.ScreenToTile(GetViewport().GetMousePosition().ToCore());
        if (!_mineHeld || (cx, cy) != _mineTile)
        {
            _host.Submit(new Command { Type = CommandType.MineStart, X = cx, Y = cy });
            _mineHeld = true;
            _mineTile = (cx, cy);
        }
    }
```

(`GetViewport().GetMousePosition().ToCore()` + `WorldXform.ScreenToTile` is the exact same cursor-tile derivation `BuildController._Process` uses for `HoverTile`; reading the shared `WorldXform` transform, not reimplementing logic — no drift risk.)

- [ ] **Step 2: Build the game project**

Run: `dotnet build game/Game.csproj -c Debug`
Expected: `0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Headless load**

Run: `& "C:\Program Files\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe" --headless --path game --quit-after 240`
Expected: clean exit 0, no script errors.

- [ ] **Step 4: Commit**

```bash
git add game/PlayerInputController.cs
git commit -m "feat(game): PlayerInputController 手挖 —— 按住 E 对光标格 MineStart/MineStop

<trailer>"
```

---

## Task 4: Free-mode WASD camera pan

**Files:**
- Modify: `game/CameraController.cs`

**Interfaces:**
- Consumes: same `player_up/down/left/right` InputMap actions (Task 2). No coupling to `PlayerInputController` — `Mode` is the single arbiter (Follow: camera ignores WASD; Free: camera pans, `PlayerInputController` ignores WASD).
- Produces: nothing new.

- [ ] **Step 1: Add the pan constant**

In `CameraController`, near the other `private const` lines, add:

```csharp
    // Free 模式 WASD 平移速度,tile/秒(固定 tile 速度,拉远时每秒扫过更多屏幕,可接受)。
    private const double FreePanTilesPerSecond = 18.0;
```

- [ ] **Step 2: Pan in `_Process` when Free**

In `_Process(double delta)`, before the block that writes `WorldXform.*`, add:

```csharp
        if (Mode == CameraMode.Free && !_snapping)
        {
            float px = (Input.IsActionPressed("player_right") ? 1f : 0f) - (Input.IsActionPressed("player_left") ? 1f : 0f);
            float py = (Input.IsActionPressed("player_down")  ? 1f : 0f) - (Input.IsActionPressed("player_up")   ? 1f : 0f);
            if (px != 0f || py != 0f)
            {
                var d = new Vector2(px, py).Normalized() * (float)(FreePanTilesPerSecond * delta);
                _centerTile += d;
            }
        }
```

(`_snapping` is only ever true in Follow mode, so the `!_snapping` guard is belt-and-suspenders; keep it for clarity.)

- [ ] **Step 3: Build the game project**

Run: `dotnet build game/Game.csproj -c Debug`
Expected: `0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Headless load**

Run: `& "C:\Program Files\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe" --headless --path game --quit-after 240`
Expected: clean exit 0.

- [ ] **Step 5: Commit**

```bash
git add game/CameraController.cs
git commit -m "feat(game): CameraController —— Free 模式 WASD 平移相机

<trailer>"
```

---

## Task 5: Player facing tick + cursor-tile reach tint

**Files:**
- Modify: `game/WorldView.cs`

**Interfaces:**
- Consumes: `sim.Player.{X, Y, WalkDir, Walking}`; `Sim.Prototypes.Get<PlayerPrototype>("player").ReachSubTiles`; `_build.HoverTile`; `Input.IsActionPressed("player_mine")`.
- Produces: nothing (pure rendering).

- [ ] **Step 1: Add a `using` for prototypes**

`game/WorldView.cs` already has `using Faketorio.Sim.Prototypes;` — confirm; if missing, add it.

- [ ] **Step 2: Player facing tick**

In `_Draw`, section 6 (player dot), after the `DrawArc(...)` line and still inside that block, add:

```csharp
            if (sim.Player.Walking)
            {
                // 八向单位向量(屏幕坐标,y 向下),与 sim Player.WalkDelta 一致。
                (float fx, float fy) = sim.Player.WalkDir switch
                {
                    0 => (0f, -1f), 1 => (1f, -1f), 2 => (1f, 0f), 3 => (1f, 1f),
                    4 => (0f, 1f), 5 => (-1f, 1f), 6 => (-1f, 0f), 7 => (-1f, -1f),
                    _ => (0f, 0f),
                };
                var dirv = new Vector2(fx, fy);
                if (dirv != Vector2.Zero)
                    DrawLine(p, p + dirv.Normalized() * (pr * 1.6f), new Color("#ffd24a"), 2f);
            }
```

(`p` and `pr` are the player-dot center and radius already in scope in that block.)

- [ ] **Step 3: Cursor-tile reach tint while mining**

Replace section 7 (hover tile) with a version that tints by reach when `player_mine` is held:

```csharp
        // 7. 光标格高亮 —— 手挖按住时按"够不够得着"着色。
        {
            var (hx, hy) = _build.HoverTile;
            var s = t.TileToScreen(hx, hy).ToGodot();
            var r = new Rect2(s, new Vector2(ppt, ppt));

            Color col;
            if (_build.LastCommandRejected)
            {
                col = new Color(1, 0.3f, 0.3f);
            }
            else if (Input.IsActionPressed("player_mine"))
            {
                int reach = sim.Prototypes.Get<PlayerPrototype>("player").ReachSubTiles;
                long ddx = sim.Player.X - ((long)hx * 256 + 128);
                long ddy = sim.Player.Y - ((long)hy * 256 + 128);
                bool inReach = ddx * ddx + ddy * ddy <= (long)reach * reach;
                col = inReach ? new Color(0.4f, 1f, 0.5f, 0.9f) : new Color(0.6f, 0.6f, 0.6f, 0.7f);
            }
            else
            {
                col = new Color(1, 1, 1, 0.8f);
            }
            DrawRect(r, col, false, 2f);
        }
```

(Reach check mirrors `Simulation.PlayerMine`'s `Isqrt(ddx²+ddy²) > ReachSubTiles` as `ddx²+ddy² <= reach²` — integer, no sqrt, display-only, does not touch the hash.)

- [ ] **Step 4: Build the game project**

Run: `dotnet build game/Game.csproj -c Debug`
Expected: `0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Headless load**

Run: `& "C:\Program Files\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe" --headless --path game --quit-after 240`
Expected: clean exit 0, no draw exceptions in output.

- [ ] **Step 6: Golden gate (guard — plan touches no sim, expect trivially unchanged)**

Run: `dotnet run -c Release --project sim/Faketorio.Sim.Bench -- --golden bench/golden.json`
Expected: `exit 0`, `gate PASS`. (If this fails, something outside the plan's stated scope changed — stop and investigate.)

- [ ] **Step 7: Commit**

```bash
git add game/WorldView.cs
git commit -m "feat(game): WorldView —— 玩家朝向刻度 + 手挖光标格 reach 着色

<trailer>"
```

---

## Definition of Done

- [ ] `WalkInput.Resolve` covers all 16 key combos (Task 1 xUnit green).
- [ ] `dotnet test Faketorio.sln -c Release` green; sim test count unchanged vs `main`, presentation +16.
- [ ] `dotnet build game/Game.csproj -c Debug` → 0/0.
- [ ] Godot headless `--quit-after 240` → clean exit 0, no script/parse/draw errors.
- [ ] Golden gate `exit 0, gate PASS`; `bench/golden.json` and `BenchScenarioTests.GOLDEN_TICK_800` byte-unchanged.
- [ ] No file under `sim/` created or modified.
- [ ] `git diff --stat main` shows only: `presentation/…/WalkInput.cs`, `presentation/…Tests/WalkInputTests.cs`, `game/project.godot`, `game/PlayerInputController.cs`, `game/Main.tscn`, `game/CameraController.cs`, `game/WorldView.cs`.
- [ ] **Deferred:** human F5 acceptance (spec §8) — run when the user is back at a computer, before final merge to `main`.
