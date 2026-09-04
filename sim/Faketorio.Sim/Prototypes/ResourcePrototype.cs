namespace Faketorio.Sim.Prototypes;

// 一个噪声特征层的配置。P6 只有矿在用;未来水域 / 树木套同一形状。
// 字段为 0 表示"未设置",由 PrototypeLoader 的解析 pass 补齐(见 spec §4.1)。
public sealed record NoiseLayer(
    int FieldId,
    int LatticeSize,      // S0,必须是 2 的幂
    int Octaves,          // K
    int ThresholdQ16,     // 场值超过它这格才有该特征
    bool Warp  = false,   // 预留,P6 不支持 —— 加载期校验为 true 即抛
    bool Ridge = false);

public readonly record struct StarterPatch(
    string Resource, int CenterX, int CenterY, int Radius, int CenterAmount);

public sealed class ResourcePrototype : PrototypeBase
{
    public required string MinableResult { get; init; }   // 挖出来的 item name
    public NoiseLayer Layer  { get; internal set; } = new(0, 0, 0, 0);  // 解析 pass 后全字段具体
    public int RichnessBase  { get; init; }               // 有矿时的最低矿量(>= 1)
    public int RichnessScale { get; init; }               // 额外矿量 = RichnessScale·excess >> 16
    public int MiningTimeTicks { get; init; } = 60;        // 矿脉每单位挖掘 tick 数
}

public sealed class MapGenPrototype : PrototypeBase       // 单例,约定 name = "default"
{
    public int DefaultLatticeSize { get; init; } = 64;
    public int DefaultOctaves     { get; init; } = 3;
    public IReadOnlyList<StarterPatch> StarterPatches { get; init; } = Array.Empty<StarterPatch>();
}
