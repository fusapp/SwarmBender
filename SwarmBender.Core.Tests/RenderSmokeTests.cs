using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SwarmBender.Core.Abstractions;
using SwarmBender.Core.Config;
using SwarmBender.Core.Data.Models;
using SwarmBender.Core.IO;
using SwarmBender.Core.Pipeline;
using SwarmBender.Core.Pipeline.Stages;
using SwarmBender.Core.Providers.Azure;
using SwarmBender.Core.Providers.Infisical;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SwarmBender.Core.Tests
{
    [TestClass]
    public sealed class RenderExpandedTests
    {
        // ---- Helpers --------------------------------------------------------

        private static ServiceProvider BuildSp(
            SbConfig cfg,
            IAzureKvCollector? kv = null,
            IInfisicalCollector? inf = null)
        {
            var sc = new ServiceCollection()
                .AddSingleton<ISbConfigLoader, MockSbConfigLoader>()
                .AddSingleton<SbConfig>(cfg)
                .AddSingleton<IFileSystem, FileSystem>()
                .AddSingleton<IYamlEngine, YamlEngine>()
                .AddSingleton<IRenderStage, LoadTemplateStage>()
                .AddSingleton<IRenderStage, ApplyOverlaysStage>()
                .AddSingleton<IRenderStage, EnvJsonCollectStage>()
                .AddSingleton<IRenderStage, ProvidersAggregateStage>()
                .AddSingleton<IRenderStage, EnvironmentApplyStage>()
                .AddSingleton<IRenderStage, LabelsStage>()
                .AddSingleton<IRenderStage, SecretsAttachStage>()
                .AddSingleton<IRenderStage, TokenExpandStage>()
                .AddSingleton<IRenderStage, SerializeStage>()
                .AddSingleton<IRenderOrchestrator, RenderOrchestrator>();

            // optionally plug fake collectors
            if (kv != null) sc.AddSingleton<IAzureKvCollector>(kv);
            else sc.AddSingleton<IAzureKvCollector, NoopAzureKvCollector>();

            if (inf != null) sc.AddSingleton<IInfisicalCollector>(inf);
            else sc.AddSingleton<IInfisicalCollector, NoopInfisicalCollector>();

            return sc.BuildServiceProvider();
        }

        private static SbConfig MinimalCfg() => new()
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

        // ---- Tests ----------------------------------------------------------

        [TestMethod]
        public async Task Overlay_Order_LastWins_PerService()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "sb-t-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                // template
                Directory.CreateDirectory(Path.Combine(tmp, "stacks", "demo"));
                File.WriteAllText(Path.Combine(tmp, "stacks", "demo", "docker-stack.template.yml"),
@"services:
  api:
    image: demo:1
    deploy:
      labels:
        one: base
        two: base");

                // all/global overlay (sets one, adds three)
                Directory.CreateDirectory(Path.Combine(tmp, "stacks", "all", "dev", "stack"));
                File.WriteAllText(Path.Combine(tmp, "stacks", "all", "dev", "stack", "global.yml"),
@"services:
  api:
    deploy:
      labels:
        one: all
        three: all");

                // stack/env overlay (overrides 'one' and 'three', adds 'two')
                Directory.CreateDirectory(Path.Combine(tmp, "stacks", "demo", "dev", "stack"));
                File.WriteAllText(Path.Combine(tmp, "stacks", "demo", "dev", "stack", "service.yml"),
@"services:
  api:
    deploy:
      labels:
        one: stack
        three: stack
        two: stack");

                using var sp = BuildSp(MinimalCfg());
                var orch = sp.GetRequiredService<IRenderOrchestrator>();
                var res = await orch.RunAsync(new RenderRequest(tmp, "demo", "dev", "env", "ops/state/last", true));
                var yaml = await File.ReadAllTextAsync(res.OutFile);

                StringAssert.Contains(yaml, "one=stack");
                StringAssert.Contains(yaml, "two=stack");
                StringAssert.Contains(yaml, "three=stack");
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        [TestMethod]
        public async Task EnvJson_And_ProvidersEnv_Are_Applied_To_Services()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "sb-t-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            string? prev = null;
            try
            {
                // template
                Directory.CreateDirectory(Path.Combine(tmp, "stacks", "demo"));
                File.WriteAllText(Path.Combine(tmp, "stacks", "demo", "docker-stack.template.yml"),
@"services:
  api:
    image: ${COMPANY_NAME}/api:1
    environment:
      - FROM_TEMPLATE=true");

                // env json
                Directory.CreateDirectory(Path.Combine(tmp, "stacks", "all", "dev", "env"));
                File.WriteAllText(Path.Combine(tmp, "stacks", "all", "dev", "env", "defaults.json"),
@"{ ""REDIS__HOST"": ""redis"", ""FeatureX.Enabled"": true }");

                // allowlist + set an OS env var to import
                Directory.CreateDirectory(Path.Combine(tmp, "stacks", "demo"));
                File.WriteAllText(Path.Combine(tmp, "stacks", "demo", "use-envvars.json"),
@"[ ""CONNECTIONSTRINGS__MAIN"", ""REDIS__*"" ]");

                prev = Environment.GetEnvironmentVariable("CONNECTIONSTRINGS__MAIN");
                Environment.SetEnvironmentVariable("CONNECTIONSTRINGS__MAIN", "Server=db;");

                var cfg = MinimalCfg();
                cfg.Providers.Env.AllowlistFileSearch.Add("stacks/{stackId}/use-envvars.json");

                using var sp = BuildSp(cfg);
                var orch = sp.GetRequiredService<IRenderOrchestrator>();
                var res = await orch.RunAsync(new RenderRequest(tmp, "demo", "dev", "env", "ops/state/last", false));
                var yaml = await File.ReadAllTextAsync(res.OutFile);

                StringAssert.Contains(yaml, "FROM_TEMPLATE: true");
                StringAssert.Contains(yaml, "REDIS__HOST: redis");
                StringAssert.Contains(yaml, "FeatureX__Enabled: True");
                StringAssert.Contains(yaml, "CONNECTIONSTRINGS__MAIN: Server=db;");
            }
            finally
            {
                if (prev is not null) Environment.SetEnvironmentVariable("CONNECTIONSTRINGS__MAIN", prev);
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        [TestMethod]
        public async Task LabelsStage_Writes_List_Syntax_And_Merges_xsb()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "sb-t-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                // template + x-sb.labels at root and service level
                Directory.CreateDirectory(Path.Combine(tmp, "stacks", "demo"));
                File.WriteAllText(Path.Combine(tmp, "stacks", "demo", "docker-stack.template.yml"),
                    @"
services:
  api:
    image: demo:1
    deploy:
      labels:
        tier: app
        owner: core
        trace: enabled");

                using var sp = new ServiceCollection()
                    .AddSingleton<ISbConfigLoader, MockSbConfigLoader>() // senin mevcut mock'un
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
                var res  = await orch.RunAsync(new RenderRequest(tmp, "demo", "dev", "env", "ops/state/last",false));
                var yaml = await File.ReadAllTextAsync(res.OutFile);

                // list style under deploy.labels
                StringAssert.Contains(yaml, "- owner=core");
                StringAssert.Contains(yaml, "- tier=app");      // service x-sb overrides root
                StringAssert.Contains(yaml, "- trace=enabled");
            }
            finally
            {
                try { Directory.Delete(tmp, recursive: true); } catch { /* ignore */ }
            }
        }

        [TestMethod]
        public async Task SecretsAttach_Removes_Plain_Env_And_Adds_External_Refs()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "sb-t-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                Directory.CreateDirectory(Path.Combine(tmp, "stacks", "demo"));
                File.WriteAllText(Path.Combine(tmp, "stacks", "demo", "docker-stack.template.yml"),
@"services:
  api:
    image: demo:1
    environment:
      - ConnectionStrings__Main=Server=db;User=sa;Pwd=secret");

                var cfg = MinimalCfg();
                cfg.Secretize.Enabled = true;
                cfg.Secretize.Paths = new() { "ConnectionStrings.*", "ConnectionStrings__*" };

                using var sp = BuildSp(cfg);
                var orch = sp.GetRequiredService<IRenderOrchestrator>();
                var res  = await orch.RunAsync(new RenderRequest(tmp, "demo", "dev", "env", "ops/state/last", false));
                var yaml = await File.ReadAllTextAsync(res.OutFile);

                // env key should be removed
                Assert.IsFalse(yaml.Contains("ConnectionStrings__Main=Server=db"), "plain env should be removed");

                // root secrets should include generated external secret name
                StringAssert.Contains(yaml, "secrets:");
                StringAssert.Contains(yaml, "external:"); // ExternalDef serialized
                // service secret ref exists
                StringAssert.Contains(yaml, "secrets:");
                StringAssert.Contains(yaml, "- source: sb_demo_api_dev_ConnectionStrings__Main_"); // nameTemplate prefix
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        [TestMethod]
        public async Task Providers_Order_Respected_KV_And_Infisical_LastWins()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "sb-t-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                Directory.CreateDirectory(Path.Combine(tmp, "stacks", "demo"));
                File.WriteAllText(Path.Combine(tmp, "stacks", "demo", "docker-stack.template.yml"),
@"services:
  api:
    image: demo:1
    environment:
      - FOO=from-template");

                var cfg = MinimalCfg();
                cfg.Providers.AzureKv = new ProvidersAzureKv { Enabled = true, VaultUrl = "https://example.vault" };
                cfg.Providers.Infisical = new ProvidersInfisical { Enabled = true };
                cfg.Providers.Order = new()
                {
                    new() { Type = "env" },
                    new() { Type = "azure-kv" },
                    new() { Type = "infisical" }
                };

                var fakeKv  = new FakeKvCollector(new Dictionary<string,string> {
                    ["FOO"] = "from-kv", ["BAR"] = "from-kv"
                });
                var fakeInf = new FakeInfisicalCollector(new Dictionary<string,string> {
                    ["FOO"] = "from-inf", ["BAZ"] = "from-inf"
                });

                using var sp = BuildSp(cfg, fakeKv, fakeInf);
                var orch = sp.GetRequiredService<IRenderOrchestrator>();
                var res  = await orch.RunAsync(new RenderRequest(tmp, "demo", "dev", "env", "ops/state/last", false));
                var yaml = await File.ReadAllTextAsync(res.OutFile);

                // Last provider is infisical -> wins on FOO
                StringAssert.Contains(yaml, "FOO: from-inf");
                // From KV only (not overridden by last) -> BAR present
                StringAssert.Contains(yaml, "BAR: from-kv");
                // From Infisical only -> BAZ present
                StringAssert.Contains(yaml, "BAZ: from-inf");
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }
    }

    // ---- Test doubles ------------------------------------------------------

    public sealed class MockSbConfigLoader : ISbConfigLoader
    {
        private readonly SbConfig _cfg;
        public MockSbConfigLoader(SbConfig? cfg = null) => _cfg = cfg ?? new SbConfig();
        public Task<SbConfig> LoadAsync(string rootPath, CancellationToken ct)
            => Task.FromResult(_cfg);
    }

    public sealed class NoopAzureKvCollector : IAzureKvCollector
    {
        public Task<Dictionary<string, string>> CollectAsync(ProvidersAzureKv cfg, string scope, CancellationToken ct)
            => Task.FromResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    public sealed class NoopInfisicalCollector : IInfisicalCollector
    {
        public Task<Dictionary<string, string>> CollectAsync(ProvidersInfisical cfg, string scope, CancellationToken ct)
            => Task.FromResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    public sealed class FakeKvCollector : IAzureKvCollector
    {
        private readonly Dictionary<string, string> _data;
        public FakeKvCollector(Dictionary<string,string> data)
            => _data = new(data, StringComparer.OrdinalIgnoreCase);

        public Task<Dictionary<string, string>> CollectAsync(ProvidersAzureKv cfg, string scope, CancellationToken ct)
            => Task.FromResult(new Dictionary<string, string>(_data, StringComparer.OrdinalIgnoreCase));
    }

    public sealed class FakeInfisicalCollector : IInfisicalCollector
    {
        private readonly Dictionary<string, string> _data;
        public FakeInfisicalCollector(Dictionary<string,string> data)
            => _data = new(data, StringComparer.OrdinalIgnoreCase);

        public Task<Dictionary<string, string>> CollectAsync(ProvidersInfisical cfg, string scope, CancellationToken ct)
            => Task.FromResult(new Dictionary<string, string>(_data, StringComparer.OrdinalIgnoreCase));
    }
}