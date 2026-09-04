using Faketorio.Sim.World;

namespace Faketorio.Sim.Tests;

public class DeterministicHashTests
{
    [Fact]
    public void SameInputs_SameOutput()
    {
        Assert.Equal(
            DeterministicHash.Hash(123456789L, 7, -3, 42),
            DeterministicHash.Hash(123456789L, 7, -3, 42));
    }

    [Fact]
    public void EachParameterAffectsOutput()
    {
        ulong baseline = DeterministicHash.Hash(1L, 1, 1, 1);
        Assert.NotEqual(baseline, DeterministicHash.Hash(2L, 1, 1, 1));
        Assert.NotEqual(baseline, DeterministicHash.Hash(1L, 2, 1, 1));
        Assert.NotEqual(baseline, DeterministicHash.Hash(1L, 1, 2, 1));
        Assert.NotEqual(baseline, DeterministicHash.Hash(1L, 1, 1, 2));
    }

    [Fact]
    public void PinnedRegressionValue()
    {
        // 钉死值:改了 Mix 常量或混合顺序就会红。首次运行填入实际值(见步骤 3)。
        Assert.Equal(10005860047443317701UL, DeterministicHash.Hash(123456789L, 7, -3, 42));
    }
}
