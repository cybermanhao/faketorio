using Faketorio.Presentation.Core;

namespace Faketorio.Presentation.Core.Tests;

public class TickAccumulatorTests
{
    [Fact]
    public void SteadyRate_YieldsOneTickPerFrame()
    {
        var acc = new TickAccumulator();
        for (int i = 0; i < 100; i++)
        {
            int n = acc.Advance(TickAccumulator.TickSeconds);
            Assert.Equal(1, n);
        }
        Assert.True(acc.Alpha < 0.01);
    }

    [Fact]
    public void HalfRate_YieldsTickEveryOtherFrame_AlphaTracks()
    {
        var acc = new TickAccumulator();
        Assert.Equal(0, acc.Advance(TickAccumulator.TickSeconds / 2));
        Assert.InRange(acc.Alpha, 0.49, 0.51);
        Assert.Equal(1, acc.Advance(TickAccumulator.TickSeconds / 2));
        Assert.True(acc.Alpha < 0.01);
    }

    [Fact]
    public void HugeDelta_ClampsToMaxCatchUp_AndDiscardsRemainder()
    {
        var acc = new TickAccumulator { MaxCatchUpTicks = 5 };
        int n = acc.Advance(1.0);   // 1 秒 ≈ 60 tick,远超上限
        Assert.Equal(5, n);
        Assert.True(acc.Alpha < 1.0, "追不上的累积时间必须被丢弃,Alpha 保持 [0,1)");
    }

    [Fact]
    public void ZeroDelta_NoTick_NoAlphaChange()
    {
        var acc = new TickAccumulator();
        acc.Advance(TickAccumulator.TickSeconds * 0.3);
        double a0 = acc.Alpha;
        Assert.Equal(0, acc.Advance(0));
        Assert.Equal(a0, acc.Alpha);
    }
}
