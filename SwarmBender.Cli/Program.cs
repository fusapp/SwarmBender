using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
using SwarmBender.Cli;
using SwarmBender.Cli.Commands;
using SwarmBender.Core;
using SwarmBender.Core.Abstractions;

var services = new ServiceCollection()
    .AddSwarmBenderCore(Directory.GetCurrentDirectory())
    .AddSingleton<IOutput,ConsoleOutput>()
    ;

var app = new CommandApp(new SwarmBenderTypeRegistrar(services));
app.Configure(cfg =>
{
    cfg.SetApplicationName("sb");
    cfg.UseAssemblyInformationalVersion();
    cfg.AddCommand<InitCommand>("init");
    cfg.AddCommand<RenderCommand>("render");
    cfg.AddBranch("secret", b =>
    {
        b.AddCommand<SecretListCommand>("list");
        b.AddCommand<SecretSyncCommand>("sync");
        b.AddCommand<SecretPruneCommand>("prune");
        b.AddCommand<SecretDiffCommand>("diff");
    });

    // (opsiyonel) utils/infisical
    cfg.AddBranch("utils", b =>
    {
        b.AddBranch("infisical", i =>
        {
            i.AddCommand<InfisicalUploadCommand>("upload").WithDescription("Upload discovered secrets to Infisical using SDK.");
        });
    });
    cfg.AddBranch("azdo", azdo =>
    {
        azdo.AddBranch("pipeline", pipeline =>
        {
            pipeline.AddCommand<AzdoPipelineInitCommand>("init");
        });
    });
    cfg.AddBranch("doctor", doctor =>
    {
        doctor.AddBranch("stage", stage =>
        {
            stage.AddCommand<DoctorStageListCommand>("list");
        });
    });
    cfg.AddBranch("config", (config) =>
    {
        config.AddCommand<ConfigExportCommand>("export")
            .WithDescription("Export merged appsettings.json (tokens & envvars expanded)");
    });
   
});

return await app.RunAsync(args);