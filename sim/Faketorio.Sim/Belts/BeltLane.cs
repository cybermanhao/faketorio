namespace Faketorio.Sim.Belts;

// 一条传送带 lane 上的物品流,采用 FFF-176 的 gap 表示法(spec 5.2/5.6):
// 不存储每个物品的绝对坐标,只记录物品之间(以及最前物品到出口)的相对
// 距离——整数亚格单位,1 tile = 256。这让正常流动只需增减最前面一个 gap
// (O(1));堵塞时也只需增减"最后一个未压缩到 0 的 gap"。
//
// lineLengthSubTiles 是这条 lane 可用的总长度。M1 Plan 2 只在单个传送带
// 格子(256)上使用这个类型;之后若要把相邻传送带合并成更长的 line,只
// 需传入更大的长度——本类型的算法不需要改动。
public sealed class BeltLane
{
    // spec: 物品间距 0.25 tile = 64 亚格单位(每个物品占用的"槽宽")。
    public const int ItemWidthSubTiles = 64;

    // 从前(出口,position 0)到后(入口)排列。
    // _gaps[0] = 出口到最前物品前沿的距离;
    // _gaps[i](i>0) = 物品 i-1 后沿到物品 i 前沿的距离。
    private readonly List<int> _gaps = new();
    private int _lineLengthSubTiles;

    // 第一个"可能仍未压缩到 0"的下标;之前的下标已确认为 0,Advance 不再
    // 重新扫描它们——这就是摊还 O(1) 的关键。只有 RemoveFront(Task 3)
    // 会把它重置回 0。
    private int _openIndex;

    public BeltLane(int lineLengthSubTiles)
    {
        if (lineLengthSubTiles < ItemWidthSubTiles)
            throw new ArgumentOutOfRangeException(nameof(lineLengthSubTiles));
        _lineLengthSubTiles = lineLengthSubTiles;
    }

    public int Count => _gaps.Count;

    // 仅供测试内省:前到后的 gap 列表,_gaps[0] = 出口到最前物品的距离。
    public IReadOnlyList<int> Gaps => _gaps;

    // 仅供测试内省:最近一次 Advance 调用中,内部循环实际迭代的次数。用
    // 来验证摊还 O(1) 的声明——如果 _openIndex 缓存真的生效,稳定堵塞
    // 状态下这个值应恒为 1(只看一眼已知为 0 的那个下标就退出),不会随
    // _gaps.Count 增长;这样才能把"缓存下标"实现和"每次从头重新扫描"
    // 的朴素实现区分开(两者的最终 Gaps 数值可能完全一样)。
    public int TouchesInLastAdvance { get; private set; }

    // 队尾(入口侧)剩余的空闲亚格数——最后一个物品之后到 line 尾端的空间。
    private int BackFreeSubTiles()
    {
        int used = 0;
        foreach (var g in _gaps) used += g;
        used += _gaps.Count * ItemWidthSubTiles;
        return _lineLengthSubTiles - used;
    }

    // 在队尾(入口)插入一个新物品,新物品贴着 line 的入口边界进入。
    // 空间不足时返回 false,不改变任何状态。
    public bool TryInsertAtBack()
    {
        int free = BackFreeSubTiles();
        if (free < ItemWidthSubTiles) return false;
        _gaps.Add(free - ItemWidthSubTiles);
        return true;
    }

    // 让这条 lane 上的物品流前进最多 speed 个亚格。
    // 只触碰 _gaps[_openIndex..] 中被实际消耗到 0 的那些下标,其余原样
    // 保留——这就是 FFF-176 描述的摊还 O(1) 更新。
    //
    // 重要:任何在本次调用中未能消耗完的 speed 预算(例如到达 _gaps 尾端
    // 时仍有剩余)会被丢弃,不会向下游移交(未来接入的、与后续 lane 的交
    // 接逻辑可能会改变这一点)。这意味着为避免系统性吞吐率衰减,调用者选
    // 择的 speed 值应当整除 ItemWidthSubTiles(64);否则当某个物品的前沿 gap
    // 恰好在本次调用中压缩到 0 且仍有余速,但 _gaps 列表已耗尽时,那个余
    // 速会被遗弃,导致每个 tick 累积一个小缺口。
    public void Advance(int speed)
    {
        TouchesInLastAdvance = 0;
        if (speed < 0) throw new ArgumentOutOfRangeException(nameof(speed));
        if (_gaps.Count == 0) return;
        int remaining = speed;
        int i = _openIndex;
        while (remaining > 0 && i < _gaps.Count)
        {
            TouchesInLastAdvance++;
            int consume = Math.Min(remaining, _gaps[i]);
            _gaps[i] -= consume;
            remaining -= consume;
            if (_gaps[i] > 0) break;
            i++;
        }
        _openIndex = Math.Min(i, _gaps.Count - 1);
    }

    // 队首物品是否已经贴到出口(gap 为 0),可以移交给下游(传送带/机械
    // 臂/建筑输入口——移交逻辑本身在后续接入 Simulation 的计划中实现)。
    public bool IsFrontReady => _gaps.Count > 0 && _gaps[0] == 0;

    // 移除队首物品(调用前必须已确认 IsFrontReady)。它腾出的空间并入新
    // 队首的前方 gap;_openIndex 重置为 0,因为后面的物品可能因此重新有
    // 空间前进。
    public void RemoveFront()
    {
        if (!IsFrontReady)
            throw new InvalidOperationException("RemoveFront called when front is not ready");
        _gaps.RemoveAt(0);
        if (_gaps.Count > 0)
            _gaps[0] += ItemWidthSubTiles;
        _openIndex = 0;
    }

    // 把入口端向外延长 subtiles 个亚格(并入队尾方向的相邻线段)。
    // 只增加可用总长,不触碰任何 gap——BackFreeSubTiles 的公式自动反映新
    // 长度,已有物品到出口的距离不变。
    public void ExtendBack(int subtiles)
    {
        if (subtiles < 0) throw new ArgumentOutOfRangeException(nameof(subtiles));
        _lineLengthSubTiles += subtiles;
    }

    // 把出口端向外延长 subtiles 个亚格(并入出口方向的相邻线段)。
    // 出口整体外移,所以最前物品到新出口的距离要相应增大:gaps[0] += subtiles。
    // 同时把内部游标重置为 0——原本压缩到 0 的最前 gap 现在重新有了空间,
    // 游标若停在它后面会导致最前物品永远不向新出口前进。
    public void ExtendFront(int subtiles)
    {
        if (subtiles < 0) throw new ArgumentOutOfRangeException(nameof(subtiles));
        _lineLengthSubTiles += subtiles;
        if (_gaps.Count > 0) _gaps[0] += subtiles;
        _openIndex = 0;
    }

    // 把相对 gap 列表转成"每个物品前沿距出口的绝对亚格距离"(前到后)。
    // 冷路径(合并/拆分/存档),一次线性扫描,允许分配。
    public IReadOnlyList<int> ToAbsolutePositions()
    {
        var result = new int[_gaps.Count];
        int pos = 0;
        for (int i = 0; i < _gaps.Count; i++)
        {
            pos += _gaps[i];
            result[i] = pos;
            pos += ItemWidthSubTiles;
        }
        return result;
    }
}
