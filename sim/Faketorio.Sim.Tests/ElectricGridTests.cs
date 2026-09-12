using Faketorio.Sim.Electric;
using Faketorio.Sim.Entities;

namespace Faketorio.Sim.Tests;

public class ElectricGridTests
{
    [Fact]
    public void TwoPolesWithinWireRange_SameNetwork()
    {
        var grid = new ElectricGrid();
        var a = new EntityId(0, 1); var b = new EntityId(1, 1);
        grid.RegisterPole(a, 0, 0, maximumWireDistanceTiles: 7, supplyAreaDistanceTiles: 2);
        grid.RegisterPole(b, 5, 0, maximumWireDistanceTiles: 7, supplyAreaDistanceTiles: 2);

        var na = grid.FindNetworkAt(0, 0);
        var nb = grid.FindNetworkAt(5, 0);
        Assert.True(na.IsValid);
        Assert.Equal(na, nb);
    }

    [Fact]
    public void TwoPolesOutOfWireRange_DifferentNetworks()
    {
        var grid = new ElectricGrid();
        grid.RegisterPole(new EntityId(0, 1), 0, 0, 5, 2);
        grid.RegisterPole(new EntityId(1, 1), 20, 0, 5, 2);

        var na = grid.FindNetworkAt(0, 0);
        var nb = grid.FindNetworkAt(20, 0);
        Assert.True(na.IsValid);
        Assert.True(nb.IsValid);
        Assert.NotEqual(na, nb);
    }

    [Fact]
    public void ChainOfThreePoles_OneNetwork()
    {
        var grid = new ElectricGrid();
        grid.RegisterPole(new EntityId(0, 1), 0, 0, 7, 2);
        grid.RegisterPole(new EntityId(1, 1), 6, 0, 7, 2);    // 0<->6: dist 6 <= 7
        grid.RegisterPole(new EntityId(2, 1), 12, 0, 7, 2);   // 6<->12: dist 6 <= 7; 0<->12: dist 12 > 7 (not direct)

        Assert.Equal(grid.FindNetworkAt(0, 0), grid.FindNetworkAt(12, 0));
    }

    [Fact]
    public void AsymmetricWireDistance_UsesStricterSide()
    {
        var grid = new ElectricGrid();
        grid.RegisterPole(new EntityId(0, 1), 0, 0, maximumWireDistanceTiles: 10, supplyAreaDistanceTiles: 2);
        grid.RegisterPole(new EntityId(1, 1), 7, 0, maximumWireDistanceTiles: 5, supplyAreaDistanceTiles: 2);

        Assert.NotEqual(grid.FindNetworkAt(0, 0), grid.FindNetworkAt(7, 0));
    }

    [Fact]
    public void SupplyArea_ChebyshevSquare_NotEuclideanCircle()
    {
        var grid = new ElectricGrid();
        grid.RegisterPole(new EntityId(0, 1), 0, 0, 7, supplyAreaDistanceTiles: 2);

        Assert.True(grid.FindNetworkAt(2, 2).IsValid);    // Chebyshev max(2,2)=2 <= 2
        Assert.False(grid.FindNetworkAt(3, 0).IsValid);   // Chebyshev 3 > 2
    }

    [Fact]
    public void PositionOutsideAnyPole_ReturnsInvalid()
        => Assert.False(new ElectricGrid().FindNetworkAt(0, 0).IsValid);

    [Fact]
    public void OverlappingSupplyAreas_LowerPoleIndexNetworkWins()
    {
        var grid = new ElectricGrid();
        var polyHighIndex = new EntityId(5, 1);
        var polyLowIndex = new EntityId(2, 1);
        grid.RegisterPole(polyHighIndex, 0, 0, maximumWireDistanceTiles: 1, supplyAreaDistanceTiles: 3);
        grid.RegisterPole(polyLowIndex, 2, 0, maximumWireDistanceTiles: 1, supplyAreaDistanceTiles: 3);
        // distance between poles = 2 > wireDistance 1 -> NOT connected, two separate networks

        var nHighOnly = grid.FindNetworkAt(-3, 0);   // only covered by polyHighIndex
        var nLowOnly  = grid.FindNetworkAt(5, 0);    // only covered by polyLowIndex
        var nOverlap  = grid.FindNetworkAt(1, 0);    // covered by both

        Assert.NotEqual(nHighOnly, nLowOnly);
        Assert.Equal(nLowOnly, nOverlap);            // lower EntityId.Index network wins the tie
    }

    [Fact]
    public void SupplyAreaStraddlesBucketBoundary_StillFound()
    {
        // 空间索引按 8 格分桶。杆在 (10,0),供电半径 5 -> 覆盖 x=5..15,跨过桶边界 x=8。
        // 查询点 (6,0) 落在杆自己所在桶(bx=1)之外的另一个桶(bx=0)里,
        // 必须依然命中——验证一根杆的覆盖方框跨桶时,插入逻辑覆盖了它接触到的每个桶。
        var grid = new ElectricGrid();
        grid.RegisterPole(new EntityId(0, 1), 10, 0, maximumWireDistanceTiles: 7, supplyAreaDistanceTiles: 5);

        Assert.True(grid.FindNetworkAt(6, 0).IsValid);
        Assert.False(grid.FindNetworkAt(4, 0).IsValid);   // Chebyshev 6 > 5,出范围
    }

    [Fact]
    public void QueryPointInEmptyBucket_ReturnsInvalid()
    {
        // 查询点所在的桶里完全没有任何杆插入过 -> 索引里没有这个桶的条目,
        // 必须走"桶不存在"分支返回 Invalid,而不是抛异常或误命中。
        var grid = new ElectricGrid();
        grid.RegisterPole(new EntityId(0, 1), 0, 0, maximumWireDistanceTiles: 7, supplyAreaDistanceTiles: 2);

        Assert.False(grid.FindNetworkAt(1000, 1000).IsValid);
    }

    [Fact]
    public void UnregisterPole_RemovesItsCoverage()
    {
        var grid = new ElectricGrid();
        var a = new EntityId(0, 1); var b = new EntityId(1, 1);
        grid.RegisterPole(a, 0, 0, 7, 2);
        grid.RegisterPole(b, 6, 0, 7, 2);
        Assert.Equal(grid.FindNetworkAt(0, 0), grid.FindNetworkAt(6, 0));

        grid.UnregisterPole(a);
        Assert.False(grid.FindNetworkAt(0, 0).IsValid);
        Assert.True(grid.FindNetworkAt(6, 0).IsValid);
    }

    private static ElectricGrid GridWithOnePole()
    {
        var grid = new ElectricGrid();
        grid.RegisterPole(new EntityId(0, 1), 0, 0, maximumWireDistanceTiles: 7, supplyAreaDistanceTiles: 5);
        return grid;
    }

    [Fact]
    public void SupplyMeetsExactDemand_FullSatisfaction_NoOverproduction()
    {
        var grid = GridWithOnePole();
        var producer = new EntityId(1, 1); var consumer = new EntityId(2, 1);

        grid.RegisterSupply(producer, 0, 0, UsagePriority.PrimaryOutput, maxJThisTick: 1000);
        grid.RegisterDemand(consumer, 0, 0, UsagePriority.PrimaryInput, amountJ: 1000);
        grid.Settle();

        Assert.Equal(Q16.One, grid.GetSatisfaction(consumer));
        Assert.Equal(1000, grid.GetAllocatedSupply(producer));
    }

    [Fact]
    public void SupplyExceedsDemand_ProducerThrottlesDown()
    {
        var grid = GridWithOnePole();
        var producer = new EntityId(1, 1); var consumer = new EntityId(2, 1);

        grid.RegisterSupply(producer, 0, 0, UsagePriority.PrimaryOutput, maxJThisTick: 1000);
        grid.RegisterDemand(consumer, 0, 0, UsagePriority.PrimaryInput, amountJ: 400);
        grid.Settle();

        Assert.Equal(Q16.One, grid.GetSatisfaction(consumer));
        Assert.Equal(400, grid.GetAllocatedSupply(producer));   // 不多烧
    }

    [Fact]
    public void DemandExceedsSupply_ProducerFullOutput_ConsumerPartialSatisfaction()
    {
        var grid = GridWithOnePole();
        var producer = new EntityId(1, 1); var consumer = new EntityId(2, 1);

        grid.RegisterSupply(producer, 0, 0, UsagePriority.PrimaryOutput, maxJThisTick: 500);
        grid.RegisterDemand(consumer, 0, 0, UsagePriority.PrimaryInput, amountJ: 1000);
        grid.Settle();

        Assert.Equal(500, grid.GetAllocatedSupply(producer));
        Assert.Equal(Q16.FromRatio(500, 1000), grid.GetSatisfaction(consumer));
    }

    [Fact]
    public void TwoProducersSameTier_ShareProportionally()
    {
        var grid = GridWithOnePole();
        var p1 = new EntityId(1, 1); var p2 = new EntityId(2, 1); var consumer = new EntityId(3, 1);

        grid.RegisterSupply(p1, 0, 0, UsagePriority.PrimaryOutput, maxJThisTick: 1000);
        grid.RegisterSupply(p2, 0, 0, UsagePriority.PrimaryOutput, maxJThisTick: 1000);
        grid.RegisterDemand(consumer, 0, 0, UsagePriority.PrimaryInput, amountJ: 1000);
        grid.Settle();

        Assert.Equal(500, grid.GetAllocatedSupply(p1));   // 各自一半,不是一个满一个空
        Assert.Equal(500, grid.GetAllocatedSupply(p2));
    }

    [Fact]
    public void ShortfallAbsorbedByLowestPriorityDemandFirst()
    {
        var grid = GridWithOnePole();
        var producer = new EntityId(1, 1);
        var primaryConsumer = new EntityId(2, 1);
        var tertiaryConsumer = new EntityId(3, 1);

        grid.RegisterSupply(producer, 0, 0, UsagePriority.PrimaryOutput, maxJThisTick: 800);
        grid.RegisterDemand(primaryConsumer, 0, 0, UsagePriority.PrimaryInput, amountJ: 800);
        grid.RegisterDemand(tertiaryConsumer, 0, 0, UsagePriority.Tertiary, amountJ: 400);
        grid.Settle();

        Assert.Equal(Q16.One, grid.GetSatisfaction(primaryConsumer));
        Assert.Equal(Q16.Zero, grid.GetSatisfaction(tertiaryConsumer));
    }

    [Fact]
    public void UncoveredProducerAndConsumer_ZeroAllocation()
    {
        var grid = new ElectricGrid();   // no poles at all
        var producer = new EntityId(0, 1); var consumer = new EntityId(1, 1);
        grid.RegisterSupply(producer, 0, 0, UsagePriority.PrimaryOutput, 1000);
        grid.RegisterDemand(consumer, 0, 0, UsagePriority.PrimaryInput, 1000);
        grid.Settle();

        Assert.Equal(0, grid.GetAllocatedSupply(producer));
        Assert.Equal(Q16.Zero, grid.GetSatisfaction(consumer));
    }

    [Fact]
    public void Settle_ClearsRegistrationsForNextTick()
    {
        var grid = GridWithOnePole();
        var producer = new EntityId(1, 1); var consumer = new EntityId(2, 1);
        grid.RegisterSupply(producer, 0, 0, UsagePriority.PrimaryOutput, 1000);
        grid.RegisterDemand(consumer, 0, 0, UsagePriority.PrimaryInput, 1000);
        grid.Settle();
        Assert.Equal(Q16.One, grid.GetSatisfaction(consumer));

        grid.Settle();   // 没有新登记就结算 -> 上一轮的结果不应该继续生效
        Assert.Equal(Q16.Zero, grid.GetSatisfaction(consumer));
    }

    [Fact]
    public void ThreeProducersUnevenShare_AllocationsSumExactlyToUsed()
    {
        // 回归测试:3 个 Amount=1 的生产者同一档,tierCapacity=3,demand 只要 2 (used=2)。
        // 若每个生产者独立算 e.Amount * used / tierCapacity = 1*2/3 = 0(向下取整),
        // Σ allocated 会变成 0 而不是 2——凭空"丢电",与 shortfall/satisfaction 记账的
        // used=2 对不上。正确实现必须保证 Σ GetAllocatedSupply == used。
        var grid = GridWithOnePole();
        var p1 = new EntityId(1, 1); var p2 = new EntityId(2, 1); var p3 = new EntityId(3, 1);
        var consumer = new EntityId(4, 1);

        grid.RegisterSupply(p1, 0, 0, UsagePriority.PrimaryOutput, maxJThisTick: 1);
        grid.RegisterSupply(p2, 0, 0, UsagePriority.PrimaryOutput, maxJThisTick: 1);
        grid.RegisterSupply(p3, 0, 0, UsagePriority.PrimaryOutput, maxJThisTick: 1);
        grid.RegisterDemand(consumer, 0, 0, UsagePriority.PrimaryInput, amountJ: 2);
        grid.Settle();

        long sum = grid.GetAllocatedSupply(p1) + grid.GetAllocatedSupply(p2) + grid.GetAllocatedSupply(p3);
        Assert.Equal(2, sum);
        // 每个生产者不能超过自己声明的产能上限
        Assert.InRange(grid.GetAllocatedSupply(p1), 0, 1);
        Assert.InRange(grid.GetAllocatedSupply(p2), 0, 1);
        Assert.InRange(grid.GetAllocatedSupply(p3), 0, 1);
    }
}
