using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwarmBender.Core;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Models;
using SwarmBender.Core.IO;
using SwarmBender.Core.Pipeline;
using SwarmBender.Core.Pipeline.Stages;
using System;
using System.IO;
using System.Threading.Tasks;
using SwarmBender.Core.Config;

namespace SwarmBender.Core.Tests
{
    [TestClass]
    public sealed class RenderSmokeTests
    {
        [TestMethod]
        public async Task Template_Then_Overlay_Serializes_With_Logging()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "sb-smoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);

            try
            {
                // stacks/demo template
                Directory.CreateDirectory(Path.Combine(tmp, "stacks", "demo"));
                File.WriteAllText(Path.Combine(tmp, "stacks", "demo", "docker-stack.template.yml"),
                    "services:\n" +
                    "  api:\n" +
                    "    image: demo:1\n" +
                    "    deploy:\n" +
                    "      labels:\n" +
                    "      - traefik.enable=true\n");

                // overlay: stacks/all/dev/stack/global.yml
                Directory.CreateDirectory(Path.Combine(tmp, "stacks", "all", "dev", "stack"));
                File.WriteAllText(Path.Combine(tmp, "stacks", "all", "dev", "stack", "global.yml"),
                    "services:\n" +
                    "  \"*\":\n" +                // wildcard overlay (ApplyOverlaysStage shallow merge covers logging at service level)
                    "    logging:\n" +
                    "      driver: json-file\n");

                // minimal config -> overlay order + writeHistory
                var cfg = new SbConfig
                {
                    Render = new RenderSection
                    {
                        AppsettingsMode = "env",
                        OutDir = "ops/state/last",
                        WriteHistory = true,
                        OverlayOrder = new()
                        {
                            "stacks/all/{env}/stack/*.y?(a)ml",
                            "stacks/{stackId}/{env}/stack/*.y?(a)ml"
                        }
                    },
                    Tokens = new TokensSection
                    {
                        User = new() { ["COMPANY_NAME"] = "fusapp" }
                    }
                };

                // DI
                using var sp = new ServiceCollection()
                    .AddSingleton<ISbConfigLoader,MockSbConfigLoader>()
                    .AddSingleton<IFileSystem, FileSystem>()
                    .AddSingleton<IYamlEngine, YamlEngine>()
                    .AddSingleton<IRenderStage, LoadTemplateStage>()
                    .AddSingleton<IRenderStage, ApplyOverlaysStage>()
                    .AddSingleton<IRenderStage, EnvironmentApplyStage>()
                    .AddSingleton<IRenderStage, LabelsStage>()
                    .AddSingleton<IRenderStage, SecretsAttachStage>()
                    .AddSingleton<IRenderStage, TokenExpandStage>()
                    .AddSingleton<IRenderStage, SerializeStage>()
                    .AddSingleton<IRenderOrchestrator, RenderOrchestrator>()
                    .BuildServiceProvider();

                var orch = sp.GetRequiredService<IRenderOrchestrator>();

                // (root, stackId, env, appSettingsMode, outDir, writeHistory)
                var req = new RenderRequest(tmp, "demo", "dev", "env", "ops/state/last", true);
                var res = await orch.RunAsync(req);

                Assert.IsTrue(File.Exists(res.OutFile), "Output YAML should be written.");

                var yaml = await File.ReadAllTextAsync(res.OutFile);
                StringAssert.Contains(yaml, "traefik.enable=true"); // from template
                StringAssert.Contains(yaml, "driver: json-file");   // from overlay
            }
            finally
            {
                try { Directory.Delete(tmp, recursive: true); } catch { /* ignore */ }
            }
        }
    }
}