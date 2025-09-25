using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
using SwarmBender.Cli;
using SwarmBender.Cli.Commands;
using SwarmBender.Core;

var services = new ServiceCollection()
    .AddSwarmBenderCore(Directory.GetCurrentDirectory())
    ;

var app = new CommandApp(new SwarmBenderTypeRegistrar(services));
app.Configure(cfg =>
{
    cfg.SetApplicationName("sb");
    cfg.AddCommand<InitCommand>("init");
    cfg.AddCommand<RenderCommand>("render");
});

return await app.RunAsync(args);