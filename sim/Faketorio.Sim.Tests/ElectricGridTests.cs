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
}
