using Microsoft.Extensions.DependencyInjection;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Data.Models;
using SwarmBender.Core.IO;
using SwarmBender.Core.Pipeline;

namespace SwarmBender.Core.Tests;

[TestClass]
public class RenderSmokeTests
{
    [TestMethod]
    public async Task Template_Then_Overlay_Serializes()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "sb-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            Directory.CreateDirectory(Path.Combine(tmp, "stacks", "demo"));
            Directory.CreateDirectory(Path.Combine(tmp, "stacks", "all", "dev", "stack"));

            File.WriteAllText(Path.Combine(tmp, "stacks", "demo", "docker-stack.template.yml"),
                "services:\n  api:\n    image: demo:1\n    deploy:\n      labels:\n      - traefik.enable=true\n");

            File.WriteAllText(Path.Combine(tmp, "stacks", "all", "dev", "stack", "global.yml"),
                "services:\n  \"*\":\n    logging:\n      driver: json-file\n");

            using var sp = new ServiceCollection()
                .AddSingleton<SwarmBender.Core.Data.Models.SbConfig>()   // default config yeterli
                .AddSwarmBenderCore(Directory.GetCurrentDirectory())
                .BuildServiceProvider();

            var orch = sp.GetRequiredService<IRenderOrchestrator>();

            // RenderRequest: (root, stackId, env, appSettingsMode, outDir, writeHistory)
            var res = await orch.RunAsync(
                new RenderRequest(tmp, "demo", "dev", "env", "ops/state/last", true));

            Assert.IsTrue(File.Exists(res.OutFile), "Output YAML should be written.");

            var yaml = await File.ReadAllTextAsync(res.OutFile);
            StringAssert.Contains(yaml, "traefik.enable=true"); // template label
            StringAssert.Contains(yaml, "driver: json-file");   // overlay logging
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* ignore */ }
        }
    }
    
    
}