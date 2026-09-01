using Faketorio.Sim.Belts;
using Faketorio.Sim.State;

namespace Faketorio.Sim.Tests;

public class BeltNetworkTests
{
    private const byte N = 0, E = 1, S = 2, W = 3;

    private static ulong Hash(BeltNetwork net)
    {
        var w = new Fnv1aHashWriter();
        net.WriteState(w);
        return w.Hash;
    }

    private static List<BeltLine> LiveLines(BeltNetwork net)
    {
        var result = new List<BeltLine>();
        for (int i = 0; i < net.Capacity; i++)
            if (net.IsAliveAtIndex(i)) result.Add(net.GetAtIndex(i));
        return result;
    }

    [Fact]
    public void AddBelt_SingleTile_CreatesOneLineCoveringThatTile()
    {
        var net = new BeltNetwork();
        var id = net.AddBelt(5, 3, E);

        var lines = LiveLines(net);
        Assert.Single(lines);
        Assert.Equal(E, lines[0].Direction);
        Assert.Equal(new[] { (5, 3) }, lines[0].Tiles);
        Assert.Equal(256, lines[0].LengthSubTiles);
        Assert.Equal(id, net.GetLineAt(5, 3));
        Assert.Same(lines[0], net.GetLine(id));
    }

    [Fact]
    public void AddBelt_SingleTile_LanesAreUsableLength256()
    {
        var net = new BeltNetwork();
        var line = net.GetLine(net.AddBelt(0, 0, E));
        Assert.True(line.LaneA.TryInsertAtBack());
        Assert.True(line.LaneB.TryInsertAtBack());
        Assert.Equal(new[] { 192 }, line.LaneA.Gaps); // 256 - 64
    }

    [Fact]
    public void GetLineAt_EmptyTile_ReturnsInvalid()
        => Assert.False(new BeltNetwork().GetLineAt(9, 9).IsValid);

    [Fact]
    public void TwoNonAdjacentBelts_StayTwoLines()
    {
        var net = new BeltNetwork();
        net.AddBelt(0, 0, E);
        net.AddBelt(10, 10, E);
        Assert.Equal(2, LiveLines(net).Count);
    }

    [Fact]
    public void WriteState_SameBuildSequence_SameHash()
    {
        BeltNetwork Build()
        {
            var n = new BeltNetwork();
            n.AddBelt(0, 0, E);
            n.AddBelt(4, 7, N);
            return n;
        }
        Assert.Equal(Hash(Build()), Hash(Build()));
    }

    [Fact]
    public void WriteState_DifferentContent_DifferentHash()
    {
        var a = new BeltNetwork(); a.AddBelt(0, 0, E);
        var b = new BeltNetwork(); b.AddBelt(0, 0, E); b.AddBelt(1, 9, E);
        Assert.NotEqual(Hash(a), Hash(b));
    }
}
