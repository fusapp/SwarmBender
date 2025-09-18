using Spectre.Console;
using SwarmBender.Services.Abstractions;
using SwarmBender.Services.Models;

namespace SwarmBender.Services;

/// <summary>Executes init scaffolding flow.</summary>
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
                (created, skipped) = await StackScaffoldAsync(root, r.StackId!, validEnvs, r.NoGlobalDefs, r.NoDefs, r.NoAliases, r.DryRun, r.Quiet, ct);
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

    private async Task<(int created, int skipped)> RootScaffoldAsync(string root, List<string> envs, bool noGlobalDefs, bool dry, bool quiet, CancellationToken ct)
    {
        var (c, s) = (0, 0);

        var r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "stacks"), dry, quiet, ct); Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "stacks", "all"), dry, quiet, ct); Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "services"), dry, quiet, ct); Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "metadata"), dry, quiet, ct); Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "ops"), dry, quiet, ct); Tally(r1, ref c, ref s);

        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "metadata", "groups.yml"), _stub.GroupsYaml, dry, quiet, ct); Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "metadata", "tenants.yml"), _stub.TenantsYaml, dry, quiet, ct); Tally(r1, ref c, ref s);

        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "ops", "policies"), dry, quiet, ct); Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "ops", "policies", "guardrails.yml"), _stub.GuardrailsYaml, dry, quiet, ct); Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "ops", "policies", "labels.yml"), _stub.LabelsPolicyYaml, dry, quiet, ct); Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "ops", "policies", "images.yml"), _stub.ImagesPolicyYaml, dry, quiet, ct); Tally(r1, ref c, ref s);

        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "ops", "checks"), dry, quiet, ct); Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "ops", "checks", "compose-v3.yml"), _stub.ComposeV3Yaml, dry, quiet, ct); Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "ops", "checks", "required-keys.yml"), _stub.RequiredKeysYaml, dry, quiet, ct); Tally(r1, ref c, ref s);

        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "ops", "state", "last"), dry, quiet, ct); Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "ops", "state", "history"), dry, quiet, ct); Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "ops", "reports", "preflight"), dry, quiet, ct); Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "ops", "reports", "diffs"), dry, quiet, ct); Tally(r1, ref c, ref s);

        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "ops", "ci-templates"), dry, quiet, ct); Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "ops", "ci-templates", "github-actions.yml"), _stub.GitHubActionsYaml, dry, quiet, ct); Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "ops", "ci-templates", "azure-pipelines.yml"), _stub.AzurePipelinesYaml, dry, quiet, ct); Tally(r1, ref c, ref s);

        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "ops", "vars"), dry, quiet, ct); Tally(r1, ref c, ref s);

        if (!noGlobalDefs)
        {
            foreach (var env in envs)
            {
                var basePath = Path.Combine(root, "stacks", "all", env);
                r1 = await _fs.EnsureDirectoryAsync(Path.Combine(basePath, "env"), dry, quiet, ct); Tally(r1, ref c, ref s);
                r1 = await _fs.EnsureDirectoryAsync(Path.Combine(basePath, "labels"), dry, quiet, ct); Tally(r1, ref c, ref s);
                r1 = await _fs.EnsureDirectoryAsync(Path.Combine(basePath, "deploy"), dry, quiet, ct); Tally(r1, ref c, ref s);
                r1 = await _fs.EnsureDirectoryAsync(Path.Combine(basePath, "logging"), dry, quiet, ct); Tally(r1, ref c, ref s);
                r1 = await _fs.EnsureDirectoryAsync(Path.Combine(basePath, "mounts"), dry, quiet, ct); Tally(r1, ref c, ref s);

                r1 = await _fs.EnsureFileAsync(Path.Combine(basePath, "env", "default.json"), _stub.EnvDefaultJson(env), dry, quiet, ct); Tally(r1, ref c, ref s);
                r1 = await _fs.EnsureFileAsync(Path.Combine(basePath, "labels", "default.yml"), _stub.LabelsDefaultYaml(env), dry, quiet, ct); Tally(r1, ref c, ref s);
                r1 = await _fs.EnsureFileAsync(Path.Combine(basePath, "deploy", "default.yml"), _stub.DeployDefaultYaml(env), dry, quiet, ct); Tally(r1, ref c, ref s);
                r1 = await _fs.EnsureFileAsync(Path.Combine(basePath, "logging", "default.yml"), _stub.LoggingDefaultYaml(env), dry, quiet, ct); Tally(r1, ref c, ref s);
                r1 = await _fs.EnsureFileAsync(Path.Combine(basePath, "mounts", "default.yml"), _stub.MountsDefaultYaml, dry, quiet, ct); Tally(r1, ref c, ref s);
            }
        }

        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "ops", "README.md"), _stub.OpsReadme, dry, quiet, ct); Tally(r1, ref c, ref s);
        r1 = await _fs.AppendGitignoreAsync(root, _stub.GitignoreLines, dry, quiet, ct); Tally(r1, ref c, ref s);

        return (c, s);
    }

    private async Task<(int created, int skipped)> StackScaffoldAsync(
        string root, string stackId, List<string> envs, bool noGlobalDefs, bool noDefs, bool noAliases, bool dry, bool quiet, CancellationToken ct)
    {
        var (c, s) = (0, 0);
        var stackRoot = Path.Combine(root, "stacks", stackId);

        var r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "stacks"), dry, quiet, ct); Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureDirectoryAsync(stackRoot, dry, quiet, ct); Tally(r1, ref c, ref s);

        r1 = await _fs.EnsureFileAsync(Path.Combine(stackRoot, "docker-stack.template.yml"), _stub.StackTemplateYaml, dry, quiet, ct); Tally(r1, ref c, ref s);

        if (!noDefs)
        {
            r1 = await _fs.EnsureFileAsync(Path.Combine(stackRoot, "secrets.yml"), _stub.StackSecretsStub, dry, quiet, ct); Tally(r1, ref c, ref s);
            r1 = await _fs.EnsureFileAsync(Path.Combine(stackRoot, "configs.yml"), _stub.StackConfigsStub, dry, quiet, ct); Tally(r1, ref c, ref s);
        }

        if (!noAliases)
        {
            r1 = await _fs.EnsureFileAsync(Path.Combine(stackRoot, "aliases.yml"), _stub.AliasesStub, dry, quiet, ct); Tally(r1, ref c, ref s);
        }

        if (!noGlobalDefs && envs.Count > 0)
        {
            foreach (var env in envs)
            {
                var basePath = Path.Combine(root, "stacks", "all", env);
                r1 = await _fs.EnsureDirectoryAsync(Path.Combine(basePath, "env"), dry, quiet, ct); Tally(r1, ref c, ref s);
                r1 = await _fs.EnsureDirectoryAsync(Path.Combine(basePath, "labels"), dry, quiet, ct); Tally(r1, ref c, ref s);
                r1 = await _fs.EnsureDirectoryAsync(Path.Combine(basePath, "deploy"), dry, quiet, ct); Tally(r1, ref c, ref s);
                r1 = await _fs.EnsureDirectoryAsync(Path.Combine(basePath, "logging"), dry, quiet, ct); Tally(r1, ref c, ref s);
                r1 = await _fs.EnsureDirectoryAsync(Path.Combine(basePath, "mounts"), dry, quiet, ct); Tally(r1, ref c, ref s);

                r1 = await _fs.EnsureFileAsync(Path.Combine(basePath, "env", "default.json"), _stub.EnvDefaultJson(env), dry, quiet, ct); Tally(r1, ref c, ref s);
                r1 = await _fs.EnsureFileAsync(Path.Combine(basePath, "labels", "default.yml"), _stub.LabelsDefaultYaml(env), dry, quiet, ct); Tally(r1, ref c, ref s);
                r1 = await _fs.EnsureFileAsync(Path.Combine(basePath, "deploy", "default.yml"), _stub.DeployDefaultYaml(env), dry, quiet, ct); Tally(r1, ref c, ref s);
                r1 = await _fs.EnsureFileAsync(Path.Combine(basePath, "logging", "default.yml"), _stub.LoggingDefaultYaml(env), dry, quiet, ct); Tally(r1, ref c, ref s);
                r1 = await _fs.EnsureFileAsync(Path.Combine(basePath, "mounts", "default.yml"), _stub.MountsDefaultYaml, dry, quiet, ct); Tally(r1, ref c, ref s);
            }
        }

        r1 = await _fs.EnsureDirectoryAsync(Path.Combine(root, "ops"), dry, quiet, ct); Tally(r1, ref c, ref s);
        r1 = await _fs.EnsureFileAsync(Path.Combine(root, "ops", "README.md"), _stub.OpsReadme, dry, quiet, ct); Tally(r1, ref c, ref s);
        r1 = await _fs.AppendGitignoreAsync(root, _stub.GitignoreLines, dry, quiet, ct); Tally(r1, ref c, ref s);

        return (c, s);
    }
}
