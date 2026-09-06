namespace Faketorio.Presentation.Core;

/// world 坐标(tile int / 亚格 long)↔ 屏幕像素。参数由相机每帧喂入。
public sealed class WorldTransform
{
    public const int SubTilesPerTile = 256;   // 与 BeltLine.TileSubTiles 一致

    public double PixelsPerTile { get; set; } = 32;
    public Vec2 CameraCenterTile { get; set; }   // 相机中心的 world tile 坐标(可含小数)
    public Vec2 ViewportSizePx { get; set; }

    public Vec2 WorldSubToScreen(long subX, long subY)
    {
        double tileX = subX / (double)SubTilesPerTile;
        double tileY = subY / (double)SubTilesPerTile;
        return new Vec2(
            (tileX - CameraCenterTile.X) * PixelsPerTile + ViewportSizePx.X / 2,
            (tileY - CameraCenterTile.Y) * PixelsPerTile + ViewportSizePx.Y / 2);
    }

    public Vec2 TileToScreen(int tileX, int tileY)
        => WorldSubToScreen((long)tileX * SubTilesPerTile, (long)tileY * SubTilesPerTile);

    public (int TileX, int TileY) ScreenToTile(Vec2 screenPx)
    {
        double tileX = (screenPx.X - ViewportSizePx.X / 2) / PixelsPerTile + CameraCenterTile.X;
        double tileY = (screenPx.Y - ViewportSizePx.Y / 2) / PixelsPerTile + CameraCenterTile.Y;
        return ((int)Math.Floor(tileX), (int)Math.Floor(tileY));
    }

    /// 可见 tile 矩形 + marginTiles 圈边距(骑边缘的大 footprint 实体 / 传送带物品 / 相机 tween 稳定)。
    public RectI VisibleTileRect(int marginTiles = 3)
    {
        var tl = ScreenToTile(new Vec2(0, 0));
        var br = ScreenToTile(ViewportSizePx);
        return new RectI(
            tl.TileX - marginTiles, tl.TileY - marginTiles,
            br.TileX + marginTiles + 1, br.TileY + marginTiles + 1);
    }
}
