using Godot;

namespace Faketorio.Game;

// 只在回放模式(SimHost.IsReplayMode)下做事:等渲染追上重放好的模拟状态、
// 摆相机到指定坐标/缩放、按指定宽高截一张 PNG、退出进程。正常游玩场景下
// (IsReplayMode == false)这个节点的 _Process 第一行就直接返回,不影响任何
// 正常玩法逻辑。
public partial class ReplayCapture : Node
{
    private SimHost _host = null!;
    private CameraController _camera = null!;
    private int _frameCount;
    private bool _cameraPositioned;

    // 相机定位之后再等这么多帧,让 WorldView._Draw() 按新的相机参数重新画一遍。
    // spike 验证过 30 帧的余量足够,这里保留同样的余量。
    private const int FramesAfterCameraMove = 30;

    public override void _Ready()
    {
        _host = GetNode<SimHost>("/root/SimHost");
        _camera = GetParent().GetNode<CameraController>("CameraController");
    }

    public override void _Process(double _delta)
    {
        if (!_host.IsReplayMode) return;

        if (_host.ReplayFailed)
        {
            GD.PushError("回放失败,放弃截图(不产出 PNG)。");
            GetTree().Quit(2);
            return;
        }

        if (!_cameraPositioned)
        {
            double x = FindArgDouble("--camera-x", 0);
            double y = FindArgDouble("--camera-y", 0);
            double zoom = FindArgDouble("--zoom", 32);
            int width = (int)FindArgDouble("--width", 1280);
            int height = (int)FindArgDouble("--height", 720);

            DisplayServer.WindowSetSize(new Vector2I(width, height));
            _camera.SetCameraForScreenshot(x, y, zoom);
            _cameraPositioned = true;
            _frameCount = 0;
            return;
        }

        _frameCount++;
        if (_frameCount < FramesAfterCameraMove) return;

        string? outPath = FindArgValue("--out");
        if (outPath is null)
        {
            GD.PushError("回放模式缺少 --out 参数,无法保存截图。");
            GetTree().Quit(1);
            return;
        }

        var img = GetViewport().GetTexture().GetImage();
        var err = img.SavePng(outPath);
        if (err != Error.Ok) GD.PushError($"截图保存失败: {err}");
        GetTree().Quit(err == Error.Ok ? 0 : 1);
    }

    private static string? FindArgValue(string key)
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == key) return args[i + 1];
        return null;
    }

    private static double FindArgDouble(string key, double fallback)
    {
        string? raw = FindArgValue(key);
        return raw is not null && double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : fallback;
    }
}
