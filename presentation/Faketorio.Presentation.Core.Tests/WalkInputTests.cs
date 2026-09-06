using Faketorio.Presentation.Core;

namespace Faketorio.Presentation.Core.Tests;

public class WalkInputTests
{
    // args: up, down, left, right, expected (-1 encodes null — xUnit InlineData can't hold int?)
    [Theory]
    [InlineData(false, false, false, false, -1)] // nothing
    [InlineData(true,  false, false, false, 0)]  // N
    [InlineData(true,  false, false, true,  1)]  // NE
    [InlineData(false, false, false, true,  2)]  // E
    [InlineData(false, true,  false, true,  3)]  // SE
    [InlineData(false, true,  false, false, 4)]  // S
    [InlineData(false, true,  true,  false, 5)]  // SW
    [InlineData(false, false, true,  false, 6)]  // W
    [InlineData(true,  false, true,  false, 7)]  // NW
    [InlineData(true,  true,  false, false, -1)] // up+down cancel, no horizontal
    [InlineData(false, false, true,  true,  -1)] // left+right cancel, no vertical
    [InlineData(true,  true,  true,  false, 6)]  // vertical cancels -> W
    [InlineData(true,  true,  false, true,  2)]  // vertical cancels -> E
    [InlineData(true,  false, true,  true,  0)]  // horizontal cancels -> N
    [InlineData(false, true,  true,  true,  4)]  // horizontal cancels -> S
    [InlineData(true,  true,  true,  true,  -1)] // everything cancels
    public void Resolve_MapsHeldKeysToEightWayDir(bool up, bool down, bool left, bool right, int expected)
    {
        int? actual = WalkInput.Resolve(up, down, left, right);
        Assert.Equal(expected == -1 ? (int?)null : expected, actual);
    }
}
