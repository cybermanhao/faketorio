using Godot;

namespace Faketorio.Game;

/// 纯几何计算,不依赖 Node/Sim。BuildController(点击命中判定)和 WorldView(绘制)
/// 必须调用这里的同一份函数算矩形——两边各自重算同一套数字会导致"画出来的框"
/// 和"能点中的区域"逐渐不一致,这个类就是唯一真源。
public static class HotbarLayout
{
    public const int SlotsPerGroup = 10;

    // ---- 快捷栏行 ----
    private static readonly Vector2 PageButtonSize = new(26, 44);
    private static readonly Vector2 SlotSize = new(38, 44);
    private const float SlotGap = 3f;
    private const float GroupGap = 10f;   // 页按钮<->槽位、槽位<->快捷操作 之间的间距
    private static readonly Vector2 QuickActionSize = new(22, 22);
    private const float QuickActionGap = 3f;
    private const int QuickActionCols = 3, QuickActionRows = 2;
    private const float HotbarBottomMargin = 14f;   // 槽位行底边到视口底边的距离

    public readonly struct HotbarRects
    {
        public readonly Rect2 PageButton;
        public readonly Rect2[] Slots;          // 长度 = slotsPerGroup
        public readonly Rect2[] QuickActions;   // 长度 = QuickActionCols * QuickActionRows

        public HotbarRects(Rect2 pageButton, Rect2[] slots, Rect2[] quickActions)
        {
            PageButton = pageButton;
            Slots = slots;
            QuickActions = quickActions;
        }
    }

    public static HotbarRects HotbarRow(Vector2 viewportSize, int slotsPerGroup)
    {
        float slotsWidth = SlotSize.X * slotsPerGroup + SlotGap * (slotsPerGroup - 1);
        float quickWidth = QuickActionSize.X * QuickActionCols + QuickActionGap * (QuickActionCols - 1);
        float totalWidth = PageButtonSize.X + GroupGap + slotsWidth + GroupGap + quickWidth;

        float rowHeight = Mathf.Max(PageButtonSize.Y, SlotSize.Y);
        float x0 = (viewportSize.X - totalWidth) / 2f;
        float y0 = viewportSize.Y - HotbarBottomMargin - rowHeight;

        var pageBtn = new Rect2(new Vector2(x0, y0 + (rowHeight - PageButtonSize.Y) / 2f), PageButtonSize);

        var slots = new Rect2[slotsPerGroup];
        float sx = x0 + PageButtonSize.X + GroupGap;
        float sy = y0 + (rowHeight - SlotSize.Y) / 2f;
        for (int i = 0; i < slotsPerGroup; i++)
        {
            slots[i] = new Rect2(new Vector2(sx + i * (SlotSize.X + SlotGap), sy), SlotSize);
        }

        var quick = new Rect2[QuickActionCols * QuickActionRows];
        float qx0 = sx + slotsWidth + GroupGap;
        float qy0 = y0 + (rowHeight - (QuickActionSize.Y * QuickActionRows + QuickActionGap * (QuickActionRows - 1))) / 2f;
        for (int r = 0; r < QuickActionRows; r++)
            for (int c = 0; c < QuickActionCols; c++)
            {
                var pos = new Vector2(qx0 + c * (QuickActionSize.X + QuickActionGap), qy0 + r * (QuickActionSize.Y + QuickActionGap));
                quick[r * QuickActionCols + c] = new Rect2(pos, QuickActionSize);
            }

        return new HotbarRects(pageBtn, slots, quick);
    }

    // ---- 背包面板 ----
    private const float MinPanelWidth = 560f;
    private const float PanelWidthRatio = 0.44f;
    private const float PanelHeightRatio = 0.7f;
    private const float PanelTopMargin = 70f;
    private const float PanelLeftMargin = 24f;
    private const float PanelPadding = 8f;
    private const int ColumnCount = 20;
    private const float CellGap = 2f;
    private const float MinVisibleRows = 2f;   // heightFraction=0 时仍然至少露出这么多行(收缩下限)

    public static Rect2 InventoryPanel(Vector2 viewportSize, float heightFraction)
    {
        float width = Mathf.Max(MinPanelWidth, viewportSize.X * PanelWidthRatio);
        float maxHeight = viewportSize.Y * PanelHeightRatio;
        float minHeight = RowHeight(width) * MinVisibleRows + PanelPadding * 2f + HeaderHeight;
        float height = Mathf.Lerp(minHeight, maxHeight, Mathf.Clamp(heightFraction, 0f, 1f));

        return new Rect2(new Vector2(PanelLeftMargin, PanelTopMargin), new Vector2(width, height));
    }

    private const float HeaderHeight = 20f;   // 标题行 + resize handle 的高度预留

    // 一行格子的高度(含格子本身,不含 gap——CellSize 是正方形,取宽度算)
    private static float RowHeight(float panelWidth)
    {
        float contentWidth = panelWidth - PanelPadding * 2f;
        float cell = (contentWidth - CellGap * (ColumnCount - 1)) / ColumnCount;
        return cell + CellGap;
    }

    public static Rect2 ResizeHandle(Rect2 panelRect)
    {
        var size = new Vector2(16, 6);
        var pos = panelRect.Position + new Vector2((panelRect.Size.X - size.X) / 2f, 4f);
        return new Rect2(pos, size);
    }

    // 返回当前可见的格子矩形(已裁剪到面板高度内),scrollOffsetRows 是滚动了多少整行
    // (含小数——允许半行滚动,视觉更顺滑)。visibleRowCount 是这次**实际**发出了几行
    // (不是估算上限)——调用方用它 + scrollOffsetRows 反推每个数组下标对应的真实
    // slotIndex 时,必须逐行连续、不能有被跳过的中间行,否则下标会错位(这是这份
    // 函数唯一必须维护的契约:数组前 N 行必须依次对应 firstRow, firstRow+1, ...,
    // 不能因为某一行"部分裁剪"就整行跳过——那样会让调用方以为第 0 行对应 firstRow,
    // 实际上却是 firstRow+1,后续所有点击命中判定都会错位一整行)。
    // 顶部一行哪怕因为滚动分数被裁掉一点点(至多 CellGap 那么几像素),也照样整行发出——
    // 允许极轻微地画出面板标题区之下、正文区之上那一丝丝(不做真正的裁剪矩形/scissor,
    // 这点视觉溢出可以接受);底部则相反,一旦某一行整体已经落在可视区域下边界之外,
    // 后面的行(y 单调递增)必然也在外面,直接 break,不再继续。
    public static Rect2[] InventoryGrid(Rect2 panelRect, int slotCount, int columnCount, float scrollOffsetRows, out int visibleRowCount)
    {
        float contentWidth = panelRect.Size.X - PanelPadding * 2f;
        float cell = (contentWidth - CellGap * (columnCount - 1)) / columnCount;
        float rowH = cell + CellGap;

        float gridTop = panelRect.Position.Y + HeaderHeight + PanelPadding;
        float gridBottom = panelRect.Position.Y + panelRect.Size.Y - PanelPadding;

        int totalRows = Mathf.CeilToInt(slotCount / (float)columnCount);
        int firstRow = Mathf.FloorToInt(scrollOffsetRows);
        float subRowOffsetPx = (scrollOffsetRows - firstRow) * rowH;

        var result = new System.Collections.Generic.List<Rect2>();
        int emittedRows = 0;
        for (int r = 0; ; r++)
        {
            int row = firstRow + r;
            if (row >= totalRows) break;
            float y = gridTop + r * rowH - subRowOffsetPx;
            if (y > gridBottom) break;   // 这一行(及之后所有行,y 单调递增)已经整体落在可视区域下方

            emittedRows++;
            for (int c = 0; c < columnCount; c++)
            {
                int slotIndex = row * columnCount + c;
                if (slotIndex >= slotCount) break;
                float x = panelRect.Position.X + PanelPadding + c * (cell + CellGap);
                result.Add(new Rect2(new Vector2(x, y), new Vector2(cell, cell)));
            }
        }
        visibleRowCount = emittedRows;
        return result.ToArray();
    }

    // ---- 手搓/蓝图面板 ----
    private const float MinContextPanelWidth = 280f;
    private const float ContextPanelWidthRatio = 0.32f;
    private const float PanelGap = 10f;

    public static Rect2 ContextPanel(Vector2 viewportSize)
    {
        var inv = InventoryPanel(viewportSize, heightFraction: 1f);   // 高度跟背包面板展开到最大时对齐
        float width = Mathf.Max(MinContextPanelWidth, viewportSize.X * ContextPanelWidthRatio);
        float x = inv.Position.X + inv.Size.X + PanelGap;
        return new Rect2(new Vector2(x, inv.Position.Y), new Vector2(width, inv.Size.Y));
    }

    // ---- 小地图占位 ----
    private static readonly Vector2 MinimapSize = new(120, 120);
    private const float MinimapMargin = 10f;

    public static Rect2 Minimap(Vector2 viewportSize)
    {
        var pos = new Vector2(viewportSize.X - MinimapMargin - MinimapSize.X, MinimapMargin);
        return new Rect2(pos, MinimapSize);
    }
}
