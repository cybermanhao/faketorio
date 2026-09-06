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
