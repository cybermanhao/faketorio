using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
// stdio 传输把 stdout 当成 JSON-RPC 协议通道用；默认的控制台日志 provider 也写
// stdout，两者混在一起会把协议流冲毁（这台机器上控制台日志编码还是 GBK，不是
// 合法 UTF-8）。这个 server 是个薄封装，sim 层本身也不记日志，直接全部关掉。
builder.Logging.ClearProviders();
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
