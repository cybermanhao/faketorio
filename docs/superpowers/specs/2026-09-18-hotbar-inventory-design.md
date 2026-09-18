# 快捷栏 + 背包/生产面板设计

**日期**：2026-09-18
**状态**：待用户审阅
**范围**：多组可切换快捷栏（10 格/组，拖拽绑定，长按/中键解绑）+ 悬浮式背包面板（只读，20 列可滚动，可收缩）+ 手搓面板（只读展示配方）+ Free 模式下的蓝图面板占位 + 小地图占位 + 玩家背包容量扩到 200

配套参考：[`docs/superpowers/specs/2026-09-02-m1-remaining-roadmap.md`](2026-09-02-m1-remaining-roadmap.md)（"表现层后续"候选之一：完整命令 UI）；[`docs/superpowers/specs/2026-09-17-build-cost-design.md`](2026-09-17-build-cost-design.md)（`BuildFromInventory` 命令，本专项直接在它上面接选择界面，不新增/修改 sim 命令）

设计过程用了 Visual Companion 走了 3 轮布局迭代（v1→v3），最终布局已经用户确认，本 spec 是对那轮迭代结果的文字化 + 技术设计补完，不是从零重新设计。

---

## 0. 背景

`BuildFromInventory` 落地后，`game/BuildController.cs` 左键固定提交 `wooden-chest`，没有"选建筑"这个界面；玩家也看不到自己背包里有什么。这次要把这两块补上，同时确定了几个比最初设想更大的范围点：多组快捷栏、可滚动大背包、手搓面板、以及两个明确占位不做的东西（小地图、蓝图面板）。

---

## 1. 范围

### 这轮做

1. **快捷栏**：10 格/组，支持多组（组数不设硬上限，运行时可"+ 新建"），左侧一个按钮显示当前组号，点击展开一个纵向小面板选组切换。数字键 1-0 始终对应**当前组**的 10 格。鼠标点击格子 = 等效数字键。选中格描边高亮 + 右下角小箭头显示朝向。`R` 键循环旋转当前选中项（0→1→2→3→0）。
2. **拖拽绑定**：从背包面板拖一个格子到快捷栏格子 = 把该物品类型绑定到那个快捷栏格（只对带 `PlaceResult` 的物品生效，其它物品拖了无效果，不报错不提示，就是没反应）。中键点快捷栏格 = 解绑，变回空格。
3. **背包面板**：`E` 键开关，浮在游戏画面上（不是全屏覆盖，四周能看到游戏世界），只读展示 `Player.Inventory`，20 列起、纵向可滚动，面板高度可拖拽收缩（不改变列数，只改变可见行数）。
4. **手搓面板**：Free/Follow 都固定显示在背包右侧（这轮 Follow 模式下的默认内容），只读列出所有 `category == "crafting"` 的配方（复用 `HandCraft`/`CraftEnqueue` 现有的 category 过滤逻辑判断"能不能手搓"，跟 sim 层判断标准一致），不做交互（不能点击排队，不显示队列进度）。
5. **右侧 2×3 快捷操作按钮**：纯占位，画出 6 个空按钮，不接任何命令，点击无反应。
6. **小地图**：右上角占一块空间，画个占位框 + 文字，不做任何实际渲染。
7. **蓝图面板**：Free（地图）模式下，背包右侧从"手搓面板"切换成"蓝图面板"，同样纯占位（文字说明"蓝图系统待建"），不接任何命令。蓝图本身（sim 层的蓝图数据模型/命令）**完全不在这次范围内**，是否要做、什么时候做留给以后单独立项。
8. **玩家背包容量扩到 200**（`data/base/player.json` 的 `inventorySize: 60 → 200`）——sim 层数据变更，见 §4。

### 不做（明确排除）

- 背包内部格子间拖拽重排
- 机器面板（点开箱子/机器看库存、设配方）
- 蓝图系统本身（sim 层 + UI）
- 快捷栏分组绑定的存档持久化（没有存档系统，这次是纯运行时状态，重开游戏回到默认组）
- 小地图实际渲染
- 2×3 快捷按钮任何实际功能
- 手搓面板的交互（排队、取消、进度显示）——只做只读列表

---

## 2. 数据层变更

`data/base/player.json` 的 `inventorySize` 从 `60` 改成 `200`。

**已知连带影响**（走跟 [`2026-09-17-build-cost-design.md`](2026-09-17-build-cost-design.md) 里 Task 2 同一类问题一样的流程处理）：

- `Player.Inventory.WriteState`（`sim/Faketorio.Sim/Items/Inventory.cs:117-125`）把每个槽位（哪怕是空的）都写进状态哈希——`_slots.Length` 从 60 变 200，序列化字节数变了，**状态哈希必然改变**。
- 需要 `--update-golden` 重新生成 `bench/golden.json`，并重新校准 `baselineNsPerTick`（`Inventory` 更大不代表每 tick 变慢多少，但既然要重新基线,顺手测一下)。
- 已知有一处硬编码会挂：`sim/Faketorio.Sim.Tests/PrototypeLoaderTests.cs:195`（`LoadsPlayerPrototype` 测试）断言 `p.InventorySize == 60`,需要改成 `200`。
- 写 plan 时要求实现者**实际跑一遍全量测试**，把因为这个改动新增的失败逐条排查是不是这同一个原因（不能假设只有这一处，参考 build-cost 那轮"以为只有一处结果有 20 处"的教训），确认没有真 bug 才批量更新。

物品的 `stackSize` **不需要改**——`data/base/items.json` 里现有 14 个物品全部已经显式设了 `stackSize`（50 或 100），`ItemPrototype.StackSize` 的默认值本身也是 50，没有缺口。

---

## 3. 快捷栏：数据结构与交互

### 3.1 状态（`game/BuildController.cs`，或者拆出一个新的 `HotbarController.cs`——见 §6 文件结构）

```csharp
// 一组 = 10 个可空绑定槽(每槽存物品原型 id,-1 = 空)
private const int SlotsPerGroup = 10;
private readonly List<int[]> _hotbarGroups = new();   // 每个元素是长度 10 的 int[]
private int _activeGroup = 0;
private int _selectedSlot = 0;      // 当前组内选中的槽位下标(0-9)
private byte _selectedRotation = 0; // 0-3,当前选中项的朝向
```

初始状态：`_hotbarGroups` 只有一组（组 0），10 槽全空（`-1`）。运行时用户拖拽绑定后填入物品原型 id。**不做任何默认预填**——之前 mockup 里画的"1 号槽是 wooden-chest"只是示意图,实际上线时快捷栏是空的,靠用户自己从背包拖东西进去绑定(跟 Factorio 一致)。

### 3.2 输入

- 数字键 `1`-`0`（`0` 对应第 10 格）：`_selectedSlot` 直接跳到对应下标（本组内）。
- 鼠标左键点击某个快捷栏格：等效于按对应数字键。
- `R` 键：`_selectedRotation = (byte)((_selectedRotation + 1) % 4)`。
- 鼠标左键点击游戏世界（原有的建造点击）：如果 `_hotbarGroups[_activeGroup][_selectedSlot]` 不是 `-1`，提交 `BuildFromInventory`（`ProtoId` = 该槽绑定的物品 id，`Rotation` = `_selectedRotation`）；如果是 `-1`（空槽），不提交任何命令（左键点地图无效果，不闪红——闪红只在命令真的被 sim 拒绝时触发）。
- 鼠标中键点击某个快捷栏格：`_hotbarGroups[_activeGroup][该格下标] = -1`（解绑）。
- 左侧翻页按钮（显示当前组号）：点击展开一个小面板，纵向列出所有组 + "+ 新建"；点某组 = `_activeGroup` 切过去，按钮文字更新成新组号；点"+ 新建" = `_hotbarGroups.Add(new int[SlotsPerGroup] 全 -1)`，`_activeGroup` 跳到新组。

### 3.3 拖拽绑定（背包格 → 快捷栏格）

**状态用 `int? _dragItemProtoId`（可空），不是 `int`**——`null` 表示"当前没有在拖东西"，这跟快捷栏槽位用 `-1` 表示"空槽/解绑"是两个不同的语义，不能共用同一个哨兵值。如果共用 `-1`，从背包**空格**按下鼠标会把 `_dragItemProtoId` 设成 `-1`，之后如果释放在某个已绑定的快捷栏格上，`_hotbarGroups[...][...] = _dragItemProtoId` 会把 `-1` 写进去——效果等同于中键解绑，但玩家的操作明明是"从空格拖了个空气过去"，不是"我要解绑这一格"。两件事撞在一起是真 bug，必须用 `int?` 区分开。

- 鼠标在背包格上按下：
  - 该格**非空**（`ItemStack.ItemProtoId != 0`）→ `_dragItemProtoId = 该物品原型 id`，开始拖拽。
  - 该格**为空**（`ItemProtoId == 0`）→ **不开始拖拽**，`_dragItemProtoId` 保持 `null`，后续的"拖动中""释放"两步整个跳过（等同于什么都没发生）。这是唯一正确的处理——空格没有"物品"可言，谈不上"拖了个不可放入快捷栏的东西"，从语义上就该在按下这一步直接短路,不进入拖拽状态机。
- 拖动中（仅当 `_dragItemProtoId.HasValue`）：画一个跟随鼠标的幽灵图标（复用 `RenderPalette.ForEntity` 的配色 + 物品名文字，跟现有渲染风格一致）。
- 释放在快捷栏格上（仅当 `_dragItemProtoId.HasValue`）：如果该物品有 `PlaceResult`，`_hotbarGroups[_activeGroup][目标格下标] = _dragItemProtoId.Value`；否则（没有 `PlaceResult`）不绑定，幽灵图标直接消失，无提示。两种情况都要把 `_dragItemProtoId` 重置回 `null`。
- 释放在非快捷栏区域（比如背包内部、空白处）：不绑定，幽灵图标消失，`_dragItemProtoId` 重置回 `null`。

---

## 4. 背包面板

### 4.1 数据

纯只读镜像 `sim.Player.Inventory`——每帧（或面板打开时）读 `for (int i = 0; i < inv.SlotCount; i++) inv[i]`，取 `ItemStack.ItemProtoId`/`Count`，`ItemProtoId == 0` 视为空槽。不缓存、不做增量更新，`E` 键打开时和打开期间每帧都是现读现画（这个项目的既有原则——立即模式渲染，零 mutation）。

### 4.2 布局

- `E` 键切换 `_inventoryOpen` bool，控制面板 `visible`。
- 格子网格固定 **20 列**（`ColumnCount = 20`，不随窗口宽度重新计算），行数 = `Ceiling(inv.SlotCount / 20.0)`——`SlotCount` 变了（比如以后 sim 侧再扩容）行数自动跟着变，不需要改 UI 代码；`inventorySize = 200` 时正好是 10 行，跟 mockup 里"20×10"对上。
- 面板宽度 = `Max(MinPanelWidth, WindowWidth * PanelWidthRatio)`，其中 `MinPanelWidth` 是一个固定像素下限（例如 20 列 × 每格最小可点击/可读尺寸 24px + 列间距，建议下限 **560px**，具体数值由实现阶段结合实际字体/图标渲染效果微调，不在本规范锁死）；`PanelWidthRatio` 沿用 mockup 中的比例（约窗口宽度的 44%）。格子大小 = 面板内容宽度 / 20（随面板宽度缩放，但面板宽度本身有下限，所以格子大小也有隐含下限，不会无限缩小到不可点击/不可读）。
- 当 `WindowWidth * PanelWidthRatio < MinPanelWidth` 时（极端小窗口），面板按 `MinPanelWidth` 渲染，允许超出"四周留出游戏背景"的宽松布局预期、甚至贴近或触碰窗口边缘——这是可接受的降级行为，不需要为此做额外的响应式重排设计。背包面板本身不设最小窗口尺寸硬性拦截（不锁定/隐藏面板），只保证格子不会缩到不可用。
- 面板高度可拖拽收缩（顶部一个 resize handle，拖动改变可见行数，超出部分靠垂直滚动查看，滚动条本身可以用 Godot 现成的 `ScrollContainer` 节点，不需要手写滚动逻辑）。
- 面板悬浮在主区偏左侧，不铺满整个窗口——四周留出能看到游戏世界背景的空间（`WorldView` 的 `_Draw` 不受面板打开与否影响，正常渲染，面板只是叠加在上层的 UI）。

---

## 5. 手搓面板 / 蓝图面板（背包右侧的上下文面板）

- 根据 `CameraController.Mode` 切换显示内容：`Follow` → 手搓面板；`Free` → 蓝图面板。
- **手搓面板**：遍历 `sim.Prototypes` 里所有 `category == "crafting"` 的 `RecipePrototype`（判断标准跟 `Simulation.cs` 里 `CraftEnqueue` 命令用的 `recipe.Category != "crafting"` 拒绝逻辑保持一致，不能各写一套），每条画一行：配方名 + 用 `RenderPalette.ForEntity`/物品色块表示的产物图标。不显示"能不能造""缺什么材料"这类需要读玩家库存比对的信息——这轮就是个静态列表。
- **蓝图面板**：一段说明文字("蓝图系统待建,见 roadmap"），没有任何交互元素。

---

## 6. 文件结构

- `game/BuildController.cs`：扩展现有类——加 §3.1 的快捷栏状态、§3.2 的输入处理、§3.3 的拖拽绑定逻辑。这个文件已经在管"建造相关输入"，快捷栏选择属于同一个职责范围，不拆新文件。
- `game/WorldView.cs`：新增背包面板/手搓面板/蓝图面板/快捷栏/小地图占位/2×3 按钮的绘制代码——延续现状"`BuildController` 管状态，`WorldView` 管画"的分工。给这部分新增的绘制代码单独抽几个私有方法（`DrawHotbar`、`DrawInventoryPanel`、`DrawContextPanel`），不要全堆进 `_Draw()` 主体，这个文件已经不小了。
- 不新增 `.tscn` 场景节点（背包面板的可滚动区域如果用 `ScrollContainer`，需要在 `Main.tscn` 里加节点——具体要不要用 Godot 原生 `ScrollContainer`/`Control` 节点树而不是纯 `_Draw()` 立即模式画背包面板，这是个实现期要定的技术选型问题，见 §7 开放问题）。

---

## 7. 开放问题（写进 spec 但留给写 plan/实现阶段核实）

- **背包面板到底用立即模式画,还是搭一棵 `Control`/`ScrollContainer` 节点树？** 这个项目目前所有 UI 都是 `WorldView._Draw()` 立即模式画的(色块+文字),没有用过 Godot 的 `Control` 节点体系。背包面板需要"可滚动"这个交互，立即模式手写滚动逻辑（裁剪区域 + 滚动偏移量 + 鼠标滚轮事件）是可以做但比较繁琐；用 Godot 原生 `ScrollContainer` + `GridContainer` 节点更省事，但意味着背包面板这块要跳出"立即模式渲染"这个项目一直以来的既有模式，混用两套 UI 范式。这个技术选型建议在写实施计划前先花一小段时间确认，不在这份 spec 里拍板。
- **拖拽的"幽灵图标跟随鼠标"** 用 Godot 的什么机制画（`_Draw()` 里跟着 `GetViewport().GetMousePosition()` 现画,还是一个独立的 `Control`/`Sprite2D` 节点跟着鼠标移动）——如果背包面板走 `Control` 节点体系,这个也应该跟着用节点体系里现成的拖拽支持（Godot `Control` 有内置的 `_GetDragData`/`_CanDropData`/`_DropData` 虚方法），不要重新发明。取决于上一条的选型结果。
