# Faketorio.Sim.McpServer

把 `Faketorio.Sim` 的 `Simulation.Submit`/`Step`/查询 API 包成 10 个 stdio MCP
工具，供 Claude Code 在对话里直接驱动/探索 sim 层场景，不用写新 xUnit 测试
再编译。

## 工具列表

- `reset_simulation(seed?, dataDir?)` —— 创建新模拟。
- `step(ticks?)` —— 推进 tick。
- `submit_command(type, x, y, protoId?, protoName?, rotation?, count?)` —— 提交命令。
- `get_tick()` —— 查当前 tick/拒绝数。
- `get_entity_at(x, y)` —— 查坐标上的实体。
- `get_inventory(x, y, role?)` —— 查实体库存。
- `resolve_prototype(name, typeName?)` —— 名字转原型 id。
- `list_prototypes(typeNameFilter?)` —— 列出全部原型。
- `compute_state_hash()` —— 算确定性哈希。
- `get_screenshot(cameraX, cameraY, zoomPpt?, width?, height?)` —— 把当前模拟状态渲染成一张 PNG 截图返回。需要设置 `FAKETORIO_GODOT_EXE` 环境变量指向 Godot 可执行文件；每次调用会另起一个 Godot 子进程，有秒级延迟。

## 本地跑

```bash
dotnet run --project sim/Faketorio.Sim.McpServer
```

## 在 Claude Code 里注册

按 Claude Code 当前的 MCP server 注册文档操作（配置文件路径/格式以官方文档
为准，这里不重复维护一份容易过期的说明）。

## 截图能力的额外要求

`get_screenshot` 工具需要:
1. 本机装有 Godot 4.5.1(Mono 版,支持 C#)。
2. 环境变量 `FAKETORIO_GODOT_EXE` 指向 Godot 可执行文件的完整路径。
3. `game/` 项目(截图用的渲染场景)跟这个 MCP server 项目在同一个仓库里,
   相对路径关系固定——不要把这个 MCP server 单独拷贝部署到别的地方运行。

截图截到的是 MCP server 这边操作历史重放出来的模拟状态,不是某个正在运行的
真人游戏会话——细节见 `docs/superpowers/specs/2026-09-13-mcp-screenshot-design.md`。

`get_screenshot` 每次调用都会从 tick 0 重放完整操作历史,固定 30 秒超时；操作历史
很长(几千条命令/step)的会话最终可能每次调用都超时,目前唯一的恢复手段是
`reset_simulation`。
