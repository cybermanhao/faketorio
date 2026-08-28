namespace Faketorio.Sim.Prototypes;

public sealed class TransportBeltPrototype : EntityPrototype
{
    // 无阻挡时每 tick 前进的亚格数(spec 5.6: 1 tile = 256 亚格)。
    // 直接以这个内部整数单位在数据里authoring,不做"物品/秒"之类的换算
    // ——本计划尚未把这个字段接到 BeltLane.Advance,换算辅助函数留给接
    // 入计划按需添加(YAGNI)。
    public int SpeedSubTilesPerTick { get; init; }
}
