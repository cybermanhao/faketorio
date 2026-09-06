using System.Collections.Generic;
using Godot;
using Faketorio.Presentation.Core;
using Faketorio.Sim.Belts;
using Faketorio.Sim.Prototypes;
using Faketorio.Sim.World;

namespace Faketorio.Game;

/// 立即模式**只读**渲染。每帧 QueueRedraw + _Draw,从 sim 现读现画,零 mutation。
/// 矿脉走 ResourceGrid.PeekResourceAt(不生成 chunk、不进状态哈希);
/// 实体走 EntityPool 的索引序遍历 + WorldGrid(本身无副作用)。
public partial class WorldView : Node2D
{
    [Export] public bool ShowGrid = true;

    /// 低于这个缩放不画矿(一屏几十万格,且 Peek 未生成 chunk 时要临时 Generate,太贵)。
    private const double MinPptForOre = 8;

    /// 每帧最多填充几个 32x32 的矿缓存块——PeekResourceAt 对未生成的 chunk 会
    /// 临时 Generate 一整块再丢弃,一次填满整屏会卡顿,所以摊到多帧。
    /// 关键:填充只在 _Process 里跑,_Draw 绝不同步生成 chunk。
    private const int MaxOreChunkFillsPerFrame = 4;

    /// 待填充的 chunk 队列上限。平移比填充快时就停止入队(矿慢一点点淡入,不卡)。
    private const int MaxPendingOreChunks = 256;

    private SimHost _host = null!;
    private CameraController _cam = null!;
    private BuildController _build = null!;

    private readonly Dictionary<long, ResourceCell[]> _oreCache = new();
    // _Draw 命中未缓存 chunk 时把 key 丢这里,_Process 每帧摊几个填进 _oreCache。
    private readonly HashSet<long> _pendingOreChunks = new();
    private readonly List<long> _oreFillScratch = new();

    public override void _Ready()
    {
        _host = GetNode<SimHost>("/root/SimHost");
        _cam = GetNode<CameraController>("../CameraController");
        _build = GetNode<BuildController>("../BuildController");
    }

    public override void _Process(double delta)
    {
        FillPendingOreChunks();
        QueueRedraw();
    }

    // 每帧摊几个待填 chunk 进缓存。这是唯一会调用 PeekResourceAt(可能触发 Generate)的地方。
    private void FillPendingOreChunks()
    {
        if (_pendingOreChunks.Count == 0) return;

        _oreFillScratch.Clear();
        foreach (var ck in _pendingOreChunks)
        {
            _oreFillScratch.Add(ck);
            if (_oreFillScratch.Count >= MaxOreChunkFillsPerFrame) break;
        }

        var res = _host.Sim.Resources;
        foreach (var ck in _oreFillScratch)
        {
            _pendingOreChunks.Remove(ck);
            if (_oreCache.ContainsKey(ck)) continue;

            int cx = (int)(ck >> 32);
            int cy = (int)(ck & 0xFFFFFFFFL);
            int bx = cx << 5, by = cy << 5;

            var arr = new ResourceCell[32 * 32];
            for (int ly = 0; ly < 32; ly++)
                for (int lx = 0; lx < 32; lx++)
                    arr[ly * 32 + lx] = res.PeekResourceAt(bx + lx, by + ly);
            _oreCache[ck] = arr;
        }
    }

    public override void _Draw()
    {
        var t = _cam.WorldXform;
        // 第一帧 CameraController._Process 可能还没跑过,视口尺寸是 (0,0) —— 这帧啥也别画。
        if (t.ViewportSizePx.X < 1 || t.ViewportSizePx.Y < 1) return;

        var vis = t.VisibleTileRect(marginTiles: 3);
        var sim = _host.Sim;
        float ppt = (float)t.PixelsPerTile;

        // 1. 地块底色:整个视口一次画完(逐格 DrawRect 在低缩放下是十万级 draw call)
        DrawRect(
            new Rect2(Vector2.Zero, new Vector2((float)t.ViewportSizePx.X, (float)t.ViewportSizePx.Y)),
            new Color(0.11f, 0.12f, 0.11f));

        // 2. 网格 gizmo
        if (ShowGrid && t.PixelsPerTile >= 6)
        {
            var faint = new Color(1, 1, 1, 0.06f);
            var mid = new Color(1, 1, 1, 0.12f);
            for (int x = vis.MinX; x <= vis.MaxX; x++)
            {
                var a = t.TileToScreen(x, vis.MinY).ToGodot();
                var b = t.TileToScreen(x, vis.MaxY).ToGodot();
                DrawLine(a, b, (x & 7) == 0 ? mid : faint, 1f);
            }
            for (int y = vis.MinY; y <= vis.MaxY; y++)
            {
                var a = t.TileToScreen(vis.MinX, y).ToGodot();
                var b = t.TileToScreen(vis.MaxX, y).ToGodot();
                DrawLine(a, b, (y & 7) == 0 ? mid : faint, 1f);
            }
        }

        // 3. 矿脉(经 PeekResourceAt,无副作用)
        if (t.PixelsPerTile >= MinPptForOre)
        {
            for (int y = vis.MinY; y < vis.MaxY; y++)
                for (int x = vis.MinX; x < vis.MaxX; x++)
                {
                    if (!TryPeekOre(x, y, out var cell) || cell.IsEmpty) continue;
                    var proto = sim.Prototypes.GetById(cell.ResourceProtoId);
                    var s = t.TileToScreen(x, y).ToGodot();
                    DrawRect(new Rect2(s, new Vector2(ppt, ppt)),
                             RenderPalette.ForResource(cell.ResourceProtoId, proto));
                }
        }

        // 4. 实体
        for (int i = 0; i < sim.Entities.Capacity; i++)
        {
            if (!sim.Entities.IsAliveAtIndex(i)) continue;
            ref readonly var d = ref sim.Entities.GetAtIndex(i);
            var proto = sim.Prototypes.GetById(d.ProtoId);
            int w = 1, h = 1;
            if (proto is EntityPrototype ep) { w = ep.TileWidth; h = ep.TileHeight; }
            // footprint 与可见矩形相交才画(骑边缘的大实体不能漏)
            if (d.X + w <= vis.MinX || d.X >= vis.MaxX || d.Y + h <= vis.MinY || d.Y >= vis.MaxY) continue;

            var s = t.TileToScreen(d.X, d.Y).ToGodot();
            var size = new Vector2(ppt * w - 2, ppt * h - 2);
            DrawRect(new Rect2(s + new Vector2(1, 1), size), RenderPalette.ForEntity(proto));
            DrawOrientation(s + new Vector2(1, 1), size, d.Rotation);

            // 传送带:在基色矩形上叠 3 个雪佛龙箭头,沿 Rotation 指的方向 —— 一眼认出是"传送带"。
            if (proto is TransportBeltPrototype)
                DrawBeltChevrons(s + new Vector2(1, 1) + size / 2, ppt, d.Rotation);
        }

        // 5. 传送带上的物品
        for (int i = 0; i < sim.Belts.Capacity; i++)
        {
            if (!sim.Belts.IsAliveAtIndex(i)) continue;
            var line = sim.Belts.GetAtIndex(i);
            DrawLaneItems(t, line, line.LaneA, laneOffsetTiles: -0.22);
            DrawLaneItems(t, line, line.LaneB, laneOffsetTiles: +0.22);
        }

        // 6. 玩家 —— 小圆点标记,半径钳在 20px 以内,再缩再放都不会变成"大饼"。
        {
            var p = t.WorldSubToScreen(sim.Player.X, sim.Player.Y).ToGodot();
            float pr = Mathf.Min(ppt * 0.3f, 20f);
            DrawCircle(p, pr, new Color("#e8e8e8"));
            DrawArc(p, pr, 0f, Mathf.Tau, 24, new Color(0.1f, 0.1f, 0.1f, 0.9f), 1.5f, true);
        }

        // 7. 光标格高亮
        {
            var (hx, hy) = _build.HoverTile;
            var s = t.TileToScreen(hx, hy).ToGodot();
            var r = new Rect2(s, new Vector2(ppt, ppt));
            DrawRect(r, _build.LastCommandRejected ? new Color(1, 0.3f, 0.3f) : new Color(1, 1, 1, 0.8f), false, 2f);
        }

        // 8. 相机模式 HUD —— 左上角文字,M 键切换时肉眼可见。
        {
            string label = _cam.Mode == CameraMode.Follow ? "FOLLOW" : "MAP (free)";
            DrawString(ThemeDB.FallbackFont, new Vector2(8, 20), label,
                       HorizontalAlignment.Left, -1f, 16, new Color(1, 1, 1, 0.9f));
        }
    }

    // 沿 rot(0/1/2/3 = N/E/S/W)方向画 3 个 ">" 雪佛龙。center 是 belt 格中心(屏幕像素)。
    private void DrawBeltChevrons(Vector2 center, float ppt, byte rot)
    {
        var (dxi, dyi) = BeltNetwork.Delta(rot);
        var dir = new Vector2(dxi, dyi);
        var perp = new Vector2(-dyi, dxi);
        var col = new Color(1, 1, 1, 0.6f);
        float wing = ppt * 0.26f;
        float depth = ppt * 0.16f;
        for (int k = -1; k <= 1; k++)
        {
            var mid = center + dir * (k * ppt * 0.3f);
            var tip = mid + dir * depth;
            DrawLine(mid + perp * wing, tip, col, 2f);
            DrawLine(mid - perp * wing, tip, col, 2f);
        }
    }

    // 按 32x32 块缓存 Peek 结果。缓存未命中时:登记该 chunk 待后台填充,这帧这一格不画
    // (返回 false → 透明,深色底透出来)。绝不在 _Draw 里同步 PeekResourceAt 未生成的 chunk。
    private bool TryPeekOre(int x, int y, out ResourceCell cell)
    {
        long ck = ((long)(x >> 5) << 32) | (uint)(y >> 5);
        if (_oreCache.TryGetValue(ck, out var arr))
        {
            cell = arr[(y & 31) * 32 + (x & 31)];
            return true;
        }

        // 平移太快、待填队列已满时就不再入队——矿慢几帧淡入即可,不阻塞。
        if (_pendingOreChunks.Count < MaxPendingOreChunks)
            _pendingOreChunks.Add(ck);
        cell = default;
        return false;
    }

    // BeltLane 的 PositionedItem.LeadingEdgeSubTiles 是"物品前沿离**出口**多少亚格"。
    // 出口在 Tiles[0] 这一格朝 Direction 的那条边上;沿 -Direction 回退即可拿到世界亚格坐标。
    private void DrawLaneItems(WorldTransform t, BeltLine line, BeltLane lane, double laneOffsetTiles)
    {
        var items = lane.ToAbsolutePositions();
        if (items.Count == 0) return;

        var (dx, dy) = BeltNetwork.Delta(line.Direction);
        var (ex, ey) = line.Tiles[0];
        const double Sub = BeltLine.TileSubTiles;   // 256

        // 出口边中点(亚格)
        double exitX = ex * Sub + Sub / 2 + dx * (Sub / 2);
        double exitY = ey * Sub + Sub / 2 + dy * (Sub / 2);
        // 行进方向的右手法线,用来把 A/B 两条 lane 岔开
        double nx = -dy, ny = dx;

        float sz = (float)t.PixelsPerTile * 0.22f;
        var half = new Vector2(sz / 2, sz / 2);

        foreach (var it in items)
        {
            // 物品中心 = 出口 - 方向 * (前沿距离 + 半个物品宽)
            double back = it.LeadingEdgeSubTiles + BeltLane.ItemWidthSubTiles / 2.0;
            double px = exitX - dx * back + nx * laneOffsetTiles * Sub;
            double py = exitY - dy * back + ny * laneOffsetTiles * Sub;
            var s = t.WorldSubToScreen((long)px, (long)py).ToGodot();
            var box = new Rect2(s - half, new Vector2(sz, sz));
            DrawRect(box, RenderPalette.ForItem(it.ItemProtoId));                 // 亮色方块
            DrawRect(box, new Color(0.1f, 0.1f, 0.1f, 0.9f), false, 1f);          // 深色描边,motion 更明显
        }
    }

    private void DrawOrientation(Vector2 topLeft, Vector2 size, byte rot)
    {
        var c = topLeft + size / 2;
        float r = Mathf.Min(size.X, size.Y) * 0.35f;
        Vector2 tip = rot switch
        {
            1 => c + new Vector2(r, 0),
            2 => c + new Vector2(0, r),
            3 => c + new Vector2(-r, 0),
            _ => c + new Vector2(0, -r),
        };
        DrawLine(c, tip, new Color(0, 0, 0, 0.7f), 2f);
    }
}
