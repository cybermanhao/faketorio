using Faketorio.Sim;
using Faketorio.Sim.Prototypes;
using Faketorio.Sim.State;
using Faketorio.Sim.World;

namespace Faketorio.Sim.Tests;

public class ResourceGridTests
{
    private static PrototypeRegistry Reg() => PrototypeLoader.LoadFromDirectory("data/base");

    private static ulong Hash(ResourceGrid g)
    {
        var w = new Fnv1aHashWriter();
        g.WriteState(w);
        return w.Hash;
    }

    [Fact]
    public void GetResourceAt_IsRepeatable()
    {
        var g = new ResourceGrid(12345, Reg());
        var a = g.GetResourceAt(200, -140);
        var b = g.GetResourceAt(200, -140);
        Assert.Equal(a, b);
    }

    [Fact]
    public void GetResourceAt_GeneratesContainingChunk()
    {
        var g = new ResourceGrid(1, Reg());
        Assert.Equal(0, g.GeneratedChunkCount);
        g.GetResourceAt(70, 70);
        Assert.True(g.IsChunkGenerated(70, 70));
        Assert.Equal(1, g.GeneratedChunkCount);
        g.GetResourceAt(71, 71);                 // same chunk
        Assert.Equal(1, g.GeneratedChunkCount);
    }

    [Fact]
    public void StarterPatch_CoalAtOrigin_RegardlessOfSeed()
    {
        var reg = Reg();
        int coalId = reg.Get<ResourcePrototype>("coal").Id;
        foreach (long seed in new long[] { 0, 1, 999, -50 })
        {
            var cell = new ResourceGrid(seed, reg).GetResourceAt(6, -8);   // starter patch centre
            Assert.Equal(coalId, cell.ResourceProtoId);
            Assert.Equal(1500, cell.Amount);      // CenterAmount * (radius - 0) / radius
        }
    }

    [Fact]
    public void StarterPatch_CentreRicherThanEdge()
    {
        var g = new ResourceGrid(0, Reg());
        int centre = g.GetResourceAt(6, -8).Amount;       // dist 0 -> 1500
        int near   = g.GetResourceAt(6, -8 + 3).Amount;   // dist 3, radius 4 -> 1500*1/4 = 375
        Assert.Equal(1500, centre);
        Assert.Equal(375, near);
        Assert.True(centre > near);
    }

    [Fact]
    public void Extract_PartialThenFull_ClearsCell()
    {
        var g = new ResourceGrid(0, Reg());
        Assert.Equal(1500, g.GetResourceAt(6, -8).Amount);

        Assert.Equal(100, g.Extract(6, -8, 100));
        Assert.Equal(1400, g.GetResourceAt(6, -8).Amount);

        Assert.Equal(1400, g.Extract(6, -8, 999999));      // over-extract returns what's left
        Assert.Equal(ResourceCell.Empty, g.GetResourceAt(6, -8));
        Assert.True(g.GetResourceAt(6, -8).IsEmpty);

        Assert.Equal(0, g.Extract(6, -8, 10));             // depleted
    }

    [Fact]
    public void Extract_NonPositiveCount_ReturnsZero_NoChange()
    {
        var g = new ResourceGrid(0, Reg());
        var h = Hash(g);                       // nothing generated
        Assert.Equal(0, g.Extract(6, -8, 0));
        Assert.Equal(0, g.Extract(6, -8, -5));
        Assert.Equal(h, Hash(g));              // guard is first — no chunk generated, no state change
        Assert.Equal(0, g.GeneratedChunkCount);
    }

    [Fact]
    public void Extract_EmptyTile_ReturnsZero()
    {
        var g = new ResourceGrid(0, Reg());
        int ex = 0, ey = 5000;
        for (int x = 0; x < 4096; x++)                     // find a tile with no resource
            if (g.GetResourceAt(x, ey).IsEmpty) { ex = x; break; }
        Assert.True(g.GetResourceAt(ex, ey).IsEmpty);
        Assert.Equal(0, g.Extract(ex, ey, 50));
    }

    [Fact]
    public void AccessOrderIndependent_SameWriteStateHash()
    {
        var reg = Reg();
        var tiles = new List<(int, int)>();
        for (int cx = -1; cx <= 1; cx++)
            for (int cy = -1; cy <= 1; cy++)
                tiles.Add((cx * 40 + 3, cy * 40 + 7));      // spread across 9 chunks

        var g1 = new ResourceGrid(555, reg);
        foreach (var (x, y) in tiles) g1.GetResourceAt(x, y);

        var g2 = new ResourceGrid(555, reg);
        for (int i = tiles.Count - 1; i >= 0; i--) g2.GetResourceAt(tiles[i].Item1, tiles[i].Item2);

        Assert.Equal(Hash(g1), Hash(g2));
    }

    [Fact]
    public void DifferentSeed_DifferentWriteStateHash()
    {
        var reg = Reg();
        ulong H(long seed)
        {
            var g = new ResourceGrid(seed, reg);
            for (int x = 200; x < 328; x += 4)             // 128-wide pure-noise area, no starter patches
                for (int y = 200; y < 328; y += 4)
                    g.GetResourceAt(x, y);
            return Hash(g);
        }
        Assert.NotEqual(H(1), H(2));
    }

    [Fact]
    public void WriteState_ChangesAfterExtract()
    {
        var g = new ResourceGrid(0, Reg());
        g.GetResourceAt(6, -8);
        var before = Hash(g);
        g.Extract(6, -8, 50);
        Assert.NotEqual(before, Hash(g));
    }

    [Fact]
    public void PeekResourceAt_MatchesGetResourceAt_AndHasNoSideEffect()
    {
        var protos = PrototypeLoader.LoadFromDirectory("data/base");
        var grid = new ResourceGrid(123456789L, protos);

        // 选一批"远处"坐标(跨多个未生成 chunk)
        var probes = new (int x, int y)[] { (500, 500), (501, 500), (-800, 320), (77, -1234), (2048, 2048) };

        int chunksBefore = grid.GeneratedChunkCount;
        var peeked = probes.Select(p => grid.PeekResourceAt(p.x, p.y)).ToArray();

        // 无副作用:Peek 不生成 chunk
        Assert.Equal(chunksBefore, grid.GeneratedChunkCount);

        // 内容一致:Peek 的结果 == 之后 GetResourceAt 的结果
        for (int i = 0; i < probes.Length; i++)
            Assert.Equal(grid.GetResourceAt(probes[i].x, probes[i].y), peeked[i]);

        // 已生成 chunk 上:Peek == Get
        Assert.Equal(grid.GetResourceAt(500, 500), grid.PeekResourceAt(500, 500));
    }

    [Fact]
    public void PeekResourceAt_DoesNotChangeStateHash()
    {
        var sim = new Simulation(PrototypeLoader.LoadFromDirectory("data/base"), 123456789L);
        sim.Step();
        ulong before = sim.ComputeStateHash();

        for (int gx = -3; gx <= 3; gx++)
            for (int gy = -3; gy <= 3; gy++)
                sim.Resources.PeekResourceAt(gx * 40 + 1000, gy * 40 + 1000);   // 一堆远处虚拟坐标

        Assert.Equal(before, sim.ComputeStateHash());
    }

    [Fact]
    public void OverlappingStarterPatches_ListOrderWins()
    {
        // 两块重叠的启动矿斑,同一格上列表靠前的胜
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "gen.json"),
            "[{ \"type\": \"item\", \"name\": \"coal\", \"stackSize\": 50 }," +
            " { \"type\": \"item\", \"name\": \"stone\", \"stackSize\": 50 }," +
            " { \"type\": \"resource\", \"name\": \"coal\", \"minableResult\": \"coal\", \"noise\": { \"thresholdQ16\": 44000 }, \"richnessBase\": 400, \"richnessScale\": 6000 }," +
            " { \"type\": \"resource\", \"name\": \"stone\", \"minableResult\": \"stone\", \"noise\": { \"thresholdQ16\": 46000 }, \"richnessBase\": 300, \"richnessScale\": 4000 }," +
            " { \"type\": \"map-gen\", \"name\": \"default\", \"starterPatches\": [" +
            "   { \"resource\": \"coal\",  \"centerX\": 0, \"centerY\": 0, \"radius\": 5, \"centerAmount\": 900 }," +
            "   { \"resource\": \"stone\", \"centerX\": 2, \"centerY\": 0, \"radius\": 5, \"centerAmount\": 900 } ] }]");
        var reg = PrototypeLoader.LoadFromDirectory(dir);
        var g = new ResourceGrid(0, reg);
        // (1,0) is inside both patches; coal is listed first
        Assert.Equal(reg.Get<ResourcePrototype>("coal").Id, g.GetResourceAt(1, 0).ResourceProtoId);
    }
}
