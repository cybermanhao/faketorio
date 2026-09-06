namespace Faketorio.Game;

/// 跟随玩家。Player.X/Y 是亚格(1 tile = 256),直接透传。
public sealed class PlayerCameraTarget(SimHost host) : ICameraTarget
{
    public (long SubX, long SubY) WorldSub => (host.Sim.Player.X, host.Sim.Player.Y);
}
