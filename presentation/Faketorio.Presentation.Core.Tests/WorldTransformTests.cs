using Faketorio.Presentation.Core;

namespace Faketorio.Presentation.Core.Tests;

public class WorldTransformTests
{
    private static WorldTransform Make(double ppt, double cx, double cy, double vw, double vh)
        => new() { PixelsPerTile = ppt, CameraCenterTile = new Vec2(cx, cy), ViewportSizePx = new Vec2(vw, vh) };

    [Theory]
    [InlineData(4)]
    [InlineData(32)]
    [InlineData(64)]
    public void TileToScreen_ScreenToTile_RoundTrips_AtTileCenter(double ppt)
    {
        var t = Make(ppt, 10, -7, 800, 600);
        foreach (var (tx, ty) in new[] { (0, 0), (10, -7), (25, 40), (-13, -2) })
        {
            var s = t.TileToScreen(tx, ty);
            // 采样格中心(+半格像素),避开边界取整歧义
            var back = t.ScreenToTile(new Vec2(s.X + ppt / 2, s.Y + ppt / 2));
            Assert.Equal((tx, ty), back);
        }
    }

    [Fact]
    public void CameraCenter_MapsToViewportCenter()
    {
        var t = Make(32, 3.5, -1.25, 800, 600);
        var s = t.WorldSubToScreen(3L * 256 + 128, -2L * 256 + 192);   // = tile (3.5, -1.25) 亚格
        Assert.InRange(s.X, 399.9, 400.1);
        Assert.InRange(s.Y, 299.9, 300.1);
    }

    [Fact]
    public void ScreenToTile_FloorsAtTileBoundary()
    {
        var t = Make(32, 0, 0, 800, 600);   // 视口中心屏幕 (400,300) = tile (0,0) 左上角
        Assert.Equal((0, 0), t.ScreenToTile(new Vec2(400 + 31.9, 300 + 0)));
        Assert.Equal((1, 0), t.ScreenToTile(new Vec2(400 + 32.1, 300 + 0)));
        Assert.Equal((-1, 0), t.ScreenToTile(new Vec2(400 - 0.1, 300 + 0)));
    }

    [Fact]
    public void VisibleTileRect_CoversViewportPlusMargin()
    {
        var t = Make(32, 0, 0, 800, 600);   // 半视口 = 400x300 px = 12.5 x 9.375 tile
        var r0 = t.VisibleTileRect(0);
        Assert.True(r0.MinX <= -12 && r0.MaxX >= 13);
        Assert.True(r0.MinY <= -9 && r0.MaxY >= 10);
        var r3 = t.VisibleTileRect(3);
        Assert.Equal(r0.MinX - 3, r3.MinX);
        Assert.Equal(r0.MaxX + 3, r3.MaxX);
    }
}
