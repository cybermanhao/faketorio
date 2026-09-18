using System;
using System.Collections.Generic;
using Godot;
using Faketorio.Sim.Commands;
using Faketorio.Sim.Prototypes;

namespace Faketorio.Game;

/// 建造命令:左键放"手持"物品,右键拆。无 UI 节点,全部立即模式(WorldView 画)。
/// 只读 sim + Submit,不直接改任何 sim 状态。
///
/// 拒绝检测:命令要到下一次 Sim.Step()(在 SimHost._Process 里)才 apply,
/// 所以这里每帧轮询 Sim.RejectedCommandCount,比上一帧大就闪一下红。
///
/// "手持"模型(替换了原来的拖拽绑定快捷栏方案):HeldItemProtoId 是唯一的"接下来要
/// 建造什么"来源,左键点地图永远建这个。设置手持的两条路径——(1)选中快捷栏格子
/// (数字键/点击),HeldFromBackpackSlot 清空;(2)点选背包里的物品,HeldFromBackpackSlot
/// 记录来源格下标。手持是粘性的:建造/绑定之后不自动清空,直到你选了别的、或者这个
/// 物品在背包里彻底用完(用完后 sim 自然拒绝,走已有的红闪逻辑,不做特殊清空判断)。
public partial class BuildController : Node
{
    public (int X, int Y) HoverTile { get; private set; }
    public bool LastCommandRejected { get; private set; }

    private SimHost _host = null!;
    private CameraController _cam = null!;
    private int _rejectedSeen;
    private double _flashRemaining;
    private bool _demolishHeld;
    private (int X, int Y) _demolishTile;

    // ---- 快捷栏状态 ----
    public int ActiveGroup { get; private set; }
    public int SelectedSlot { get; private set; }
    public byte SelectedRotation { get; private set; }
    public bool GroupPanelExpanded { get; private set; }
    public bool InventoryOpen { get; private set; }
    public IReadOnlyList<int[]> HotbarGroups => _hotbarGroups;

    // ---- 手持状态 ----
    public int? HeldItemProtoId { get; private set; }
    public int? HeldFromBackpackSlot { get; private set; }

    // ---- 背包面板状态 ----
    public float PanelHeightFraction { get; private set; } = 1f;
    public float ScrollOffsetRows { get; private set; }
    public int ContextTab { get; private set; }   // 0 = 建筑, 1 = 中间产品

    private bool _resizingPanel;

    // 背包格子"按下"了但还没松开——用于区分这是一次点击(选中/应用/取消)还是一次
    // 拖拽(交换/合并/移动),按松开落在哪个格子来判定,见 HandleBackpackRelease。
    // 故意不用像素距离阈值:手一抖松开点偏了几像素、但仍在同一个格子范围内,应该
    // 还是算点击——阈值判定在格子够大、抖动幅度不确定的情况下容易把轻微手抖误判成
    // "拖拽到了同一格"(结果两边都不触发,点击被吞掉),按格子边界判反而更稳妥。
    private int? _backpackPressSlot;

    private readonly List<int[]> _hotbarGroups = new();

    internal const float GroupPanelItemHeight = 22f;

    private int[] NewEmptyGroup()
    {
        var g = new int[HotbarLayout.SlotsPerGroup];
        Array.Fill(g, -1);
        return g;
    }

    public override void _Ready()
    {
        _host = GetNode<SimHost>("/root/SimHost");
        _cam = GetNode<CameraController>("../CameraController");
        _rejectedSeen = _host.Sim.RejectedCommandCount;
        _hotbarGroups.Add(NewEmptyGroup());
    }

    public override void _Process(double delta)
    {
        HoverTile = _cam.WorldXform.ScreenToTile(GetViewport().GetMousePosition().ToCore());

        int rejectedNow = _host.Sim.RejectedCommandCount;
        if (rejectedNow > _rejectedSeen) _flashRemaining = 0.15;   // 上一帧 Step() 里刚拒了命令
        _rejectedSeen = rejectedNow;

        if (_flashRemaining > 0) _flashRemaining -= delta;
        LastCommandRejected = _flashRemaining > 0;

        UpdateDemolish();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } key)
        {
            int? slot = DigitKeyToSlot(key.Keycode);
            if (slot is int s)
            {
                SelectHotbarSlot(s);
                GetViewport().SetInputAsHandled();
                return;
            }
            if (key.Keycode == Key.R)
            {
                SelectedRotation = (byte)((SelectedRotation + 1) % 4);
                GetViewport().SetInputAsHandled();
                return;
            }
            if (key.Keycode == Key.E)
            {
                InventoryOpen = !InventoryOpen;
                _backpackPressSlot = null;   // 关面板时手上正按着的背包格直接作废,不留到下次松开
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        if (_resizingPanel && e is InputEventMouseMotion rmm)
        {
            var viewportForResize = GetViewport().GetVisibleRect().Size;
            var panelForResize = HotbarLayout.InventoryPanel(viewportForResize, 1f);
            float delta = -rmm.Relative.Y / Mathf.Max(1f, panelForResize.Size.Y);
            PanelHeightFraction = Mathf.Clamp(PanelHeightFraction + delta, 0f, 1f);
        }

        if (InventoryOpen && !_backpackPressSlot.HasValue && e is InputEventMouseButton wheel && wheel.Pressed
            && (wheel.ButtonIndex == MouseButton.WheelUp || wheel.ButtonIndex == MouseButton.WheelDown))
        {
            var viewport = GetViewport().GetVisibleRect().Size;
            var invPanel = HotbarLayout.InventoryPanel(viewport, PanelHeightFraction);
            if (invPanel.HasPoint(wheel.Position))
            {
                int slotCount = _host.Sim.Player.Inventory.SlotCount;
                int totalRows = Mathf.CeilToInt(slotCount / (float)HotbarLayout.ColumnCount);
                HotbarLayout.InventoryGrid(invPanel, slotCount, HotbarLayout.ColumnCount, ScrollOffsetRows, out int visibleRowsForScroll);
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

            if (relb.ButtonIndex == MouseButton.Left && _backpackPressSlot.HasValue)
            {
                HandleBackpackRelease(relb.Position);
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        // 到这里,剩下的只可能是"按下"事件——所有"松开"事件(背包拖拽释放/resize 结束)
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
                    HandleHotbarSlotClick(i);
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }

            if (InventoryOpen)
            {
                if (_cam.Mode == CameraMode.Follow)
                {
                    var tabs = HotbarLayout.ContextPanelTabs(HotbarLayout.ContextPanel(viewport));
                    for (int i = 0; i < tabs.Length; i++)
                    {
                        if (tabs[i].HasPoint(mb.Position))
                        {
                            ContextTab = i;
                            GetViewport().SetInputAsHandled();
                            return;
                        }
                    }
                }

                var invPanel = HotbarLayout.InventoryPanel(viewport, PanelHeightFraction);
                var inv = _host.Sim.Player.Inventory;
                int? hitSlot = HotbarLayout.HitTestInventoryGrid(invPanel, inv.SlotCount, HotbarLayout.ColumnCount, ScrollOffsetRows, mb.Position);
                if (hitSlot is int slotIndex)
                {
                    var stack = inv[slotIndex];
                    if (stack.IsEmpty)
                    {
                        // 空格:如果手上正拿着背包来源的东西,点空格 = 放到这里(移动)。
                        if (HeldFromBackpackSlot is int fromSlot)
                        {
                            SubmitMoveInventorySlot(fromSlot, slotIndex);
                            HeldFromBackpackSlot = null;
                            HeldItemProtoId = null;
                        }
                        // 手上没拿背包来源的东西(可能是没拿,也可能是快捷栏来源的手持):
                        // 点空格没有意义,不做任何事——但仍然要吃掉这次点击,不落到下面的建造分支。
                    }
                    else
                    {
                        // 非空格:按下先记录,松开时再判断是点击还是拖拽(见 HandleBackpackRelease)。
                        _backpackPressSlot = slotIndex;
                    }
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

                if (invPanel.HasPoint(mb.Position) || HotbarLayout.ContextPanel(viewport).HasPoint(mb.Position))
                {
                    // 点在背包/手搓面板空白处(不是格子、不是 handle、不是页签):
                    // 不建造,直接吃掉这次点击。
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }

            if (HeldItemProtoId is int heldId)
            {
                var (x, y) = _cam.WorldXform.ScreenToTile(mb.Position.ToCore());
                _host.Submit(new Command { Type = CommandType.BuildFromInventory, ProtoId = heldId, X = x, Y = y, Rotation = SelectedRotation });
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
                    // 解绑的正是当前手持所在的这个快捷栏格:手持必须跟着刷新,不然会
                    // 悬空指向一个已经不存在的绑定(格子画出来是空的,但地图上左键还在建它)。
                    if (i == SelectedSlot && !HeldFromBackpackSlot.HasValue) SelectHotbarSlot(i);
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }
        }
    }

    // 选中一个快捷栏格子:手持切到这个格子绑定的物品(可能是空),来源标记为快捷栏
    // (清空 HeldFromBackpackSlot)。数字键和点击格子共用这个方法。
    private void SelectHotbarSlot(int slot)
    {
        SelectedSlot = slot;
        HeldFromBackpackSlot = null;
        int bound = _hotbarGroups[ActiveGroup][slot];
        HeldItemProtoId = bound >= 0 ? bound : null;
    }

    // 点击快捷栏格子:如果当前手持来自背包点选、且这个物品能建造(有 PlaceResult),
    // 顺带把它绑定到这个格子上。不管绑没绑成,点击之后都切到这个格子的选中态——
    // 这样"绑定失败"(比如拖了个原材料)也有明确、一致的落点,不是卡在中间状态。
    private void HandleHotbarSlotClick(int slot)
    {
        if (HeldFromBackpackSlot.HasValue && HeldItemProtoId is int heldId)
        {
            var protos = _host.Sim.Prototypes;
            var itemProto = protos.GetById(heldId) as ItemPrototype;
            bool bindable = itemProto?.PlaceResult is not null && protos.TryGetEntityByName(itemProto.PlaceResult, out _);
            if (bindable) _hotbarGroups[ActiveGroup][slot] = heldId;
        }
        SelectHotbarSlot(slot);
    }

    // 背包格子松开:按松开点落在哪个格子来判定这是拖拽还是点击——落在按下的同一格
    // (或者压根没落在任何格子上)= 点击;落在另一个格子上 = 拖拽,直接提交交换/合并/
    // 移动,不影响当前的点选中状态(拖拽是一次性的、自包含的手势,跟"选中"是两回事)。
    private void HandleBackpackRelease(Vector2 releasePos)
    {
        int pressSlot = _backpackPressSlot!.Value;
        _backpackPressSlot = null;

        var viewport = GetViewport().GetVisibleRect().Size;
        var invPanel = HotbarLayout.InventoryPanel(viewport, PanelHeightFraction);
        var inv = _host.Sim.Player.Inventory;
        int? releaseSlot = HotbarLayout.HitTestInventoryGrid(invPanel, inv.SlotCount, HotbarLayout.ColumnCount, ScrollOffsetRows, releasePos);

        if (releaseSlot is int rs && rs != pressSlot)
        {
            // 拖拽动到的两个格子里,如果有一个正是当前点选中的来源格,这次拖拽会改变
            // 它的内容(交换/合并),留着旧的选中状态会指向一个已经不对的物品——必须
            // 先清掉,不能让"选中"粘在一个刚被这次拖拽改写过的格子上。
            if (HeldFromBackpackSlot == pressSlot || HeldFromBackpackSlot == rs)
            {
                HeldFromBackpackSlot = null;
                HeldItemProtoId = null;
            }
            SubmitMoveInventorySlot(pressSlot, rs);
            return;
        }

        if (releaseSlot is null) return;   // 松开在背包格子范围之外:整个手势作废,不产生任何效果

        // 松开落回了按下的同一格 = 点击(选中/应用到已选中目标/取消选中)。
        if (HeldFromBackpackSlot == pressSlot)
        {
            // 再点一次已经选中的那一格 = 取消选中。
            HeldFromBackpackSlot = null;
            HeldItemProtoId = null;
            return;
        }

        if (HeldFromBackpackSlot is int fromSlot)
        {
            // 已经选中了别的格子,这次点击 = 把它放到这一格(合并/交换)。
            SubmitMoveInventorySlot(fromSlot, pressSlot);
            HeldFromBackpackSlot = null;
            HeldItemProtoId = null;
            return;
        }

        // 之前没有选中任何背包格子:选中这一格(这里一定非空——空格在按下时就已经
        // 走了另一条分支,根本不会设置 _backpackPressSlot)。
        var stack = inv[pressSlot];
        HeldFromBackpackSlot = pressSlot;
        HeldItemProtoId = stack.ItemProtoId;
    }

    private void SubmitMoveInventorySlot(int fromSlot, int toSlot)
        => _host.Submit(new Command { Type = CommandType.MoveInventorySlot, X = fromSlot, Y = toSlot });

    private static int? DigitKeyToSlot(Key k) => k switch
    {
        Key.Key1 => 0, Key.Key2 => 1, Key.Key3 => 2, Key.Key4 => 3, Key.Key5 => 4,
        Key.Key6 => 5, Key.Key7 => 6, Key.Key8 => 7, Key.Key9 => 8, Key.Key0 => 9,
        _ => null,
    };

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
        SelectHotbarSlot(0);
        GroupPanelExpanded = false;
        return true;
    }

    // 长按右键 = 挖矿/拆除——复用 MineStart/MineStop 同一套 sim 逻辑(距离检查 +
    // 进度累积 + minableResult 物品归还),对矿脉和可拆实体都生效。原来 PlayerInputController
    // 还有一条按住 E 键的手挖输入路径,走的是同一个 sim 命令;E 键现在改绑给背包开关,
    // 那条输入路径已下线,长按右键是唯一入口。
    private void UpdateDemolish()
    {
        bool held = Input.IsMouseButtonPressed(MouseButton.Right);
        if (!held)
        {
            if (_demolishHeld) { _host.Submit(new Command { Type = CommandType.MineStop }); _demolishHeld = false; }
            return;
        }

        if (!_demolishHeld || HoverTile != _demolishTile)
        {
            _host.Submit(new Command { Type = CommandType.MineStart, X = HoverTile.X, Y = HoverTile.Y });
            _demolishHeld = true;
            _demolishTile = HoverTile;
        }
    }
}
