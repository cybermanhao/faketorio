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

    private bool _mineHeld;
    private (int X, int Y) _mineTile;

    public override void _Ready()
    {
        _host = GetNode<SimHost>("/root/SimHost");
        _cam = GetNode<CameraController>("../CameraController");
    }

    public override void _Process(double delta)
    {
        UpdateMovement();
        UpdateMining();
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
}
