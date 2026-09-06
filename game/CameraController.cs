using Godot;
using Faketorio.Presentation.Core;

namespace Faketorio.Game;

public enum CameraMode { Follow, Free }

/// 双模相机控制器。**它本身不是 Camera2D** —— 场景里根本没有 Camera2D。
/// 唯一的坐标映射是 WorldXform(WorldTransform):每帧把中心 tile / 每格像素 /
/// 视口尺寸喂进去,WorldView 完全靠它把 world 坐标算成屏幕像素。
///
/// Follow:锁玩家,近景窄缩放区间,切回时 0.25s 缓动滑回。
/// Free(地图模式):中键拖动平移,缩放区间放宽。M 键切换。
public partial class CameraController : Node
{
    public CameraMode Mode { get; private set; } = CameraMode.Follow;

    public WorldTransform WorldXform { get; } = new();

    private ICameraTarget? _followTarget;
    private SimHost _host = null!;

    // Follow 近景窄区间;Free 放宽
    private const double FollowMinPpt = 32, FollowMaxPpt = 64;
    private const double FreeMinPpt = 6, FreeMaxPpt = 64;
    private const double SnapDuration = 0.25;
    // Free 模式 WASD 平移速度,tile/秒(固定 tile 速度,拉远时每秒扫过更多屏幕,可接受)。
    private const double FreePanTilesPerSecond = 18.0;

    private double _ppt = 48;
    private bool _panning;

    // 相机中心的 world tile 坐标(可含小数)。以前借用 Camera2D.GlobalPosition,现在自己持有。
    private Vector2 _centerTile;

    // 切回 Follow 时的缓动状态
    private bool _snapping;
    private double _snapElapsed;
    private Vector2 _snapFrom;

    public override void _Ready()
    {
        _host = GetNode<SimHost>("/root/SimHost");
        _followTarget = new PlayerCameraTarget(_host);
        _centerTile = TargetTile();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        // InputMap 里有 map_toggle 就用它;没有就直接认 M 键。
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
            // 拖多少屏幕像素,世界中心就反向移动等量:px / (px per tile) = tile。
            _centerTile -= mm.Relative / (float)_ppt;
        }
    }

    private void ToggleMode()
    {
        if (Mode == CameraMode.Follow)
        {
            Mode = CameraMode.Free;
            _panning = false;
            _snapping = false;
            _ppt = System.Math.Clamp(_ppt, FreeMinPpt, FreeMaxPpt);
        }
        else
        {
            Mode = CameraMode.Follow;
            _panning = false;
            _ppt = System.Math.Clamp(_ppt, FollowMinPpt, FollowMaxPpt);
            _snapping = true;
            _snapElapsed = 0;
            _snapFrom = _centerTile;
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
        if (Mode == CameraMode.Follow)
        {
            var target = TargetTile();
            if (_snapping)
            {
                _snapElapsed += delta;
                float k = (float)System.Math.Clamp(_snapElapsed / SnapDuration, 0.0, 1.0);
                // sine ease-out
                float eased = Mathf.Sin(k * Mathf.Pi * 0.5f);
                _centerTile = _snapFrom.Lerp(target, eased);
                if (k >= 1f) _snapping = false;
            }
            else
            {
                _centerTile = target;
            }
        }

        // !_snapping is always true here (snap only runs in Follow) — kept for intent
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

        var vp = GetViewport().GetVisibleRect().Size;
        WorldXform.PixelsPerTile = _ppt;
        WorldXform.CameraCenterTile = new Vec2(_centerTile.X, _centerTile.Y);
        WorldXform.ViewportSizePx = new Vec2(vp.X, vp.Y);
    }
}
