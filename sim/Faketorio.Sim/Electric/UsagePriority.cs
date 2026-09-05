namespace Faketorio.Sim.Electric;

// 完整 6 档;M1 只有 PrimaryOutput(发电机)/PrimaryInput(未来消费者)非空。
// 供给侧按数值升序消耗;需求侧吸收缺口按数值降序(Tertiary 先牺牲)。
public enum UsagePriority : byte
{
    Solar = 0,
    PrimaryOutput = 1,
    SecondaryOutput = 2,
    PrimaryInput = 3,
    SecondaryInput = 4,
    Tertiary = 5,
}
