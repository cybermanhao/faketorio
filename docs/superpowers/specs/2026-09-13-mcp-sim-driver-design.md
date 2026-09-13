# MCP 原子操作模拟驱动器设计(子项目一:操作 + 读取状态)

## 0. 背景

现在测试/探索 Faketorio 的 sim 层只有一条路:写新的 xUnit 测试代码、编译、跑。
想临时验证一个场景("熔炉输出堵住会不会被机械臂正确清空"这种)得走完整的
编辑-编译-跑测试循环。这个子项目给 sim 层包一层 MCP server,让 Claude Code
能直接在对话里逐条调用原子操作("放一个熔炉""跑 200 tick""查一下库存")来
驱动、探索、验证场景,不用写文件、不用编译。

**子项目边界**:这一轮只做"操作模拟 + 读取状态",不含截图——截图需要驱动
Godot 进程渲染,技术可行性(Windows 上 headless/离屏渲染截图)还没验证过,
brainstorm 时已经和用户确认拆成独立的第二个子项目,单独立项。

## 1. 架构

新建 `sim/Faketorio.Sim.McpServer` 控制台项目(`net8.0`),引用
`Faketorio.Sim`(项目引用,不是 NuGet),依赖 `ModelContextProtocol`
NuGet 包(官方 C# SDK,截至写这份 spec 时的稳定版本是 1.4.0——**实现时用
`dotnet add package ModelContextProtocol` 装最新稳定版,不要手动钉死这个
版本号,以免装的时候已经有更新的补丁版**)。

Stdio 传输(这是给 Claude Code 自己用的本地工具,不需要 HTTP):

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
await builder.Build().RunAsync();
```

工具类用 `[McpServerToolType]` 标注,每个工具方法用 `[McpServerTool]` +
`[Description(...)]` 标注(供 MCP 协议把描述暴露给客户端)。

在项目根目录(或用户的 Claude Code 配置)注册这个 server,让 Claude Code
在会话里能发现并调用这些工具——具体注册步骤(`.mcp.json` 之类)留给实施阶段
按 Claude Code 当时的官方文档操作,这份 spec 不钉死配置文件格式。

## 2. 状态模型

进程持有一个静态的 `Simulation?` 字段,初始为 `null`。除 `reset_simulation`
外的所有工具,如果这个字段还是 `null`,统一返回一个明确的错误(不是抛未处理
异常)。

```csharp
internal static class SimHost
{
    public static Simulation? Sim;
}
```

`reset_simulation` 创建一个新的 `Simulation` 替换掉旧的——旧场景直接丢弃,
没有"多开"概念(brainstorm 时已确认单实例够用,不做多 session)。

## 3. 工具目录

每个工具的参数/返回值都是 MCP SDK 能自动处理的简单类型(`int`/`long`/
`string`/`bool`/可空类型/简单 record)——SDK 会自动从方法签名生成 JSON Schema,
不需要手写 schema。

### `reset_simulation`
```csharp
[McpServerTool, Description("创建一个新的模拟,替换当前场景(如果有的话)")]
public static ResetResult ResetSimulation(
    [Description("确定性种子,默认 0")] long seed = 0,
    [Description("原型数据目录,默认 data/base")] string dataDir = "data/base")
```
返回 `ResetResult(long Tick, int PrototypeCount)`。内部:
`SimHost.Sim = new Simulation(PrototypeLoader.LoadFromDirectory(dataDir), seed);`。
`dataDir` 是相对路径,相对于 server 进程的工作目录(实现时要确认 Claude Code
启动 MCP server 子进程时的工作目录是仓库根目录——如果不是,工具要能收到绝对
路径,或者 server 自己算出仓库根目录;这个细节在实施计划里要写清楚具体验证
方法,不能想当然)。

### `step`
```csharp
[McpServerTool, Description("推进模拟若干 tick")]
public static StepResult Step([Description("要跑的 tick 数,默认 1")] int ticks = 1)
```
返回 `StepResult(long Tick, int RejectedCommandCount, int RejectedThisCall)`——
`RejectedThisCall` = 调用前后 `RejectedCommandCount` 的差值,方便调用方立刻
知道这批 tick 期间有没有命令被拒(不用另外调 `get_tick` 对比)。`ticks < 1`
时直接返回错误(不静默当 0 处理)。未 `reset_simulation` 时返回错误。

### `submit_command`
```csharp
[McpServerTool, Description("提交一条原子命令(不会立即执行,下次 step 时生效)")]
public static SubmitResult SubmitCommand(
    [Description("命令类型:PlaceEntity/RemoveEntity/MovePlayer/StopPlayer/MineStart/MineStop/CraftEnqueue/TransferToEntity/TransferFromEntity/SetRecipe/RotateEntity")]
    string type,
    [Description("目标/来源坐标 X")] int x,
    [Description("目标/来源坐标 Y")] int y,
    [Description("原型 id(和 protoName 二选一)")] int? protoId = null,
    [Description("原型名字(和 protoId 二选一,内部查表转 id)")] string? protoName = null,
    [Description("朝向 0=北 1=东 2=南 3=西,默认 0")] byte rotation = 0,
    [Description("数量,默认 0")] int count = 0)
```
- `type` 按 `Enum.TryParse<CommandType>(type, ignoreCase: true, out var t)` 解析,
  解析失败返回错误(列出合法值)。
- `protoId`/`protoName` 都不给或都给,返回错误;只给 `protoName` 时按
  `sim.Prototypes` 里全部原型(`GetById(0..Count-1)`)线性扫描找
  `.Name == protoName` 的第一个匹配——**如果不同 CLR 类型下有重名的原型
  (item 和 entity 允许同名,见 `PrototypeRegistry.cs:5` 的注释),这里会拿到
  歧义结果**;`submit_command` 场景下按 `CommandType` 基本能唯一确定该找
  哪类原型(比如 `PlaceEntity` 找实体类,`TransferToEntity` 找物品类),但
  这份 spec 不预先枚举每个 `CommandType` 对应哪些具体 CLR 类型来做精确过滤
  ——先用"名字全局唯一"假设实现,如果测试阶段发现 `data/base` 里真的有重名
  冲突导致解析错误,再回来加类型过滤(YAGNI,不确定这个问题真实存在之前不做)。
- 命令提交后不是"填 X/Y/Rotation/Count 全部字段再决定校验",而是原样构造
  `Command { Type = t, ProtoId = resolvedId, X = x, Y = y, Rotation = rotation, Count = count }`
  然后 `sim.Submit(command)`——校验/拒绝逻辑完全在 `Simulation.Apply` 里,
  MCP 层不重复做合法性判断。
- 返回 `SubmitResult(bool Queued)`——`Queued` 恒为 `true`(`Submit` 只是入队,
  不会立即失败;真正的接受/拒绝要等下一次 `step` 后看 `RejectedThisCall`)。
  工具描述里要说清楚这一点,避免调用方误以为 `submit_command` 会立刻告诉
  它命令有没有被接受。

### `get_tick`
```csharp
[McpServerTool, Description("查询当前 tick 和已拒绝命令总数,不推进模拟")]
public static TickInfo GetTick()
```
返回 `TickInfo(long Tick, int RejectedCommandCount)`。

### `get_entity_at`
```csharp
[McpServerTool, Description("查询指定坐标的实体")]
public static EntityInfo? GetEntityAt(int x, int y)
```
`World.GetEntityAt(x, y)` 拿 `EntityId`,`IsValid` 为 false 时返回 `null`
(MCP 允许工具返回 null 表示"没有")。否则读 `Entities.Get(id)` 拿
`EntityData`,返回
`EntityInfo(int Index, int Generation, string ProtoName, int ProtoId, int X, int Y, byte Rotation)`
——`ProtoName` 从 `Prototypes.GetById(data.ProtoId).Name` 取,省得调用方
再反查一次。

### `get_inventory`
```csharp
[McpServerTool, Description("查询指定坐标实体的库存内容")]
public static InventoryInfo? GetInventory(int x, int y, int role = 0)
```
`World.GetEntityAt` 找实体,无效返回 `null`;`Inventories.GetInventoryId(id, role)`
无效也返回 `null`(role 不存在,比如给一个箱子传 `role=2`)。否则遍历
`inv[0..SlotCount)`,跳过 `IsEmpty` 的槽位,返回
`InventoryInfo(int SlotCount, List<SlotInfo> Slots)`,
`SlotInfo(int Slot, string ItemName, int ItemProtoId, int Count)`。

### `resolve_prototype`
```csharp
[McpServerTool, Description("按名字查原型 id;同名可能匹配多个不同类型的原型,此时都返回,自己按 TypeName 挑")]
public static List<PrototypeInfo> ResolvePrototype(string name, string? typeName = null)
```
线性扫描 `Prototypes.GetById(0..Count-1)`,过滤 `.Name == name`(以及
`typeName` 给了的话再过滤 `.GetType().Name == typeName`),返回
`PrototypeInfo(int Id, string Name, string TypeName)` 列表(可能 0、1 或多条,
调用方自己按需处理;不是"找不到就报错"的语义,找不到就是空列表)。

### `list_prototypes`
```csharp
[McpServerTool, Description("列出全部已加载的原型(名字+类型),用于发现能用什么")]
public static List<PrototypeInfo> ListPrototypes(string? typeNameFilter = null)
```
同样线性扫描全部,`typeNameFilter` 给了就按 `.GetType().Name` 过滤。

### `compute_state_hash`
```csharp
[McpServerTool, Description("计算当前模拟状态的 FNV-1a 哈希,用于跨会话对拍确定性")]
public static HashResult ComputeStateHash()
```
返回 `HashResult(ulong Hash)`,直接调 `sim.ComputeStateHash()`。

**所有工具在 `SimHost.Sim == null` 时统一返回一个错误**(不是抛异常导致
MCP 协议层报一个不友好的通用错误)——用 MCP SDK 的错误返回机制(工具方法
可以返回 `McpException`风格的失败,或者约定一个 `{ Error: string }` 形状的
返回值,具体用哪种交给实施阶段按 SDK 实际支持的方式定,这份 spec 只要求
"消息要清楚说明是没 reset 导致的",不钉死具体异常类型)。

## 4. 明确不做(这轮范围外)

- 截图/渲染——独立子项目,brainstorm 已确认拆分,技术可行性未验证。
- 多 session/多实例并发——brainstorm 已确认单实例够用。
- 复合高层动作(比如"造一条完整生产线"这种一次调用做十件事的封装)——
  MCP 层保持薄,复杂场景靠多次调用原子工具在对话里拼,不在 server 里加
  业务逻辑。
- `submit_command` 的按 `CommandType` 精确类型过滤重名原型——先用"名字全局
  唯一"假设,真遇到冲突再加(见 §3 `submit_command` 小节)。

## 5. 测试计划

- MCP server 项目里加一个测试项目(`sim/Faketorio.Sim.McpServer.Tests`),
  **不通过 stdio 起子进程**——直接在进程内 new 出工具类、调用工具的静态方法
  (它们本质是普通静态方法,MCP 特性只是装饰),用 xUnit 断言返回值。覆盖:
  - 未 `reset_simulation` 时每个工具返回明确错误,不抛未处理异常。
  - `reset_simulation` 后能正常 `step`/`submit_command`/查询。
  - `submit_command` 一个非法 `type` 字符串返回错误,列出合法值。
  - `submit_command` 用 `protoName` 解析(比如放一个 `"stone-furnace"`)
    等价于直接给 `protoId`。
  - `get_entity_at`/`get_inventory` 对空坐标/无效 role 返回 `null`,不抛异常。
  - 一个端到端小场景:`reset` → 放电线杆+发电机+熔炉+塞矿 → `step(200)` →
    `get_inventory` 看到产出——跟现有 `SimulationTests.cs` 里
    `Furnace_AutoMatchesAndSmeltsIronOre` 验证的是同一件事,但完全通过 MCP
    工具调用完成,不直接碰 `Simulation` API,确保工具层本身是好用的。
