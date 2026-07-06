namespace Faketorio.Sim.Commands;

// 双缓冲命令队列:Submit 写入 pending,tick 开始时整体交换后读取。零稳态分配。
public sealed class CommandQueue
{
    private Command[] _pending = new Command[256];
    private Command[] _applying = new Command[256];
    private int _pendingCount;
    private int _applyingCount;

    public void Enqueue(in Command command)
    {
        if (_pendingCount == _pending.Length)
            Array.Resize(ref _pending, _pending.Length * 2);
        _pending[_pendingCount++] = command;
    }

    // 交换缓冲,返回本 tick 待应用的命令(按提交顺序)
    public ReadOnlySpan<Command> BeginTick()
    {
        (_pending, _applying) = (_applying, _pending);
        _applyingCount = _pendingCount;
        _pendingCount = 0;
        return _applying.AsSpan(0, _applyingCount);
    }
}
