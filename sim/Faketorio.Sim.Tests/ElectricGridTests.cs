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
}
