namespace Faketorio.Presentation.Core;

/// 把变帧率的 delta 秒累积,吐出这一帧要跑几个固定步长的 sim tick。
public sealed class TickAccumulator
{
    public const double TickSeconds = 1.0 / 60.0;
    public int MaxCatchUpTicks { get; init; } = 5;

    private double _acc;

    /// 返回 [0, MaxCatchUpTicks] 个要执行的 tick;超出的累积时间被丢弃(不追实时)。
    public int Advance(double deltaSeconds)
    {
        _acc += deltaSeconds;
        int n = 0;
        while (_acc >= TickSeconds && n < MaxCatchUpTicks)
        {
            _acc -= TickSeconds;
            n++;
        }
        if (_acc >= TickSeconds) _acc %= TickSeconds;   // 夹紧:丢弃追不上的部分,Alpha 保持 [0,1)
        return n;
    }

    /// 距下一个 tick 的分数进度 [0, 1),给未来 lerp 用;v1 不消费。
    public double Alpha => _acc / TickSeconds;
}
