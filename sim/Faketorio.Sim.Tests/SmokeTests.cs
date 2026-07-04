namespace Faketorio.Sim.Tests;

public class SmokeTests
{
    [Fact]
    public void TestProjectReferencesSim()
    {
        Assert.Equal("Faketorio.Sim", typeof(Faketorio.Sim.AssemblyMarker).Assembly.GetName().Name);
    }
}
