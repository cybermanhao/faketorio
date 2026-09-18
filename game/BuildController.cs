using System;
using System.Collections.Generic;
using Godot;
using Faketorio.Sim.Commands;
using Faketorio.Sim.Prototypes;

namespace Faketorio.Game;

/// 最小放置命令:左键放快捷栏选中槽绑定的物品,右键拆。无 UI。
/// 只读 sim + Submit,不直接改任何 sim 状态。
///
/// 拒绝检测:命令要到下一次 Sim.Step()(在 SimHost._Process 里)才 apply,
/// 所以这里每帧轮询 Sim.RejectedCommandCount,比上一帧大就闪一下红。
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

        if (mb.ButtonIndex != MouseButton.Left) return;

        int boundItemId = _hotbarGroups[ActiveGroup][SelectedSlot];
        if (boundItemId >= 0)
        {
            var (x, y) = _cam.WorldXform.ScreenToTile(mb.Position.ToCore());
            _host.Submit(new Command { Type = CommandType.BuildFromInventory, ProtoId = boundItemId, X = x, Y = y, Rotation = SelectedRotation });
        }

        GetViewport().SetInputAsHandled();
    }

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
        SelectedSlot = 0;
        GroupPanelExpanded = false;
        return true;
    }

    // 长按右键 = 拆除——复用手挖(MineStart/MineStop)同一套 sim 逻辑(距离检查 +
    // 进度累积 + minableResult 物品归还),不新写机制,同 PlayerInputController.UpdateMining()。
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
