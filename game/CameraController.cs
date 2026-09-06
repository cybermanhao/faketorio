using Godot;
using Faketorio.Presentation.Core;

namespace Faketorio.Game;

public enum CameraMode { Follow, Free }

/// 双模相机。Follow:锁玩家,近景窄缩放区间。Free(地图模式):中键拖动平移,
/// 缩放区间放宽到能一眼看完整片基地。M 键切换,切回 Follow 时 tween 滑回玩家。
///
/// 注意:Camera2D 自身的变换在这里**不参与渲染** —— WorldView 是 TopLevel 的,
/// 完全靠 WorldXform(WorldTransform)算像素。GlobalPosition 只被当成
/// "相机中心的 world tile 坐标"这个纯数据来用,Zoom 恒为 1。
public partial class CameraController : Camera2D
{
    public CameraMode Mode { get; private set; } = CameraMode.Follow;

    // 刻意不叫 Transform:Node2D 已经有一个 Transform(Transform2D)属性,
    // 同名会遮蔽引擎属性(CS0108)并让 Godot 的属性表混乱。
    public WorldTransform WorldXform { get; } = new();

    private ICameraTarget? _followTarget;
    private SimHost _host = null!;

    // Follow 近景窄区间;Free 放宽
    private const double FollowMinPpt = 32, FollowMaxPpt = 64;
    private const double FreeMinPpt = 4, FreeMaxPpt = 64;

    private double _ppt = 48;
    private bool _panning;
    private Tween? _snapTween;

    public override void _Ready()
    {
        _host = GetNode<SimHost>("/root/SimHost");
        _followTarget = new PlayerCameraTarget(_host);
        Zoom = Vector2.One;
        GlobalPosition = TargetTile();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        // InputMap 里有 map_toggle 就用它;没有(project.godot 的 action 序列化
        // 在别的 Godot 版本上可能读不出来)就直接认 M 键。
        bool toggle = (InputMap.HasAction("map_toggle") && e.IsActionPressed("map_toggle"))
                      || e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.M };
        if (toggle) { ToggleMode(); return; }

        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed) Zoomstep(+1);
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed) Zoomstep(-1);
            else if (mb.ButtonIndex == MouseButton.Middle) _panning = mb.Pressed && Mode == CameraMode.Free;
        }

        if (e is InputEventMouseMotion mm && _panning && Mode == CameraMode.Free)
        {
            // 拖多少屏幕像素,世界就反向移动等量:px / (px per tile) = tile。
            GlobalPosition -= mm.Relative / (float)_ppt;
        }
    }

    private void ToggleMode()
    {
        if (Mode == CameraMode.Follow)
        {
            Mode = CameraMode.Free;
            _panning = false;
            _snapTween?.Kill();
            _snapTween = null;
            _ppt = System.Math.Clamp(_ppt, FreeMinPpt, FreeMaxPpt);
        }
        else
        {
            Mode = CameraMode.Follow;
            _panning = false;
            _ppt = System.Math.Clamp(_ppt, FollowMinPpt, FollowMaxPpt);
            _snapTween?.Kill();
            _snapTween = CreateTween();
            _snapTween.TweenProperty(this, "global_position", TargetTile(), 0.25)
                      .SetTrans(Tween.TransitionType.Sine)
                      .SetEase(Tween.EaseType.Out);
        }
    }

    private void Zoomstep(int dir)
    {
        double min = Mode == CameraMode.Follow ? FollowMinPpt : FreeMinPpt;
        double max = Mode == CameraMode.Follow ? FollowMaxPpt : FreeMaxPpt;
        _ppt = System.Math.Clamp(_ppt * (dir > 0 ? 1.2 : 1.0 / 1.2), min, max);
    }

    private Vector2 TargetTile()
    {
        var (sx, sy) = _followTarget?.WorldSub ?? (0L, 0L);
        return new Vector2(
            (float)(sx / (double)WorldTransform.SubTilesPerTile),
            (float)(sy / (double)WorldTransform.SubTilesPerTile));
    }

    public override void _Process(double delta)
    {
        if (Mode == CameraMode.Follow && (_snapTween is null || !_snapTween.IsRunning()))
            GlobalPosition = TargetTile();

        var vp = GetViewportRect().Size;
        WorldXform.PixelsPerTile = _ppt;
        WorldXform.CameraCenterTile = new Vec2(GlobalPosition.X, GlobalPosition.Y);
        WorldXform.ViewportSizePx = new Vec2(vp.X, vp.Y);
    }
}
