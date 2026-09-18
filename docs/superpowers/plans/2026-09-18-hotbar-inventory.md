# 快捷栏 + 背包/生产面板 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给 `BuildFromInventory` 接一套真正的"选建筑"界面——多组可拖拽绑定的快捷栏、悬浮可滚动背包面板、只读手搓面板 + Free 模式蓝图占位、2×3 快捷按钮占位、小地图占位，并把玩家背包容量从 60 扩到 200。

**Architecture:** 全部落在既有的 `game/BuildController.cs`(状态 + 输入)/`game/WorldView.cs`(绘制)两文件分工里，延续项目现有的"立即模式渲染、零 mutation"UI 范式——不引入 Godot `Control`/`ScrollContainer` 节点树，滚动/收缩/拖拽全部手写(裁剪矩形 + 滚动偏移量 + 鼠标事件)，理由见 Task 2 前的说明。新增一个纯静态几何工具类 `game/HotbarLayout.cs`，把"给定视口尺寸算出每个 UI 元素的屏幕矩形"这套数学集中到一处，`BuildController`(点击命中判定)和 `WorldView`(绘制)各自调用同一份函数，避免布局数字在两个文件里各写一份、逐渐漂移不一致。sim 层唯一改动是 `data/base/player.json` 的 `inventorySize: 60 → 200`(纯数据,不新增/修改 sim 命令)。

**Tech Stack:** C# / .NET 8，xUnit(sim 层 TDD)，Godot 4.5.1 Mono(game 层，人工 F5 验收，无自动化测试)。

**Spec:** [`docs/superpowers/specs/2026-09-18-hotbar-inventory-design.md`](../specs/2026-09-18-hotbar-inventory-design.md)

## Global Constraints

- `game/*.cs` 不进 `Faketorio.sln`、不进 CI——每个 game 层任务只能 `dotnet build -c Release game/Game.csproj` 编译检查，最后一个任务额外要求 `dotnet build -c Debug game/Game.csproj` + 人工 F5(Godot 编辑器 F5 用的是 Debug 配置，之前两次都因为只编译 Release、Godot 加载了改动前的 stale Debug DLL 而产生假阴性，见 [`2026-09-02-m1-remaining-roadmap.md`](../specs/2026-09-02-m1-remaining-roadmap.md) 记录的两次事故)。
- **不新增 `.tscn` 节点，不引入 `Control`/`ScrollContainer`**——这个项目至今所有 UI(HUD 文字、hover 高亮框、挖矿进度条)全部是 `WorldView._Draw()` 立即模式画的，`Main.tscn` 里没有任何 `Control` 派生节点。背包面板的滚动/收缩本规范决定继续手写(裁剪矩形 + 浮点行偏移量 + 鼠标滚轮/拖拽手柄事件)，不混用第二套 UI 范式，也不需要碰 `Main.tscn`。这个决定解决了 spec §7 的两个开放问题:(a) 背包面板用立即模式画，不搭节点树；(b) 拖拽幽灵图标也是 `_Draw()` 里跟着 `GetViewport().GetMousePosition()` 现画的一个色块+文字，不用 `Control` 的 `_GetDragData` 系列虚方法。
- **按键映射的一处偏离**：spec 原文写"`E` 键切换背包"，但 `project.godot` 里 `E` 键(keycode 69)已经绑定给 `player_mine`(手挖，`PlayerInputController.UpdateMining()`)——这是写 plan 时读代码才发现的真实冲突，spec 写的时候没有核对现有 `[input]` 映射。本 plan 改用 **`Tab` 键**切换背包面板(不跟任何现有映射冲突，是常见的"打开物品栏"按键)，直接在代码里判 `Keycode: Key.Tab`(同 `CameraController.cs` 判 `Key.M` 的写法，不新增 `project.godot` `[input]` 动作，不碰这个文件)。收尾任务的 F5 验收清单和完成后的 roadmap 记录都要写清楚这个键位偏离。
- 快捷栏的数字键(`1`-`0`)、旋转键(`R`)同理直接判 `InputEventKey.Keycode`，不新增 `project.godot` 动作——跟 `Tab`/`M` 保持同一种写法，这个项目里"频道键位"(WASD/E/`` ` ``/M)用 `[input]` action，"UI 交互键位"目前只有 `` ` ``(`debug_toggle`)算是例外用了 action；鉴于本次新增键位多且都是一次性简单判断，统一走直接 `Keycode` 判断更省事，不为每个键都去改 `project.godot` 的 resource 格式(手改这个文件的自定义二进制式语法容易出错，直接 C# 判断更安全)。
- `Player.Inventory` 的"空槽"判定用 `ItemStack.IsEmpty`(`Count == 0`)，不是 spec 文字里写的"`ItemProtoId == 0`"——两者在这个项目里永远等价(`ItemStack.cs` 注释:"所以 (slot == Empty) 与 slot.IsEmpty 永远等价")，但既有代码全部用 `IsEmpty`/`Count`，新代码统一跟随，不新引入 `ItemProtoId == 0` 这个次要写法。
- `RenderPalette.ForEntity(PrototypeBase)` 现有实现按运行时类型 switch，传入非实体类型(比如 `ItemPrototype`)会落到 `default => #888888`，不会抛异常——可以安全地对任意 `PrototypeBase` 调用，不需要先判类型。

---

## Task 1: 玩家背包容量 60 → 200(sim 数据层)

**Files:**
- Modify: `data/base/player.json`
- Modify: `sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs:195`
- Modify: `bench/golden.json`(用 `--update-golden` 重新生成，不手改)

**Interfaces:**
- Consumes: 无新依赖，纯数据值变更。
- Produces: `sim.Player.Inventory.SlotCount == 200`——Task 3(背包面板读取)、Task 4(拖拽绑定)依赖这个更大的槽位数才有实际的"可滚动"内容可测。

这次改动**不**新增/删除任何 `PrototypeBase` 子类实例，`PrototypeRegistry.AssignIds()`(按类型名+name 排序分配 id)完全不受影响——跟 [`2026-09-17-build-cost-design.md`](../specs/2026-09-17-build-cost-design.md) Task 2 那次"新增 7 个原型导致后续所有 id 平移、级联出 ~20 处失败"是完全不同的风险量级(这次只改一个 JSON 里的整数字段值，不产生新原型)。预期只有两类失败：(1) `PrototypeLoaderTests.cs:195` 的硬编码 `60` 断言；(2) `Inventory.WriteState` 把槽位数写进状态哈希，`bench/golden.json` 的哈希值必然变。**但 Step 3 仍然要求实际跑一遍全量测试逐条确认**，不能只凭这段分析就跳过验证——spec §2 明确要求不能重蹈"以为只有一处结果有 20 处"的覆辙。

- [ ] **Step 1: 改 `data/base/player.json`**

```json
[{ "type": "player", "name": "player",
   "inventorySize": 200, "reachSubTiles": 1536, "walkSpeedSubTilesPerTick": 38, "craftQueueCap": 32,
   "startingInventory": [
     { "name": "iron-plate", "amount": 8 },
     { "name": "wooden-chest", "amount": 1 },
     { "name": "electric-mining-drill", "amount": 1 },
     { "name": "stone-furnace", "amount": 1 },
     { "name": "burner-generator", "amount": 1 },
     { "name": "small-electric-pole", "amount": 2 },
     { "name": "coal", "amount": 20 }
   ] }]
```

（只改 `inventorySize` 这一个值，`startingInventory` 数组内容不变。）

- [ ] **Step 2: 改 `PrototypeLoaderTests.cs:195` 的断言**

`sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs` 里 `LoadsPlayerPrototype` 测试：

```csharp
    [Fact]
    public void LoadsPlayerPrototype()
    {
        var p = Load().Get<PlayerPrototype>("player");
        Assert.Equal(60, p.InventorySize);
        Assert.Equal(1536, p.ReachSubTiles);
        Assert.Equal(7, p.StartingInventory.Count);
        Assert.Equal("iron-plate", p.StartingInventory[0].Name);
    }
```

改成：

```csharp
    [Fact]
    public void LoadsPlayerPrototype()
    {
        var p = Load().Get<PlayerPrototype>("player");
        Assert.Equal(200, p.InventorySize);
        Assert.Equal(1536, p.ReachSubTiles);
        Assert.Equal(7, p.StartingInventory.Count);
        Assert.Equal("iron-plate", p.StartingInventory[0].Name);
    }
```

- [ ] **Step 3: 跑全量测试，逐条排查除了 Step 2 那处之外还有没有别的失败**

Run: `dotnet test -c Release Faketorio.sln`

Expected: 全部通过(Step 2 已经把已知的那一处改掉了)。如果出现任何**其它**失败，先用 `git stash` 把 Step 1/2 的改动暂存、跑一次基线确认那条测试在改动前就是绿的，再 `git stash pop` 恢复，然后读失败信息判断根因：如果是又一处硬编码 `60` / `SlotCount` 假设，按 Step 2 的方式同步更新（并在这个任务的 commit message 里如实写出"还发现并修了 X 处"）；如果失败原因看起来跟背包容量无关（比如断言的是别的字段），停下来，那是真 bug，不要顺手"因为跟这次改动同批出现"就假定它跟这次改动有关。

- [ ] **Step 4: 重新生成 bench golden 基线**

Run: `dotnet build -c Release sim/Faketorio.Sim.Bench/Faketorio.Sim.Bench.csproj`
Run: `dotnet sim/Faketorio.Sim.Bench/bin/Release/net8.0/Faketorio.Sim.Bench.dll --scale 50 --ticks 5000 --golden bench/golden.json --update-golden`

Expected: 命令成功退出（`--update-golden` 总是成功，见 `BenchRunner.cs` 里的注释"10. --update-golden regenerates the baseline and always succeeds"），`bench/golden.json` 的内容被覆盖（`recordedAtCommit`/`baselineNsPerTick`/hash 字段都会变——这是预期的，`Inventory` 槽位数变大后 `Player.WriteState` 序列化字节数变了，状态哈希必然不同）。

- [ ] **Step 5: 用新基线跑一次 gate 检查，确认基线本身是自洽的**

Run: `dotnet sim/Faketorio.Sim.Bench/bin/Release/net8.0/Faketorio.Sim.Bench.dll --scale 50 --ticks 5000 --golden bench/golden.json`

Expected: `gate PASS`（跟 Step 4 刚写的基线比对，理应完全一致——如果不是 PASS，说明基线生成过程本身有问题，比如两次跑的场景不是同一个种子/参数，需要回头检查命令参数是否跟 Step 4 完全一致）。

- [ ] **Step 6: Commit**

```bash
git add data/base/player.json sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs bench/golden.json
git commit -m "feat(sim): 玩家背包容量 60 -> 200,重新基线 bench golden"
```

---

## Task 2: 共享布局几何工具 `HotbarLayout`

**Files:**
- Create: `game/HotbarLayout.cs`

**Interfaces:**
- Consumes: 无(纯静态方法，只吃 `Godot.Vector2`/基础数值类型，不依赖任何 Node/Sim 状态)。
- Produces: `HotbarLayout` 静态类，公开以下方法供 Task 3(`BuildController` 点击命中判定)和 Task 5(`WorldView` 绘制)共用——两边**必须**调用同一份函数算矩形，不能各自重算，否则点击命中区域和实际画出来的框会对不上：
  - `HotbarRects HotbarRow(Vector2 viewportSize, int slotsPerGroup)` → 页按钮矩形 + N 个快捷栏槽位矩形 + 6 个快捷操作按钮矩形。
  - `Rect2 InventoryPanel(Vector2 viewportSize, float heightFraction)` → 背包面板整体矩形(位置+尺寸)。
  - `Rect2[] InventoryGrid(Rect2 panelRect, int slotCount, int columnCount, float scrollOffsetRows, out int visibleRowCount)` → 每个格子的屏幕矩形，已经应用裁剪(只返回真正可见的格子;不可见的行不进数组，调用方按数组下标对应 `slotIndex = firstVisibleRow*columnCount + arrayIndex` 反推)。
  - `Rect2 ResizeHandle(Rect2 panelRect)` → 面板顶部的收缩手柄矩形。
  - `Rect2 ContextPanel(Vector2 viewportSize)` → 手搓/蓝图面板矩形，紧贴背包面板右侧。
  - `Rect2 Minimap(Vector2 viewportSize)` → 小地图占位框矩形。

`HotbarRects` 是这个文件里定义的一个小 `readonly struct`，装页按钮/槽位数组/快捷操作数组三个字段。

背景数字全部照抄 Visual Companion v3 mockup(`.superpowers/brainstorm/380-1789718040/content/hotbar-inventory-v3.html`)里已经跟用户确认过的比例，换算成像素常量：

- [ ] **Step 1: 写 `game/HotbarLayout.cs`**

```csharp
using Godot;

namespace Faketorio.Game;

/// 纯几何计算,不依赖 Node/Sim。BuildController(点击命中判定)和 WorldView(绘制)
/// 必须调用这里的同一份函数算矩形——两边各自重算同一套数字会导致"画出来的框"
/// 和"能点中的区域"逐渐不一致,这个类就是唯一真源。
public static class HotbarLayout
{
    public const int SlotsPerGroup = 10;

    // ---- 快捷栏行 ----
    private static readonly Vector2 PageButtonSize = new(26, 44);
    private static readonly Vector2 SlotSize = new(38, 44);
    private const float SlotGap = 3f;
    private const float GroupGap = 10f;   // 页按钮<->槽位、槽位<->快捷操作 之间的间距
    private static readonly Vector2 QuickActionSize = new(22, 22);
    private const float QuickActionGap = 3f;
    private const int QuickActionCols = 3, QuickActionRows = 2;
    private const float HotbarBottomMargin = 14f;   // 槽位行底边到视口底边的距离

    public readonly struct HotbarRects
    {
        public readonly Rect2 PageButton;
        public readonly Rect2[] Slots;          // 长度 = slotsPerGroup
        public readonly Rect2[] QuickActions;   // 长度 = QuickActionCols * QuickActionRows

        public HotbarRects(Rect2 pageButton, Rect2[] slots, Rect2[] quickActions)
        {
            PageButton = pageButton;
            Slots = slots;
            QuickActions = quickActions;
        }
    }

    public static HotbarRects HotbarRow(Vector2 viewportSize, int slotsPerGroup)
    {
        float slotsWidth = SlotSize.X * slotsPerGroup + SlotGap * (slotsPerGroup - 1);
        float quickWidth = QuickActionSize.X * QuickActionCols + QuickActionGap * (QuickActionCols - 1);
        float totalWidth = PageButtonSize.X + GroupGap + slotsWidth + GroupGap + quickWidth;

        float rowHeight = Mathf.Max(PageButtonSize.Y, SlotSize.Y);
        float x0 = (viewportSize.X - totalWidth) / 2f;
        float y0 = viewportSize.Y - HotbarBottomMargin - rowHeight;

        var pageBtn = new Rect2(new Vector2(x0, y0 + (rowHeight - PageButtonSize.Y) / 2f), PageButtonSize);

        var slots = new Rect2[slotsPerGroup];
        float sx = x0 + PageButtonSize.X + GroupGap;
        float sy = y0 + (rowHeight - SlotSize.Y) / 2f;
        for (int i = 0; i < slotsPerGroup; i++)
        {
            slots[i] = new Rect2(new Vector2(sx + i * (SlotSize.X + SlotGap), sy), SlotSize);
        }

        var quick = new Rect2[QuickActionCols * QuickActionRows];
        float qx0 = sx + slotsWidth + GroupGap;
        float qy0 = y0 + (rowHeight - (QuickActionSize.Y * QuickActionRows + QuickActionGap * (QuickActionRows - 1))) / 2f;
        for (int r = 0; r < QuickActionRows; r++)
            for (int c = 0; c < QuickActionCols; c++)
            {
                var pos = new Vector2(qx0 + c * (QuickActionSize.X + QuickActionGap), qy0 + r * (QuickActionSize.Y + QuickActionGap));
                quick[r * QuickActionCols + c] = new Rect2(pos, QuickActionSize);
            }

        return new HotbarRects(pageBtn, slots, quick);
    }

    // ---- 背包面板 ----
    private const float MinPanelWidth = 560f;
    private const float PanelWidthRatio = 0.44f;
    private const float PanelHeightRatio = 0.7f;
    private const float PanelTopMargin = 70f;
    private const float PanelLeftMargin = 24f;
    private const float PanelPadding = 8f;
    private const int ColumnCount = 20;
    private const float CellGap = 2f;
    private const float MinVisibleRows = 2f;   // heightFraction=0 时仍然至少露出这么多行(收缩下限)

    public static Rect2 InventoryPanel(Vector2 viewportSize, float heightFraction)
    {
        float width = Mathf.Max(MinPanelWidth, viewportSize.X * PanelWidthRatio);
        float maxHeight = viewportSize.Y * PanelHeightRatio;
        float minHeight = RowHeight(width) * MinVisibleRows + PanelPadding * 2f + HeaderHeight;
        float height = Mathf.Lerp(minHeight, maxHeight, Mathf.Clamp(heightFraction, 0f, 1f));

        return new Rect2(new Vector2(PanelLeftMargin, PanelTopMargin), new Vector2(width, height));
    }

    private const float HeaderHeight = 20f;   // 标题行 + resize handle 的高度预留

    // 一行格子的高度(含格子本身,不含 gap——CellSize 是正方形,取宽度算)
    private static float RowHeight(float panelWidth)
    {
        float contentWidth = panelWidth - PanelPadding * 2f;
        float cell = (contentWidth - CellGap * (ColumnCount - 1)) / ColumnCount;
        return cell + CellGap;
    }

    public static Rect2 ResizeHandle(Rect2 panelRect)
    {
        var size = new Vector2(16, 6);
        var pos = panelRect.Position + new Vector2((panelRect.Size.X - size.X) / 2f, 4f);
        return new Rect2(pos, size);
    }

    // 返回当前可见的格子矩形(已裁剪到面板高度内),scrollOffsetRows 是滚动了多少整行
    // (含小数——允许半行滚动,视觉更顺滑)。visibleRowCount 是这次**实际**发出了几行
    // (不是估算上限)——调用方用它 + scrollOffsetRows 反推每个数组下标对应的真实
    // slotIndex 时,必须逐行连续、不能有被跳过的中间行,否则下标会错位(这是这份
    // 函数唯一必须维护的契约:数组前 N 行必须依次对应 firstRow, firstRow+1, ...,
    // 不能因为某一行"部分裁剪"就整行跳过——那样会让调用方以为第 0 行对应 firstRow,
    // 实际上却是 firstRow+1,后续所有点击命中判定都会错位一整行)。
    // 顶部一行哪怕因为滚动分数被裁掉一点点(至多 CellGap 那么几像素),也照样整行发出——
    // 允许极轻微地画出面板标题区之下、正文区之上那一丝丝(不做真正的裁剪矩形/scissor,
    // 这点视觉溢出可以接受);底部则相反,一旦某一行整体已经落在可视区域下边界之外,
    // 后面的行(y 单调递增)必然也在外面,直接 break,不再继续。
    public static Rect2[] InventoryGrid(Rect2 panelRect, int slotCount, int columnCount, float scrollOffsetRows, out int visibleRowCount)
    {
        float contentWidth = panelRect.Size.X - PanelPadding * 2f;
        float cell = (contentWidth - CellGap * (columnCount - 1)) / columnCount;
        float rowH = cell + CellGap;

        float gridTop = panelRect.Position.Y + HeaderHeight + PanelPadding;
        float gridBottom = panelRect.Position.Y + panelRect.Size.Y - PanelPadding;

        int totalRows = Mathf.CeilToInt(slotCount / (float)columnCount);
        int firstRow = Mathf.FloorToInt(scrollOffsetRows);
        float subRowOffsetPx = (scrollOffsetRows - firstRow) * rowH;

        var result = new System.Collections.Generic.List<Rect2>();
        int emittedRows = 0;
        for (int r = 0; ; r++)
        {
            int row = firstRow + r;
            if (row >= totalRows) break;
            float y = gridTop + r * rowH - subRowOffsetPx;
            if (y > gridBottom) break;   // 这一行(及之后所有行,y 单调递增)已经整体落在可视区域下方

            emittedRows++;
            for (int c = 0; c < columnCount; c++)
            {
                int slotIndex = row * columnCount + c;
                if (slotIndex >= slotCount) break;
                float x = panelRect.Position.X + PanelPadding + c * (cell + CellGap);
                result.Add(new Rect2(new Vector2(x, y), new Vector2(cell, cell)));
            }
        }
        visibleRowCount = emittedRows;
        return result.ToArray();
    }

    // ---- 手搓/蓝图面板 ----
    private const float MinContextPanelWidth = 280f;
    private const float ContextPanelWidthRatio = 0.32f;
    private const float PanelGap = 10f;

    public static Rect2 ContextPanel(Vector2 viewportSize)
    {
        var inv = InventoryPanel(viewportSize, heightFraction: 1f);   // 高度跟背包面板展开到最大时对齐
        float width = Mathf.Max(MinContextPanelWidth, viewportSize.X * ContextPanelWidthRatio);
        float x = inv.Position.X + inv.Size.X + PanelGap;
        return new Rect2(new Vector2(x, inv.Position.Y), new Vector2(width, inv.Size.Y));
    }

    // ---- 小地图占位 ----
    private static readonly Vector2 MinimapSize = new(120, 120);
    private const float MinimapMargin = 10f;

    public static Rect2 Minimap(Vector2 viewportSize)
    {
        var pos = new Vector2(viewportSize.X - MinimapMargin - MinimapSize.X, MinimapMargin);
        return new Rect2(pos, MinimapSize);
    }
}
```

- [ ] **Step 2: 编译确认无误**

Run: `dotnet build -c Release game/Game.csproj`
Expected: `0 个警告 0 个错误`。

- [ ] **Step 3: Commit**

```bash
git add game/HotbarLayout.cs
git commit -m "feat(game): 新增 HotbarLayout 共享布局几何工具"
```

---

## Task 3: 快捷栏状态 + 输入(`BuildController.cs`)

**Files:**
- Modify: `game/BuildController.cs`

**Interfaces:**
- Consumes: `HotbarLayout.HotbarRow`(Task 2)、`CommandType.BuildFromInventory`(已存在)、`PrototypeRegistry.TryGetEntityByName`(已存在，用于判断一个物品是否可绑进快捷栏——即是否有 `PlaceResult` 且能解析出实体）。
- Produces:
  - `public int ActiveGroup { get; }`、`public int SelectedSlot { get; }`、`public byte SelectedRotation { get; }`、`public IReadOnlyList<int[]> HotbarGroups { get; }`、`public bool GroupPanelExpanded { get; }` —— Task 5(`WorldView` 画快捷栏)读这些。
  - `public bool InventoryOpen { get; }`(Task 4 在同一个文件里加 Tab 切换逻辑时会用到这个字段的 setter，这个任务先声明字段占位，默认 `false`，Task 4 补输入)。

这个任务只做"快捷栏本身"(选格、翻组、旋转、把选中槽的绑定物品用于建造)，**不**做拖拽绑定(那是背包面板存在之后才有意义的交互，见 Task 4)——所以这个任务结束时快捷栏槽位全是空的（`-1`），没有背包面板没法把东西拖进去，属于预期中间状态，不是 bug。

- [ ] **Step 1: 加状态字段 + 初始化**

在 `game/BuildController.cs` 现有字段区(`_chestItemProtoId` 那一组)之后加：

```csharp
    // ---- 快捷栏状态 ----
    public int ActiveGroup { get; private set; }
    public int SelectedSlot { get; private set; }
    public byte SelectedRotation { get; private set; }
    public bool GroupPanelExpanded { get; private set; }
    public bool InventoryOpen { get; private set; }
    public IReadOnlyList<int[]> HotbarGroups => _hotbarGroups;

    private readonly List<int[]> _hotbarGroups = new();

    private int[] NewEmptyGroup()
    {
        var g = new int[HotbarLayout.SlotsPerGroup];
        Array.Fill(g, -1);
        return g;
    }
```

文件顶部加 `using System;` `using System.Collections.Generic;`（如果还没有——检查现有 `using` 块）。

`_Ready()` 方法末尾（`_rejectedSeen = _host.Sim.RejectedCommandCount;` 之后）加：

```csharp
        _hotbarGroups.Add(NewEmptyGroup());
```

- [ ] **Step 2: 数字键选槽 + R 键旋转**

`_UnhandledInput` 方法开头加一段处理键盘事件的分支（跟现有的鼠标分支并列，不要嵌进去）：

```csharp
    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } key)
        {
            int? slot = DigitKeyToSlot(key.Keycode);
            if (slot is int s)
            {
                SelectedSlot = s;
                GetViewport().SetInputAsHandled();
                return;
            }
            if (key.Keycode == Key.R)
            {
                SelectedRotation = (byte)((SelectedRotation + 1) % 4);
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        if (e is not InputEventMouseButton mb || !mb.Pressed) return;
        // ... 原有鼠标分支往下接
```

原有鼠标分支（左键建造那部分）保持不动，紧接在这段键盘分支之后（`if (e is not InputEventMouseButton mb || !mb.Pressed) return;` 这行本来就在，不用重复写，上面贴的是给你看新旧怎么衔接）。

在类里加一个私有静态方法：

```csharp
    private static int? DigitKeyToSlot(Key k) => k switch
    {
        Key.Key1 => 0, Key.Key2 => 1, Key.Key3 => 2, Key.Key4 => 3, Key.Key5 => 4,
        Key.Key6 => 5, Key.Key7 => 6, Key.Key8 => 7, Key.Key9 => 8, Key.Key0 => 9,
        _ => null,
    };
```

- [ ] **Step 3: 鼠标点击快捷栏槽位 / 页按钮 / 已展开的分组列表**

在原有左键分支**之前**插入命中判定（命中了快捷栏 UI 就消费掉这次点击，不再往下走"点世界=建造"那条路）：

```csharp
        if (mb.ButtonIndex == MouseButton.Left)
        {
            var viewport = GetViewport().GetVisibleRect().Size;
            var rects = HotbarLayout.HotbarRow(viewport, HotbarLayout.SlotsPerGroup);

            if (rects.PageButton.HasPoint(mb.Position))
            {
                GroupPanelExpanded = !GroupPanelExpanded;
                GetViewport().SetInputAsHandled();
                return;
            }

            if (GroupPanelExpanded && HandleGroupPanelClick(mb.Position, rects.PageButton))
            {
                GetViewport().SetInputAsHandled();
                return;
            }

            for (int i = 0; i < rects.Slots.Length; i++)
            {
                if (rects.Slots[i].HasPoint(mb.Position))
                {
                    SelectedSlot = i;
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }
        }

        if (mb.ButtonIndex == MouseButton.Middle)
        {
            var viewport = GetViewport().GetVisibleRect().Size;
            var rects = HotbarLayout.HotbarRow(viewport, HotbarLayout.SlotsPerGroup);
            for (int i = 0; i < rects.Slots.Length; i++)
            {
                if (rects.Slots[i].HasPoint(mb.Position))
                {
                    _hotbarGroups[ActiveGroup][i] = -1;
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }
        }
```

组列表展开面板的命中判定 + 处理（纵向列在页按钮正上方，每项高度 22px，跟页按钮同宽；最后一项是"+ 新建"）。`GroupPanelItemHeight` 声明成 `internal const`（不是 `private const`）——Task 5 里 `WorldView` 要画一模一样的展开列表，需要引用同一个数值，同命名空间下 `internal` 就能直接访问，不用另开一套公开 API：

```csharp
    internal const float GroupPanelItemHeight = 22f;

    // 展开的分组列表点在了哪一项——是就处理(切组/新建)并返回 true;点在列表范围外返回 false
    // (调用方据此决定是否收起面板;这里保持简单,点哪儿都直接处理完就收起)。
    private bool HandleGroupPanelClick(Vector2 pos, Rect2 pageButton)
    {
        int itemCount = _hotbarGroups.Count + 1;   // 最后一项是"+ 新建"
        var panelRect = new Rect2(
            pageButton.Position - new Vector2(0, itemCount * GroupPanelItemHeight),
            new Vector2(pageButton.Size.X, itemCount * GroupPanelItemHeight));

        if (!panelRect.HasPoint(pos)) return false;

        int index = (int)((pos.Y - panelRect.Position.Y) / GroupPanelItemHeight);
        if (index < 0 || index >= itemCount) return false;

        if (index == _hotbarGroups.Count)
        {
            _hotbarGroups.Add(NewEmptyGroup());
            ActiveGroup = _hotbarGroups.Count - 1;
        }
        else
        {
            ActiveGroup = index;
        }
        SelectedSlot = 0;
        GroupPanelExpanded = false;
        return true;
    }
```

（`WorldView` 画组列表面板时要用一模一样的 `panelRect` 计算方式，Task 5 会直接复用 `BuildController.GroupPanelItemHeight` 这个常量重新算出同一个矩形。）

- [ ] **Step 4: 左键建造改用选中槽绑定的物品**

现有左键建造那段（在 Step 3 插入的命中判定全部落空之后才会走到）：

```csharp
        var (x, y) = _cam.WorldXform.ScreenToTile(mb.Position.ToCore());
        _host.Submit(new Command { Type = CommandType.BuildFromInventory, ProtoId = _chestItemProtoId, X = x, Y = y });

        GetViewport().SetInputAsHandled();
```

改成：

```csharp
        int boundItemId = _hotbarGroups[ActiveGroup][SelectedSlot];
        if (boundItemId >= 0)
        {
            var (x, y) = _cam.WorldXform.ScreenToTile(mb.Position.ToCore());
            _host.Submit(new Command { Type = CommandType.BuildFromInventory, ProtoId = boundItemId, X = x, Y = y, Rotation = SelectedRotation });
        }

        GetViewport().SetInputAsHandled();
```

（空槽——`boundItemId == -1`——直接不提交命令，`GetViewport().SetInputAsHandled()` 仍然要调用，否则这次点击会继续往 Godot 的默认处理链走。`_chestItemProtoId` 字段和它在 `_Ready()` 里的初始化整行删掉——不再需要硬编码木箱，`using Faketorio.Sim.Prototypes;` 如果没有别的地方用到 `ItemPrototype` 就顺手删掉这行 `using`，用 `grep -n "ItemPrototype\|using Faketorio.Sim.Prototypes"  game/BuildController.cs` 确认一下再删，Task 4 马上又会用到这个 using，别删了又要加回来——如果不确定就先不删，等 Task 4 完成后再统一清理。）

- [ ] **Step 5: 编译确认无误**

Run: `dotnet build -c Release game/Game.csproj`
Expected: `0 个警告 0 个错误`。

- [ ] **Step 6: Commit**

```bash
git add game/BuildController.cs
git commit -m "feat(game): 快捷栏状态+输入 —— 多组选槽/旋转/翻组,左键建造改用选中槽绑定物品"
```

---

## Task 4: 背包面板状态 + 拖拽绑定(`BuildController.cs`)

**Files:**
- Modify: `game/BuildController.cs`

**Interfaces:**
- Consumes: `HotbarLayout.InventoryPanel`/`InventoryGrid`/`ResizeHandle`(Task 2)、`sim.Player.Inventory`(已存在)、`Inventory.SlotCount`/`this[int]`(已存在)、`PrototypeRegistry.Get<ItemPrototype>`/`TryGetEntityByName`(已存在)。
- Produces:
  - `public bool InventoryOpen`(补上 Task 3 占位字段的实际切换逻辑)。
  - `public float PanelHeightFraction { get; }`、`public float ScrollOffsetRows { get; }` —— Task 5 画面板要用。
  - `public int? DragItemProtoId { get; }`、`public Vector2 DragScreenPos { get; }` —— Task 5 画拖拽幽灵图标要用。

- [ ] **Step 1: 加状态字段**

```csharp
    // ---- 背包面板状态 ----
    public float PanelHeightFraction { get; private set; } = 1f;
    public float ScrollOffsetRows { get; private set; }
    public int? DragItemProtoId { get; private set; }
    public Vector2 DragScreenPos { get; private set; }

    private bool _resizingPanel;
```

- [ ] **Step 2: Tab 切换背包面板**

在 Step 3(Task 3 写的)键盘分支里加一支：

```csharp
            if (key.Keycode == Key.Tab)
            {
                InventoryOpen = !InventoryOpen;
                GetViewport().SetInputAsHandled();
                return;
            }
```

（插在 `Key.R` 那个 `if` 块之后、`if (e is not InputEventMouseButton mb...)` 之前。）

- [ ] **Step 3: 拖拽开始——鼠标左键在背包格上按下**

在左键分支里、Task 3 Step 3 写的"快捷栏槽位命中判定"之后、"左键建造"之前，插入背包格命中判定（只在 `InventoryOpen` 时生效）：

```csharp
`cellRects` 数组下标不能简单假设"每行固定 20 个"——`HotbarLayout.InventoryGrid` 在最后一行 `slotIndex >= slotCount` 时会提前截断，行内实际格子数可能小于 20，所以命中判定要按行展开逐格核对真实 `slotIndex`，不能用 `firstRow * 20 + i` 这种简化下标换算：

```csharp
            if (InventoryOpen)
            {
                var invPanel = HotbarLayout.InventoryPanel(viewport, PanelHeightFraction);
                var inv = _host.Sim.Player.Inventory;
                var cellRects = HotbarLayout.InventoryGrid(invPanel, inv.SlotCount, 20, ScrollOffsetRows, out int visibleRows);
                int firstRow = Mathf.FloorToInt(ScrollOffsetRows);

                int idx = 0;
                bool hitCell = false;
                for (int r = 0; r < visibleRows && !hitCell; r++)
                {
                    int row = firstRow + r;
                    for (int c = 0; c < 20; c++)
                    {
                        int slotIndex = row * 20 + c;
                        if (slotIndex >= inv.SlotCount || idx >= cellRects.Length) break;
                        if (cellRects[idx].HasPoint(mb.Position))
                        {
                            var stack = inv[slotIndex];
                            if (!stack.IsEmpty) DragItemProtoId = stack.ItemProtoId;   // 空格不开始拖拽,DragItemProtoId 保持 null
                            hitCell = true;
                            break;
                        }
                        idx++;
                    }
                }
                if (hitCell)
                {
                    GetViewport().SetInputAsHandled();
                    return;
                }

                var handle = HotbarLayout.ResizeHandle(invPanel);
                if (handle.HasPoint(mb.Position))
                {
                    _resizingPanel = true;
                    GetViewport().SetInputAsHandled();
                    return;
                }

                if (invPanel.HasPoint(mb.Position))
                {
                    // 点在面板空白处(不是格子也不是 handle):不建造,直接吃掉这次点击。
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }
```

- [ ] **Step 4: 拖拽中——鼠标移动更新幽灵图标位置；松开——释放判定**

`_UnhandledInput` 里加处理 `InputEventMouseMotion` 和左键释放：

```csharp
        if (e is InputEventMouseMotion mm && DragItemProtoId.HasValue)
        {
            DragScreenPos = mm.Position;
        }

        if (_resizingPanel && e is InputEventMouseMotion rmm)
        {
            var viewport = GetViewport().GetVisibleRect().Size;
            var panel = HotbarLayout.InventoryPanel(viewport, 1f);   // 用满高度算总可用范围做分母
            float delta = -rmm.Relative.Y / Mathf.Max(1f, panel.Size.Y);
            PanelHeightFraction = Mathf.Clamp(PanelHeightFraction + delta, 0f, 1f);
        }

        if (e is InputEventMouseButton relb && !relb.Pressed)
        {
            if (relb.ButtonIndex == MouseButton.Left && _resizingPanel)
            {
                _resizingPanel = false;
                GetViewport().SetInputAsHandled();
                return;
            }

            if (relb.ButtonIndex == MouseButton.Left && DragItemProtoId.HasValue)
            {
                int dragged = DragItemProtoId.Value;
                DragItemProtoId = null;   // 无论落在哪都先清空拖拽状态,下面只是决定要不要真的绑定

                var protos = _host.Sim.Prototypes;
                var itemProto = protos.GetById(dragged) as ItemPrototype;
                bool bindable = itemProto?.PlaceResult is not null && protos.TryGetEntityByName(itemProto.PlaceResult, out _);

                if (bindable)
                {
                    var viewport = GetViewport().GetVisibleRect().Size;
                    var rects = HotbarLayout.HotbarRow(viewport, HotbarLayout.SlotsPerGroup);
                    for (int i = 0; i < rects.Slots.Length; i++)
                    {
                        if (rects.Slots[i].HasPoint(relb.Position))
                        {
                            _hotbarGroups[ActiveGroup][i] = dragged;
                            break;
                        }
                    }
                }
                GetViewport().SetInputAsHandled();
                return;
            }
        }
```

（`_resizingPanel` 的 `InputEventMouseMotion` 处理必须放在"拖拽中更新幽灵图标"那段**之后**、左键释放判定**之前**——三段是独立的 `if`，顺序不影响正确性，这里只是给出建议的书写顺序，方便阅读。滚轮滚动背包在这个任务里也一起加：）

```csharp
        if (InventoryOpen && e is InputEventMouseButton wheel && wheel.Pressed
            && (wheel.ButtonIndex == MouseButton.WheelUp || wheel.ButtonIndex == MouseButton.WheelDown))
        {
            var viewport = GetViewport().GetVisibleRect().Size;
            var invPanel = HotbarLayout.InventoryPanel(viewport, PanelHeightFraction);
            if (invPanel.HasPoint(wheel.Position))
            {
                int totalRows = Mathf.CeilToInt(_host.Sim.Player.Inventory.SlotCount / 20f);
                HotbarLayout.InventoryGrid(invPanel, _host.Sim.Player.Inventory.SlotCount, 20, ScrollOffsetRows, out int visibleRows);
                float maxScroll = Mathf.Max(0, totalRows - visibleRows);
                float dir = wheel.ButtonIndex == MouseButton.WheelDown ? 1f : -1f;
                ScrollOffsetRows = Mathf.Clamp(ScrollOffsetRows + dir, 0f, maxScroll);
                GetViewport().SetInputAsHandled();
                return;
            }
        }
```

- [ ] **Step 5: 核对完整的 `_UnhandledInput` 方法**

Task 3 + Task 4 是分好几个片段插入同一个方法的，容易在拼装顺序上出错（尤其是"哪段在哪段之前 return"直接决定这段代码能不能被执行到）。写完 Step 1-4 之后，把整个 `_UnhandledInput` 方法跟下面这份完整版本逐行核对一遍，确认结构完全一致（下面这份就是 Task 3 Step 2/3/4 + Task 4 Step 2/3/4 全部叠加后的最终形态，包括正确的分支顺序）：

```csharp
    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } key)
        {
            int? slot = DigitKeyToSlot(key.Keycode);
            if (slot is int s)
            {
                SelectedSlot = s;
                GetViewport().SetInputAsHandled();
                return;
            }
            if (key.Keycode == Key.R)
            {
                SelectedRotation = (byte)((SelectedRotation + 1) % 4);
                GetViewport().SetInputAsHandled();
                return;
            }
            if (key.Keycode == Key.Tab)
            {
                InventoryOpen = !InventoryOpen;
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        if (e is InputEventMouseMotion mm && DragItemProtoId.HasValue)
        {
            DragScreenPos = mm.Position;
        }

        if (_resizingPanel && e is InputEventMouseMotion rmm)
        {
            var viewportForResize = GetViewport().GetVisibleRect().Size;
            var panelForResize = HotbarLayout.InventoryPanel(viewportForResize, 1f);
            float delta = -rmm.Relative.Y / Mathf.Max(1f, panelForResize.Size.Y);
            PanelHeightFraction = Mathf.Clamp(PanelHeightFraction + delta, 0f, 1f);
        }

        if (InventoryOpen && e is InputEventMouseButton wheel && wheel.Pressed
            && (wheel.ButtonIndex == MouseButton.WheelUp || wheel.ButtonIndex == MouseButton.WheelDown))
        {
            var viewport = GetViewport().GetVisibleRect().Size;
            var invPanel = HotbarLayout.InventoryPanel(viewport, PanelHeightFraction);
            if (invPanel.HasPoint(wheel.Position))
            {
                int totalRows = Mathf.CeilToInt(_host.Sim.Player.Inventory.SlotCount / 20f);
                HotbarLayout.InventoryGrid(invPanel, _host.Sim.Player.Inventory.SlotCount, 20, ScrollOffsetRows, out int visibleRowsForScroll);
                float maxScroll = Mathf.Max(0, totalRows - visibleRowsForScroll);
                float dir = wheel.ButtonIndex == MouseButton.WheelDown ? 1f : -1f;
                ScrollOffsetRows = Mathf.Clamp(ScrollOffsetRows + dir, 0f, maxScroll);
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        if (e is InputEventMouseButton relb && !relb.Pressed)
        {
            if (relb.ButtonIndex == MouseButton.Left && _resizingPanel)
            {
                _resizingPanel = false;
                GetViewport().SetInputAsHandled();
                return;
            }

            if (relb.ButtonIndex == MouseButton.Left && DragItemProtoId.HasValue)
            {
                int dragged = DragItemProtoId.Value;
                DragItemProtoId = null;   // 无论落在哪都先清空拖拽状态,下面只是决定要不要真的绑定

                var protos = _host.Sim.Prototypes;
                var itemProto = protos.GetById(dragged) as ItemPrototype;
                bool bindable = itemProto?.PlaceResult is not null && protos.TryGetEntityByName(itemProto.PlaceResult, out _);

                if (bindable)
                {
                    var viewport = GetViewport().GetVisibleRect().Size;
                    var rects = HotbarLayout.HotbarRow(viewport, HotbarLayout.SlotsPerGroup);
                    for (int i = 0; i < rects.Slots.Length; i++)
                    {
                        if (rects.Slots[i].HasPoint(relb.Position))
                        {
                            _hotbarGroups[ActiveGroup][i] = dragged;
                            break;
                        }
                    }
                }
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        // 到这里,剩下的只可能是"按下"事件——所有"松开"事件(拖拽释放/resize 结束)
        // 都已经在上面处理并 return 掉了。
        if (e is not InputEventMouseButton mb || !mb.Pressed) return;

        if (mb.ButtonIndex == MouseButton.Left)
        {
            var viewport = GetViewport().GetVisibleRect().Size;
            var rects = HotbarLayout.HotbarRow(viewport, HotbarLayout.SlotsPerGroup);

            if (rects.PageButton.HasPoint(mb.Position))
            {
                GroupPanelExpanded = !GroupPanelExpanded;
                GetViewport().SetInputAsHandled();
                return;
            }

            if (GroupPanelExpanded && HandleGroupPanelClick(mb.Position, rects.PageButton))
            {
                GetViewport().SetInputAsHandled();
                return;
            }

            for (int i = 0; i < rects.Slots.Length; i++)
            {
                if (rects.Slots[i].HasPoint(mb.Position))
                {
                    SelectedSlot = i;
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }

            if (InventoryOpen)
            {
                var invPanel = HotbarLayout.InventoryPanel(viewport, PanelHeightFraction);
                var inv = _host.Sim.Player.Inventory;
                var cellRects = HotbarLayout.InventoryGrid(invPanel, inv.SlotCount, 20, ScrollOffsetRows, out int visibleRows);
                int firstRow = Mathf.FloorToInt(ScrollOffsetRows);

                int idx = 0;
                bool hitCell = false;
                for (int r = 0; r < visibleRows && !hitCell; r++)
                {
                    int row = firstRow + r;
                    for (int c = 0; c < 20; c++)
                    {
                        int slotIndex = row * 20 + c;
                        if (slotIndex >= inv.SlotCount || idx >= cellRects.Length) break;
                        if (cellRects[idx].HasPoint(mb.Position))
                        {
                            var stack = inv[slotIndex];
                            if (!stack.IsEmpty) DragItemProtoId = stack.ItemProtoId;
                            hitCell = true;
                            break;
                        }
                        idx++;
                    }
                }
                if (hitCell)
                {
                    GetViewport().SetInputAsHandled();
                    return;
                }

                var handle = HotbarLayout.ResizeHandle(invPanel);
                if (handle.HasPoint(mb.Position))
                {
                    _resizingPanel = true;
                    GetViewport().SetInputAsHandled();
                    return;
                }

                if (invPanel.HasPoint(mb.Position))
                {
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }

            int boundItemId = _hotbarGroups[ActiveGroup][SelectedSlot];
            if (boundItemId >= 0)
            {
                var (x, y) = _cam.WorldXform.ScreenToTile(mb.Position.ToCore());
                _host.Submit(new Command { Type = CommandType.BuildFromInventory, ProtoId = boundItemId, X = x, Y = y, Rotation = SelectedRotation });
            }

            GetViewport().SetInputAsHandled();
            return;
        }

        if (mb.ButtonIndex == MouseButton.Middle)
        {
            var viewport = GetViewport().GetVisibleRect().Size;
            var rects = HotbarLayout.HotbarRow(viewport, HotbarLayout.SlotsPerGroup);
            for (int i = 0; i < rects.Slots.Length; i++)
            {
                if (rects.Slots[i].HasPoint(mb.Position))
                {
                    _hotbarGroups[ActiveGroup][i] = -1;
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }
        }
    }
```

如果自己拼出来的方法跟这份对不上，以这份为准改。

- [ ] **Step 6: 编译确认无误**

Run: `dotnet build -c Release game/Game.csproj`
Expected: `0 个警告 0 个错误`。检查是否需要新增 `using System;`(`Mathf`/`Vector2` 已经通过 `using Godot;` 覆盖，不需要额外 using)。

- [ ] **Step 7: Commit**

```bash
git add game/BuildController.cs
git commit -m "feat(game): 背包面板状态 —— Tab 开关/滚动/收缩手柄/拖拽绑定(int? 哨兵值跟解绑区分开)"
```

---

## Task 5: 绘制(`WorldView.cs`)

**Files:**
- Modify: `game/WorldView.cs`

**Interfaces:**
- Consumes: Task 2 的 `HotbarLayout` 全部方法、Task 3/4 在 `BuildController` 上暴露的全部公开状态(`ActiveGroup`/`SelectedSlot`/`SelectedRotation`/`HotbarGroups`/`GroupPanelExpanded`/`InventoryOpen`/`PanelHeightFraction`/`ScrollOffsetRows`/`DragItemProtoId`/`DragScreenPos`)、`sim.Player.Inventory`、`sim.Prototypes`(遍历找 `category == "crafting"` 的配方)、`_cam.Mode`(`CameraController.Mode`，已存在)、`RenderPalette.ForEntity`(已存在)。
- Produces: 无(这是最终消费者，纯绘制)。

- [ ] **Step 1: 加一个物品配色 helper**

`RenderPalette.cs` 里的 `ForItem(int)` 目前是固定近白色，跟传送带上滑动的物品配套；快捷栏/背包格子需要"能建造的物品 = 它建成后那个实体的颜色，不能建造的原材料 = 沿用现有近白色"这个规则。在 `game/WorldView.cs` 里加一个私有 helper(不改 `RenderPalette.cs`，因为这条规则依赖 `PrototypeRegistry` 查找，属于"调用方业务逻辑"而不是纯配色表)：

```csharp
    // 能建造的物品(有 PlaceResult 且能解析出实体)用它建成后的实体配色;
    // 原材料/半成品(iron-plate、coal 等)沿用 RenderPalette.ForItem 的近白色。
    private static Color ItemCellColor(int itemProtoId, PrototypeRegistry protos)
    {
        if (protos.GetById(itemProtoId) is ItemPrototype ip && ip.PlaceResult is not null
            && protos.TryGetEntityByName(ip.PlaceResult, out var entityProto))
        {
            return RenderPalette.ForEntity(entityProto);
        }
        return RenderPalette.ForItem(itemProtoId);
    }
```

- [ ] **Step 2: `_Draw()` 末尾接入四个新的绘制方法**

在 `_Draw()` 方法末尾(第 9 步"相机模式 HUD"之后、第 9 步 debug 覆盖层判断之前或之后都可以——放在 debug 覆盖层判断**之后**，保持"debug 覆盖层最后画在最上层"的现有顺序不变)加：

```csharp
        // 10. 快捷栏 + 背包/生产面板/小地图 —— 全部立即模式画,数据来自 BuildController
        // 暴露的公开状态,这里只读不改(跟这个文件其它部分同一个零 mutation 原则)。
        DrawHotbar(sim);
        DrawMinimap();
        if (_build.InventoryOpen)
        {
            DrawInventoryPanel(sim);
            DrawContextPanel(sim);
        }
        DrawDragGhost(sim);
```

- [ ] **Step 3: `DrawHotbar`**

```csharp
    private void DrawHotbar(Faketorio.Sim.Simulation sim)
    {
        var viewport = GetViewport().GetVisibleRect().Size;
        var rects = HotbarLayout.HotbarRow(viewport, HotbarLayout.SlotsPerGroup);
        var protos = sim.Prototypes;
        var group = _build.HotbarGroups[_build.ActiveGroup];

        // 页按钮
        DrawRect(rects.PageButton, new Color(0.16f, 0.16f, 0.1f));
        DrawRect(rects.PageButton, new Color(0.4f, 0.4f, 0.4f), false, 1.5f);
        DrawString(ThemeDB.FallbackFont, rects.PageButton.Position + new Vector2(8, 28),
                   (_build.ActiveGroup + 1).ToString(), HorizontalAlignment.Left, -1f, 14, new Color("#ffd24a"));

        // 展开的分组列表
        if (_build.GroupPanelExpanded)
        {
            int itemCount = _build.HotbarGroups.Count + 1;
            var panelRect = new Rect2(
                rects.PageButton.Position - new Vector2(0, itemCount * BuildController.GroupPanelItemHeight),
                new Vector2(rects.PageButton.Size.X, itemCount * BuildController.GroupPanelItemHeight));
            DrawRect(panelRect, new Color(0.12f, 0.12f, 0.08f, 0.95f));
            DrawRect(panelRect, new Color(0.4f, 0.4f, 0.4f), false, 1f);
            for (int i = 0; i < itemCount; i++)
            {
                var itemPos = panelRect.Position + new Vector2(0, i * BuildController.GroupPanelItemHeight);
                string label = i == _build.HotbarGroups.Count ? "+" : (i + 1).ToString();
                var color = i == _build.ActiveGroup ? new Color("#ffd24a") : new Color(0.85f, 0.85f, 0.85f);
                DrawString(ThemeDB.FallbackFont, itemPos + new Vector2(8, 16), label,
                           HorizontalAlignment.Left, -1f, 12, color);
            }
        }

        // 10 个槽位
        for (int i = 0; i < rects.Slots.Length; i++)
        {
            var r = rects.Slots[i];
            int itemId = group[i];
            bool selected = i == _build.SelectedSlot;

            DrawRect(r, itemId < 0 ? new Color(0.1f, 0.1f, 0.1f, 0.6f) : ItemCellColor(itemId, protos));
            DrawRect(r, selected ? new Color("#ffd24a") : new Color(0.4f, 0.4f, 0.4f),
                     false, selected ? 2.5f : 1.5f);
            DrawString(ThemeDB.FallbackFont, r.Position + new Vector2(2, 10), ((i + 1) % 10).ToString(),
                       HorizontalAlignment.Left, -1f, 9, new Color("#ffd24a"));

            if (selected)
            {
                // 右下角小箭头表示 SelectedRotation(复用 DrawOrientation 同款画法,缩小版)
                var c = r.Position + r.Size - new Vector2(10, 10);
                DrawOrientation(c - new Vector2(6, 6), new Vector2(12, 12), _build.SelectedRotation);
            }
        }

        // 2x3 快捷操作占位(纯占位,不接功能)
        foreach (var r in rects.QuickActions)
        {
            DrawRect(r, new Color(0.1f, 0.1f, 0.1f, 0.4f));
            DrawRect(r, new Color(0.35f, 0.35f, 0.35f), false, 1f, antialiased: false);
        }
    }
```

（`BuildController.GroupPanelItemHeight` 要在 Task 3 里从 `private const` 改成 `internal const`——已经在 Task 3 Step 3 的说明里写了这个要求，这里是它唯一的调用点。）

- [ ] **Step 4: `DrawInventoryPanel`**

```csharp
    private void DrawInventoryPanel(Faketorio.Sim.Simulation sim)
    {
        var viewport = GetViewport().GetVisibleRect().Size;
        var panel = HotbarLayout.InventoryPanel(viewport, _build.PanelHeightFraction);
        var inv = sim.Player.Inventory;
        var protos = sim.Prototypes;

        DrawRect(panel, new Color(0.14f, 0.12f, 0.08f, 0.95f));
        DrawRect(panel, new Color(0.35f, 0.29f, 0.16f), false, 1.5f);
        DrawString(ThemeDB.FallbackFont, panel.Position + new Vector2(8, 16), "背包",
                   HorizontalAlignment.Left, -1f, 13, new Color("#e8d9a8"));

        var handle = HotbarLayout.ResizeHandle(panel);
        DrawRect(handle, new Color(0.35f, 0.29f, 0.16f));

        var cellRects = HotbarLayout.InventoryGrid(panel, inv.SlotCount, 20, _build.ScrollOffsetRows, out int visibleRows);
        int firstRow = Mathf.FloorToInt(_build.ScrollOffsetRows);
        int idx = 0;
        for (int r = 0; r < visibleRows; r++)
        {
            int row = firstRow + r;
            for (int c = 0; c < 20; c++)
            {
                int slotIndex = row * 20 + c;
                if (slotIndex >= inv.SlotCount || idx >= cellRects.Length) goto Done;
                var rect = cellRects[idx];
                var stack = inv[slotIndex];

                DrawRect(rect, stack.IsEmpty ? new Color(0.08f, 0.08f, 0.06f, 0.5f) : ItemCellColor(stack.ItemProtoId, protos));
                DrawRect(rect, new Color(0.3f, 0.27f, 0.2f), false, 1f);
                if (!stack.IsEmpty)
                    DrawString(ThemeDB.FallbackFont, rect.Position + new Vector2(1, rect.Size.Y - 1), stack.Count.ToString(),
                               HorizontalAlignment.Left, rect.Size.X, 8, Colors.White);
                idx++;
            }
        }
        Done: ;
    }
```

- [ ] **Step 5: `DrawContextPanel`(手搓面板 Follow / 蓝图占位 Free)**

```csharp
    private void DrawContextPanel(Faketorio.Sim.Simulation sim)
    {
        var viewport = GetViewport().GetVisibleRect().Size;
        var panel = HotbarLayout.ContextPanel(viewport);

        DrawRect(panel, new Color(0.10f, 0.13f, 0.16f, 0.95f));
        DrawRect(panel, new Color(0.2f, 0.29f, 0.35f), false, 1.5f);

        if (_cam.Mode == CameraMode.Free)
        {
            DrawString(ThemeDB.FallbackFont, panel.Position + new Vector2(8, 16), "蓝图面板",
                       HorizontalAlignment.Left, -1f, 13, new Color("#a8d9e8"));
            DrawString(ThemeDB.FallbackFont, panel.Position + new Vector2(8, 40), "蓝图系统待建,见 roadmap",
                       HorizontalAlignment.Left, panel.Size.X - 16, 11, new Color(0.7f, 0.7f, 0.7f));
            return;
        }

        DrawString(ThemeDB.FallbackFont, panel.Position + new Vector2(8, 16), "手搓面板",
                   HorizontalAlignment.Left, -1f, 13, new Color("#a8d9e8"));

        var protos = sim.Prototypes;
        float y = panel.Position.Y + 34;
        const float rowH = 20f;
        for (int id = 0; id < protos.Count; id++)
        {
            if (protos.GetById(id) is not RecipePrototype recipe || recipe.Category != "crafting") continue;
            if (y + rowH > panel.Position.Y + panel.Size.Y) break;   // 面板画不下更多了,直接停(这轮不做滚动)

            var swatchRect = new Rect2(panel.Position.X + 8, y, 14, 14);
            var color = recipe.ResolvedResults.Count > 0 ? ItemCellColor(recipe.ResolvedResults[0].ItemProtoId, protos) : new Color(0.5f, 0.5f, 0.5f);
            DrawRect(swatchRect, color);
            DrawString(ThemeDB.FallbackFont, panel.Position + new Vector2(28, y + 12), recipe.Name,
                       HorizontalAlignment.Left, panel.Size.X - 36, 10, new Color(0.85f, 0.85f, 0.85f));
            y += rowH;
        }
    }
```

- [ ] **Step 6: `DrawMinimap` + `DrawDragGhost`**

```csharp
    private void DrawMinimap()
    {
        var viewport = GetViewport().GetVisibleRect().Size;
        var r = HotbarLayout.Minimap(viewport);
        DrawRect(r, new Color(0.05f, 0.06f, 0.04f, 0.85f));
        DrawRect(r, new Color(0.35f, 0.35f, 0.5f), false, 1f);
        DrawString(ThemeDB.FallbackFont, r.Position + new Vector2(8, r.Size.Y / 2), "小地图(占位)",
                   HorizontalAlignment.Left, r.Size.X - 16, 9, new Color(0.5f, 0.5f, 0.6f));
    }

    private void DrawDragGhost(Faketorio.Sim.Simulation sim)
    {
        if (!_build.DragItemProtoId.HasValue) return;
        int itemId = _build.DragItemProtoId.Value;
        var proto = sim.Prototypes.GetById(itemId);
        var color = ItemCellColor(itemId, sim.Prototypes);

        var size = new Vector2(36, 36);
        var rect = new Rect2(_build.DragScreenPos - size / 2f, size);
        DrawRect(rect, color, true);
        DrawRect(rect, new Color("#ffd24a"), false, 2f);
        DrawString(ThemeDB.FallbackFont, rect.Position + new Vector2(2, size.Y - 4), proto.Name,
                   HorizontalAlignment.Left, size.X - 4, 7, Colors.White);
    }
```

- [ ] **Step 7: 加必要的 `using`**

文件顶部确认/补上：

```csharp
using Faketorio.Sim.Prototypes;   // 已存在(ItemPrototype/RecipePrototype/PrototypeRegistry 都在这个命名空间)
```

（`WorldView.cs` 已经 `using Faketorio.Sim.Prototypes;`——`grep -n "^using" game/WorldView.cs` 确认一下，理论上不需要新增。）

- [ ] **Step 8: 编译确认无误**

Run: `dotnet build -c Release game/Game.csproj`
Expected: `0 个警告 0 个错误`。

- [ ] **Step 9: Commit**

```bash
git add game/WorldView.cs
git commit -m "feat(game): 绘制快捷栏/背包面板/手搓面板/蓝图占位/小地图占位/拖拽幽灵图标"
```

---

## Task 6: 收尾 —— 全量编译 + 人工 F5 验收

**Files:** 无代码改动(除非验收发现问题需要回头补丁——那种情况按发现问题的任务归属提交,不在这个任务里堆修复)。

**Interfaces:** 无。

- [ ] **Step 1: 全量编译(Release + Debug 都要)**

Run: `dotnet build -c Release Faketorio.sln`
Expected: `0 个警告 0 个错误`(确认 sim 层 Task 1 的改动没有破坏别的项目)。

Run: `dotnet build -c Release game/Game.csproj`
Run: `dotnet build -c Debug game/Game.csproj`
Expected: 两者都 `0 个警告 0 个错误`——**Debug 这一步不能省**,Godot 编辑器 F5 用的就是 Debug 配置,之前两次人工验收都因为只编译过 Release、Godot 加载了改动前的 stale Debug DLL 而出现"看起来像真 bug"的假阴性(记录在 `2026-09-02-m1-remaining-roadmap.md`)。

- [ ] **Step 2: 人工 F5 验收**

启动 Godot(`"C:\Program Files\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe" --path game`),F5 进场景,依次验证:

1. 默认状态:屏幕底部居中能看到"1"号页按钮 + 10 个空槽位(虚线/暗色，无边框高亮说明未绑定) + 右侧 6 个占位快捷按钮；右上角能看到"小地图(占位)"框。
2. 按 `Tab`:背包面板从左侧浮现，四周仍能看到游戏世界背景(不是全屏遮罩)；面板右侧能看到"手搓面板"，列出配方名 + 色块(至少能看到 `transport-belt-basic`/`inserter-basic`/`small-electric-pole`/`stone-furnace`/`electric-mining-drill`/`assembling-machine-1`/`burner-generator` 这 7 条 —— 这些都是 category=crafting 的配方)。
3. 背包格子里能看到开局物资(`iron-plate` x8、`wooden-chest` x1、`electric-mining-drill` x1、`stone-furnace` x1、`burner-generator` x1、`small-electric-pole` x2、`coal` x20)，数字显示在格子右下角。
4. 从背包里"electric-mining-drill"那一格按住鼠标左键拖到快捷栏第 1 槽再松开：应该能看到拖拽过程中有个跟着鼠标的幽灵方块；松开后第 1 槽变成非空(有色块 + 边框)。
5. 从背包一个**空格**开始拖拽再随便松开：不应该发生任何变化(不该意外清空任何已绑定的快捷栏槽——这是验证 spec 挑出来的那个 `_dragItemProtoId` 哨兵值 bug 修复是否生效)。
6. 中键点第 1 槽:变回空槽。
7. 重新拖 `electric-mining-drill` 到第 1 槽，按 `1` 键(或直接点第 1 槽)选中它，按 `R` 键几次观察选中槽右下角的小箭头方向变化。
8. 左键点空地：应该提交 `BuildFromInventory`,钻头被建造出来(背包里对应物品数量应该减少直至归零)。空槽状态下左键点地图应该完全没反应(不闪红,因为根本没提交命令)。
9. 点左侧页按钮:展开分组列表,能看到"1"和"+"；点"+"新建一组，切过去后 10 槽应该全空；再点回"1"，之前绑定的东西还在。
10. 缩小 Godot 窗口(拖拽游戏窗口边缘到很小，或者改 `project.godot` 的默认分辨率测试极端情况——测完记得改回来)，确认背包面板格子不会缩小到完全不可点/不可读，允许面板贴边或部分超出可视区域。
11. 按 `M` 切到 Free(地图)模式：背包右侧应该从"手搓面板"变成"蓝图面板"(纯文字占位，无交互)。
12. 确认原有功能(挖矿进度条、拆除长按、debug 碰撞盒`` ` ``键、传送带渲染)没有被这次改动影响。

如果验收发现问题，记录现象，不要自己臆测原因就动手改——回到对应任务的实现代码逐行核对，按问题归属的任务号提交修复(比如快捷栏选中态的问题回 Task 3，拖拽的问题回 Task 4，画面的问题回 Task 5)。

- [ ] **Step 3: 更新 roadmap 文档**

在 `docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md` 里追加一条记录(参照文档里其它条目的写法)，内容至少包含:

- 做了什么(多组快捷栏、拖拽绑定、悬浮背包面板、手搓/蓝图占位面板、玩家背包 60→200)。
- 发现的键位冲突(`E` 被 `player_mine` 占用，改用 `Tab`)。
- 验收结果(F5 checklist 12 项的通过情况)。
- 明确排除的范围(背包内部拖拽重排、机器面板、真蓝图系统、快捷栏持久化、小地图真实渲染、2×3 快捷按钮功能、手搓面板交互)留给以后单独立项。

这一步没有固定代码模板，参照文档里"建造成本机制"那条记录的详略程度写。

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md
git commit -m "docs(roadmap): 记录快捷栏+背包/生产面板落地"
```
