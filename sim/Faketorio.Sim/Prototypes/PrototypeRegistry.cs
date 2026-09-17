namespace Faketorio.Sim.Prototypes;

public sealed class PrototypeRegistry
{
    // 键: (具体 CLR 类型, name)。item 与 entity 允许同名。
    private readonly Dictionary<(Type, string), PrototypeBase> _byTypeAndName = new();
    private readonly List<PrototypeBase> _byId = new();
    private readonly Dictionary<string, EntityPrototype> _entitiesByName = new();

    public int Count => _byId.Count;

    internal void Register(PrototypeBase proto)
    {
        if (!_byTypeAndName.TryAdd((proto.GetType(), proto.Name), proto))
            throw new InvalidDataException($"Duplicate prototype: {proto.GetType().Name} '{proto.Name}'");
    }

    // 全部注册完后调用:按(类型名, name)序分配稳定 Id
    internal void AssignIds()
    {
        var all = new List<PrototypeBase>(_byTypeAndName.Values);
        all.Sort(static (a, b) =>
        {
            int c = string.CompareOrdinal(a.GetType().FullName, b.GetType().FullName);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });
        _byId.Clear();
        for (int i = 0; i < all.Count; i++)
        {
            all[i].Id = i;
            _byId.Add(all[i]);
        }

        foreach (var p in all)
            if (p is EntityPrototype ep) _entitiesByName[ep.Name] = ep;
    }

    public T Get<T>(string name) where T : PrototypeBase
        => (T)_byTypeAndName[(typeof(T), name)];

    public bool TryGet<T>(string name, out T proto) where T : PrototypeBase
    {
        if (_byTypeAndName.TryGetValue((typeof(T), name), out var p) && p is T t)
        {
            proto = t;
            return true;
        }
        proto = null!;
        return false;
    }

    public PrototypeBase GetById(int id) => _byId[id];

    public bool TryGetById(int id, out PrototypeBase proto)
    {
        if (id >= 0 && id < _byId.Count)
        {
            proto = _byId[id];
            return true;
        }
        proto = null!;
        return false;
    }

    // 按名字查任意子类型的 EntityPrototype,不要求调用方知道具体是哪个子类——
    // Get<T>/TryGet<T> 按 (具体 CLR 类型, name) 做键,查不到抽象基类;这个方法
    // 专门补上"我只有个名字,不知道是哪种实体"这个场景(BuildFromInventory 用)。
    public bool TryGetEntityByName(string name, out EntityPrototype proto)
        => _entitiesByName.TryGetValue(name, out proto!);
}
