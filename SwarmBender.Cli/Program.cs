using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;
using SwarmBender.Cli.Infrastructure;
using SwarmBender.Cli.Commands;
using SwarmBender.Services;
using SwarmBender.Services.Abstractions;

var services = new ServiceCollection();

// Core services
services.AddSingleton<IInitExecutor, InitExecutor>();
services.AddSingleton<IFileSystem, FileSystem>();
services.AddSingleton<IEnvParser, EnvParser>();
services.AddSingleton<IStubContent, StubContent>();
services.AddSingleton<IValidator, Validator>();
services.AddSingleton<IYamlLoader, YamlLoader>();


var registrar = new TypeRegistrar(services);
var app = new CommandApp(registrar);

app.Configure(cfg =>
{
    cfg.SetApplicationName("sb");
    cfg.AddCommand<InitCommand>("init")
       .WithDescription("Initialize project root or a specific stack scaffold.");
    cfg.AddCommand<ValidateCommand>("validate")
       .WithDescription("Validate a stack (or all stacks) against policies and basic schema.");
});

return app.Run(args);
