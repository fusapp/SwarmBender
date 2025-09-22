using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
using SwarmBender.Cli.Infrastructure;
using SwarmBender.Cli.Commands;
using SwarmBender.Cli.Commands.Secrets;
using SwarmBender.Cli.Commands.Utils;
using SwarmBender.Cli.Commands.Utils.Azdo;
using SwarmBender.Cli.Commands.Utils.Infisical;
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
services.AddSingleton<IRenderExecutor, RenderExecutor>(); // NEW
services.AddSwarmBenderSecrets();

var registrar = new TypeRegistrar(services);
var app = new CommandApp(registrar);

app.Configure(cfg =>
{
    cfg.SetApplicationName("sb");
    cfg.AddCommand<InitCommand>("init")
       .WithDescription("Initialize project root or a specific stack scaffold.");
    cfg.AddCommand<ValidateCommand>("validate")
       .WithDescription("Validate a stack (or all stacks) against policies and basic schema.");
    cfg.AddCommand<RenderCommand>("render")
       .WithDescription("Render final stack.yml for one or more environments.");
    cfg.AddSecretsCommands();
    cfg.AddBranch("utils", utils =>
    {
       utils.AddCommand<UtilsCommand>("utils");
       utils.AddBranch("infisical", inf =>
       {
          inf.AddCommand<InfisicalUploadCommand>("upload");
          inf.AddCommand<InfisicalInitCommand>("init");
       });
       
       utils.AddBranch("azdo", azdo =>
       {
          azdo.AddBranch("pipeline", pipeline =>
          {
             pipeline.AddCommand<AzdoPipelineInitCommand>("init");
          });
       });
       
    });
});

return app.Run(args);
