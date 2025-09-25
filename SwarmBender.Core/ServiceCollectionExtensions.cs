using Microsoft.Extensions.DependencyInjection;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Config;
using SwarmBender.Core.IO;
using SwarmBender.Core.Pipeline;
using SwarmBender.Core.Providers.Azure;
using SwarmBender.Core.Providers.Infisical;

namespace SwarmBender.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSwarmBenderCore(this IServiceCollection services, string rootPath)
    {
        services.AddSingleton<IFileSystem, FileSystem>();
        services.AddSingleton<IYamlEngine, YamlEngine>();

        // Load SbConfig from ops/sb.yml
        services.AddSingleton<ISbConfigLoader, SbConfigLoader>();
        services.AddSingleton(provider =>
        {
            var loader = provider.GetRequiredService<ISbConfigLoader>();
            // blocking load at startup is fine for CLI
            return loader.LoadAsync(rootPath, CancellationToken.None).GetAwaiter().GetResult();
        });

        // Providers
        services.AddSingleton<IAzureKvCollector, AzureKvCollector>();
        services.AddSingleton<IInfisicalCollector, InfisicalCollector>();

        // Orchestrator + stages
        services.AddSingleton<IRenderOrchestrator, RenderOrchestrator>();
        // services.AddSingleton<IRenderStage, TemplateLoaderStage>();
        // services.AddSingleton<IRenderStage, ApplyOverlaysStage>();
        // services.AddSingleton<IRenderStage, EnvJsonCollectStage>();
        // services.AddSingleton<IRenderStage, ProvidersAggregateStage>();
        // services.AddSingleton<IRenderStage, EnvironmentApplyStage>();
        // services.AddSingleton<IRenderStage, LabelsStage>();
        // services.AddSingleton<IRenderStage, SecretsAttachStage>();
        // services.AddSingleton<IRenderStage, TokenExpandStage>();
        // services.AddSingleton<IRenderStage, SerializeStage>();

        return services;
    }
}