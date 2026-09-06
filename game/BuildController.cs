using Godot;
using Faketorio.Sim.Commands;
using Faketorio.Sim.Prototypes;

namespace Faketorio.Game;

/// 最小放置命令:左键放硬编码 proto(木箱),右键拆。无 UI。
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
    private int _chestProtoId;
    private int _rejectedSeen;
    private double _flashRemaining;

    public override void _Ready()
    {
        _host = GetNode<SimHost>("/root/SimHost");
        _cam = GetNode<CameraController>("../CameraController");
        _chestProtoId = _host.Sim.Prototypes.Get<ContainerPrototype>("wooden-chest").Id;
        _rejectedSeen = _host.Sim.RejectedCommandCount;
    }

    public override void _Process(double delta)
    {
        HoverTile = _cam.WorldXform.ScreenToTile(GetViewport().GetMousePosition().ToCore());

        int rejectedNow = _host.Sim.RejectedCommandCount;
        if (rejectedNow > _rejectedSeen) _flashRemaining = 0.15;   // 上一帧 Step() 里刚拒了命令
        _rejectedSeen = rejectedNow;

        if (_flashRemaining > 0) _flashRemaining -= delta;
        LastCommandRejected = _flashRemaining > 0;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventMouseButton mb || !mb.Pressed) return;
        if (mb.ButtonIndex != MouseButton.Left && mb.ButtonIndex != MouseButton.Right) return;

        var (x, y) = _cam.WorldXform.ScreenToTile(mb.Position.ToCore());
        if (mb.ButtonIndex == MouseButton.Left)
            _host.Submit(new Command { Type = CommandType.PlaceEntity, ProtoId = _chestProtoId, X = x, Y = y });
        else
            _host.Submit(new Command { Type = CommandType.RemoveEntity, X = x, Y = y });

        GetViewport().SetInputAsHandled();
    }
}
