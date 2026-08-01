namespace Faketorio.Sim.State;

// 规范序列化的唯一出口:哈希与存档共用(spec 铁律 4)。
// 新增模拟状态必须写进对应的 WriteState,否则确定性测试覆盖不到它。
public interface IStateWriter
{
    void Write(byte value);
    void Write(int value);
    void Write(long value);
}
