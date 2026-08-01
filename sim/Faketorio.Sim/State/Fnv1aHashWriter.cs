namespace Faketorio.Sim.State;

public sealed class Fnv1aHashWriter : IStateWriter
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    private ulong _hash = OffsetBasis;

    public ulong Hash => _hash;

    public void Write(byte value)
    {
        _hash ^= value;
        _hash *= Prime;
    }

    public void Write(int value)
    {
        Write((byte)value);
        Write((byte)(value >> 8));
        Write((byte)(value >> 16));
        Write((byte)(value >> 24));
    }

    public void Write(long value)
    {
        Write((int)value);
        Write((int)(value >> 32));
    }
}
