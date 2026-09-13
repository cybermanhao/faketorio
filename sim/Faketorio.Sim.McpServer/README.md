# Faketorio.Sim.McpServer

把 `Faketorio.Sim` 的 `Simulation.Submit`/`Step`/查询 API 包成 9 个 stdio MCP
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

## 本地跑

```bash
dotnet run --project sim/Faketorio.Sim.McpServer
```

## 在 Claude Code 里注册

按 Claude Code 当前的 MCP server 注册文档操作（配置文件路径/格式以官方文档
为准，这里不重复维护一份容易过期的说明）。

## 范围外

截图/渲染能力（驱动 Godot 截图）是独立的第二个子项目，还没开始——见
`docs/superpowers/specs/2026-09-13-mcp-sim-driver-design.md` §0。
