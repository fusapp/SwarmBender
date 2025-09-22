using Spectre.Console;
using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;

namespace SwarmBender.Services;

/// <summary>Executes init scaffolding flow (overlay-based v3).</summary>
public sealed class InitExecutor : IInitExecutor
{
    private readonly IFileSystem _fs;
    private readonly IEnvParser _envs;
    private readonly IStubContent _stub;

    public InitExecutor(IFileSystem fs, IEnvParser envs, IStubContent stub)
        => (_fs, _envs, _stub) = (fs, envs, stub);

    public async Task<InitResult> ExecuteAsync(InitRequest r, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(r.RootPath);
        var (validEnvs, invalidEnvs) = _envs.Normalize(r.EnvNames);

        var (created, skipped) = (0, 0);

        try
        {
            if (string.IsNullOrWhiteSpace(r.StackId))
            {
                (created, skipped) = await RootScaffoldAsync(root, validEnvs, r.NoGlobalDefs, r.DryRun, r.Quiet, ct);
            }
            else
            {
                (created, skipped) = await StackScaffoldAsync(root, r.StackId!, validEnvs, r.NoGlobalDefs, r.NoDefs,
                    r.NoAliases, r.DryRun, r.Quiet, ct);
            }
        }
        catch (Exception ex)
        {
            if (!r.Quiet) AnsiConsole.MarkupLine("[red]ERROR:[/] {0}", ex.Message);
            return new InitResult(created, skipped, invalidEnvs);
        }

        return new InitResult(created, skipped, invalidEnvs);
    }

    private static void Tally(WriteResult r, ref int created, ref int skipped)
    {
        if (r == WriteResult.Created) created++;
        else skipped++;
    }

    private async Task<(int created, int skipped)> RootScaffoldAsync(string root, List<string> envs, bool noGlobalDefs,
        bool dry, bool quiet, CancellationToken ct)
    {
        var (c, s) = (0, 0);

        // Root folders
        var r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "stacks"), dry, quiet, ct);
        Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "stacks", "all"), dry, quiet, ct);
        Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "services"), dry, quiet, ct);
        Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "metadata"), dry, quiet, ct);
        Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "ops"), dry, quiet, ct);
        Tally(r1, ref c, ref s);

        // Metadata
        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "metadata", "groups.yml"), _stub.GroupsYaml, dry, quiet, ct);
        Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "metadata", "tenants.yml"), _stub.TenantsYaml, dry, quiet,
            ct);
        Tally(r1, ref c, ref s);

        // Ops
        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "ops", "policies"), dry, quiet, ct);
        Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "ops", "policies", "guardrails.yml"), _stub.GuardrailsYaml,
            dry, quiet, ct);
        Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "ops", "policies", "labels.yml"), _stub.LabelsPolicyYaml, dry,
            quiet, ct);
        Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "ops", "policies", "images.yml"), _stub.ImagesPolicyYaml, dry,
            quiet, ct);
        Tally(r1, ref c, ref s);

        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "ops", "checks"), dry, quiet, ct);
        Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "ops", "checks", "compose-v3.yml"), _stub.ComposeV3Yaml, dry,
            quiet, ct);
        Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "ops", "checks", "required-keys.yml"), _stub.RequiredKeysYaml,
            dry, quiet, ct);
        Tally(r1, ref c, ref s);

        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "ops", "state", "last"), dry, quiet, ct);
        Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "ops", "state", "history"), dry, quiet, ct);
        Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "ops", "reports", "preflight"), dry, quiet, ct);
        Tally(r1, ref c, ref s);

        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "ops", "vars"), dry, quiet, ct);
        Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "ops", "vars", "secrets-provider.yml"),
            _stub.SecretsProviderYaml, dry, quiet, ct);
        Tally(r1, ref c, ref s);

        // Global allowlist for process env (optional)
        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "stacks", "all", "use-envvars.json"),
            _stub.UseEnvVarsDefaultJson, dry, quiet, ct);
        Tally(r1, ref c, ref s);

        if (!noGlobalDefs)
        {
            foreach (var env in envs)
            {
                // stacks/all/<env>/env with appsettings & defaults
                var basePath = Path.Combine(root, "stacks", "all", env);
                r1 = await _fs.EnsureDirectoryAsync(Path.Combine(basePath, "env"), dry, quiet, ct);
                Tally(r1, ref c, ref s);
                r1 = await _fs.EnsureFileAsync(Path.Combine(basePath, "env", "default.json"), _stub.EnvDefaultJson(env),
                    dry, quiet, ct);
                Tally(r1, ref c, ref s);
                r1 = await _fs.EnsureFileAsync(Path.Combine(basePath, "env", "appsettings.json"), "{}", dry, quiet, ct);
                Tally(r1, ref c, ref s);

                // stacks/all/<env>/stack overlays
                r1 = await _fs.EnsureDirectoryAsync(Path.Combine(basePath, "stack"), dry, quiet, ct);
                Tally(r1, ref c, ref s);
                r1 = await _fs.EnsureFileAsync(Path.Combine(basePath, "stack", "global.yml"),
                    _stub.GlobalStackOverlayYaml(env), dry, quiet, ct);
                Tally(r1, ref c, ref s);

                // empty secrets map for this env (optional but helpful)
                r1 = await _fs.EnsureFileAsync(Path.Combine(root, "ops", "vars", $"secrets-map.{env}.yml"),
                    _stub.SecretsMapYaml(env), dry, quiet, ct);
                Tally(r1, ref c, ref s);
            }
        }

        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "ops", "README.md"), _stub.OpsReadme, dry, quiet, ct);
        Tally(r1, ref c, ref s);
        r1 = await _fs.AppendGitignoreAsync(root, _stub.GitignoreLines, dry, quiet, ct);
        Tally(r1, ref c, ref s);

        return (c, s);
    }

    private async Task<(int created, int skipped)> StackScaffoldAsync(
        string root, string stackId, List<string> envs, bool noGlobalDefs, bool noDefs, bool noAliases, bool dry,
        bool quiet, CancellationToken ct)
    {
        var (c, s) = (0, 0);
        var stackRoot = Path.Combine(root, "stacks", stackId);

        var r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "stacks"), dry, quiet, ct);
        Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureDirectoryAsync(stackRoot, dry, quiet, ct);
        Tally(r1, ref c, ref s);

        // core files
        r1 = await _fs.EnsureFileAsync(Path.Combine(stackRoot, "docker-stack.template.yml"), _stub.StackTemplateYaml,
            dry, quiet, ct);
        Tally(r1, ref c, ref s);

        if (!noDefs)
        {
            r1 = await _fs.EnsureFileAsync(Path.Combine(stackRoot, "secrets.yml"), _stub.StackSecretsStub, dry, quiet,
                ct);
            Tally(r1, ref c, ref s);
            r1 = await _fs.EnsureFileAsync(Path.Combine(stackRoot, "configs.yml"), _stub.StackConfigsStub, dry, quiet,
                ct);
            Tally(r1, ref c, ref s);
        }

        if (!noAliases)
        {
            r1 = await _fs.EnsureFileAsync(Path.Combine(stackRoot, "aliases.yml"), _stub.AliasesStub, dry, quiet, ct);
            Tally(r1, ref c, ref s);
        }

        // NEW: stack-level process env allowlist (files win over process env; this lets you whitelist adds)
        const string useEnvvarsStub = """
                                      [
                                        "CONSUL_SERVER",
                                        "CONSUL_MASTER_TOKEN"
                                      ]
                                      """;
        r1 = await _fs.EnsureFileAsync(Path.Combine(stackRoot, "use-envvars.json"), useEnvvarsStub, dry, quiet, ct);
        Tally(r1, ref c, ref s);

        // per-environment overlays + env jsons
        const string overlayStub = """
                                   # Global overlays for this environment (merged after template, before service-level).
                                   # Put any Compose keys here; examples below:
                                   #
                                   # services:
                                   #   "*":
                                   #     # x-sb-secrets:
                                   #     #   - key: ConnectionStrings__MSSQL_Master
                                   #     #   - key: Redis__Master
                                   #     # healthcheck:
                                   #     #   test: ["CMD", "curl", "-f", "http://localhost:4509/healthz"]
                                   #     #   interval: 30s
                                   #     #   timeout: 5s
                                   #     #   retries: 2
                                   #     # logging:
                                   #     #   driver: "json-file"
                                   #     #   options:
                                   #     #     max-size: "10m"
                                   #     #     max-file: "3"
                                   """;

        const string envDefaultJsonStub = """
                                          {
                                            "COMPANY_NAME": "from-json",
                                            "ENVIRONMENT_SUFFIX": ""
                                          }
                                          """;

        const string envAppsettingsJsonStub = """
                                              {
                                                // Put stack-specific appsettings overrides here.
                                                // Example:
                                                // "ConnectionStrings": {
                                                //   "MSSQL_Master": "Server=...;User Id=...;Password=...;Database=...;TrustServerCertificate=True;"
                                                // }
                                              }
                                              """;

        foreach (var env in envs)
        {
            var envDir = Path.Combine(stackRoot, env);
            r1 = await _fs.EnsureDirectoryAsync(envDir, dry, quiet, ct);
            Tally(r1, ref c, ref s);

            // overlays folder (no numeric prefix anymore)
            var stackDir = Path.Combine(envDir, "stack");
            r1 = await _fs.EnsureDirectoryAsync(stackDir, dry, quiet, ct);
            Tally(r1, ref c, ref s);

            var overlayPath = Path.Combine(stackDir, "global.yml");
            r1 = await _fs.EnsureFileAsync(overlayPath, overlayStub, dry, quiet, ct);
            Tally(r1, ref c, ref s);

            // NEW: stack/<env>/env with default.json + appsettings.json
            var envJsonDir = Path.Combine(envDir, "env");
            r1 = await _fs.EnsureDirectoryAsync(envJsonDir, dry, quiet, ct);
            Tally(r1, ref c, ref s);

            r1 = await _fs.EnsureFileAsync(Path.Combine(envJsonDir, "default.json"), envDefaultJsonStub, dry, quiet,
                ct);
            Tally(r1, ref c, ref s);

            r1 = await _fs.EnsureFileAsync(Path.Combine(envJsonDir, "appsettings.json"), envAppsettingsJsonStub, dry,
                quiet, ct);
            Tally(r1, ref c, ref s);
        }

        // ops footer (keep)
        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "ops"), dry, quiet, ct);
        Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "ops", "README.md"), _stub.OpsReadme, dry, quiet, ct);
        Tally(r1, ref c, ref s);
        r1 = await _fs.AppendGitignoreAsync(root, _stub.GitignoreLines, dry, quiet, ct);
        Tally(r1, ref c, ref s);

        return (c, s);
    }
}