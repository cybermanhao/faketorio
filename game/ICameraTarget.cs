namespace Faketorio.Game;

/// 相机跟随目标的唯一接口——只暴露"世界亚格坐标"。
/// 现在只有玩家实现;将来车辆/遥控机器人同样只要实现这个。
public interface ICameraTarget
{
    (long SubX, long SubY) WorldSub { get; }
}
