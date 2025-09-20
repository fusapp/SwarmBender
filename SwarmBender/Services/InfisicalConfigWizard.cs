using System.Text;
using Spectre.Console;
using SwarmBender.Services.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SwarmBender.Services;

/// <summary>
/// Builds ops/providers/infisical.yml via an interactive Spectre wizard.
/// </summary>
public sealed class InfisicalConfigWizard : IInfisicalConfigWizard
{
    private static readonly ISerializer YamlWriter = new SerializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public async Task<string> RunAsync(string rootPath, string? configPath = null, bool forceOverwrite = false, CancellationToken ct = default)
    {
        var path = configPath;
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(rootPath, "ops", "vars", "providers", "infisical.yml");

        var dir = Path.GetDirectoryName(path!)!;
        Directory.CreateDirectory(dir);

        // Ask base params
        AnsiConsole.MarkupLine("[bold]Infisical config wizard[/] (press Enter to accept defaults)");

        var baseUrl        = AnsiConsole.Ask<string>("Base URL? [grey](default: https://app.infisical.com)[/]", "https://app.infisical.com");
        var uploadEndpoint = AnsiConsole.Ask<string>("Upload endpoint? [grey](default: api/v4/secrets/batch)[/]", "api/v4/secrets/batch");
        var downloadEndpoint = AnsiConsole.Ask<string>("Download endpoint? [grey](default: api/v3/secrets/raw)[/]", "api/v3/secrets/raw");
        var foldersEndpoint  = AnsiConsole.Ask<string>("Folders endpoint? [grey](default: api/v3/folders)[/]", "api/v3/folders");

        // Project/Workspace identification (we support either id or slug)
        var idOrSlugChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Identify workspace by?")
                .AddChoices("workspaceId (GUID/ID)", "workspaceSlug (string)"));

        string? workspaceId = null;
        string? workspaceSlug = null;
        if (idOrSlugChoice.StartsWith("workspaceId", StringComparison.OrdinalIgnoreCase))
            workspaceId = AnsiConsole.Ask<string>("Workspace ID? (e.g. bf6f4...)", "");
        else
            workspaceSlug = AnsiConsole.Ask<string>("Workspace slug? (e.g. polarbear-qiqa)", "");

        var pathTemplate  = AnsiConsole.Ask<string>("Secret path template? [grey](default: /{scope})[/]", "/{scope}");
        var tokenEnvVar   = AnsiConsole.Ask<string>("Token env var? [grey](default: INFISICAL_TOKEN)[/]", "INFISICAL_TOKEN");
        var autoCreate    = AnsiConsole.Confirm("Auto-create secret path on upload? [grey](default: yes)[/]", true);

        // Include patterns (apply to flattened keys)
        AnsiConsole.MarkupLine("[grey]Include patterns apply to flattened keys (use double underscore __).[/]");
        var include = new List<string>();
        include.Add(AnsiConsole.Ask<string>("Include #1 [grey](default: ConnectionStrings__*)[/]", "ConnectionStrings__*"));
        include.Add(AnsiConsole.Ask<string>("Include #2 [grey](default: Redis__*)[/]",          "Redis__*"));
        include.Add(AnsiConsole.Ask<string>("Include #3 [grey](default: Mongo__*)[/]",          "Mongo__*"));

        // Replace map
        var doReplace = AnsiConsole.Confirm("Apply key replacements? (e.g., __ -> _)", true);
        var replace = new Dictionary<string, string>();
        if (doReplace)
        {
            replace["__"] = "_";
            while (AnsiConsole.Confirm("Add another replacement mapping?", false))
            {
                var from = AnsiConsole.Ask<string>("Replace from:");
                var to   = AnsiConsole.Ask<string>("Replace to:");
                if (!string.IsNullOrWhiteSpace(from))
                    replace[from] = to ?? "";
            }
        }

        var stripPrefix = AnsiConsole.Ask<string>("Strip prefix from flattened key? [grey](default: empty)[/]", "");
        var keyTemplate = AnsiConsole.Ask<string>("Key template for Infisical? [grey](default: {key})[/]", "{key}");

        // Env map
        var envMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["prod"] = "prod",
            ["dev"] = "dev"
        };
        if (AnsiConsole.Confirm("Customize environment mapping (swarm env -> infisical env)?", false))
        {
            var more = true;
            envMap.Clear();
            while (more)
            {
                var from = AnsiConsole.Ask<string>("Swarm env name (e.g. dev, prod):");
                var to   = AnsiConsole.Ask<string>("Infisical env slug for that (e.g. dev, prod):");
                if (!string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to))
                    envMap[from] = to;
                more = AnsiConsole.Confirm("Add another env map?", false);
            }
            if (envMap.Count == 0)
            {
                envMap["prod"] = "prod";
                envMap["dev"]  = "dev";
            }
        }

        // Optional direct map
        var directMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (AnsiConsole.Confirm("Add direct key mapping (flat key -> Infisical key)?", false))
        {
            var more = true;
            while (more)
            {
                var flat = AnsiConsole.Ask<string>("Flattened key (e.g. ConnectionStrings__MSSQL_Master):");
                var inf  = AnsiConsole.Ask<string>("Infisical key (e.g. MSSQL_Master):");
                if (!string.IsNullOrWhiteSpace(flat) && !string.IsNullOrWhiteSpace(inf))
                    directMap[flat] = inf;
                more = AnsiConsole.Confirm("Add another direct map?", false);
            }
        }

        // Build YAML object
        var yamlObj = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["baseUrl"]          = baseUrl,
            ["downloadEndpoint"] = downloadEndpoint,
            ["uploadEndpoint"]   = uploadEndpoint,
            ["foldersEndpoint"]  = foldersEndpoint,
            ["workspaceId"]      = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId,
            ["workspaceSlug"]    = string.IsNullOrWhiteSpace(workspaceSlug) ? null : workspaceSlug,
            ["path"]             = pathTemplate,
            ["tokenEnvVar"]      = tokenEnvVar,
            ["autoCreatePathOnUpload"] = autoCreate,
            ["include"]          = include,
            ["stripPrefix"]      = stripPrefix,
            ["replace"]          = replace.Count == 0 ? null : replace,
            ["keyTemplate"]      = keyTemplate,
            ["map"]              = directMap.Count == 0 ? new Dictionary<string, string>() : directMap,
            ["envMap"]           = envMap
        };

        // Clean nulls
        var cleaned = yamlObj
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        // Overwrite check
        if (File.Exists(path!) && !forceOverwrite)
        {
            var overwrite = AnsiConsole.Confirm($"[yellow]{path}[/] already exists. Overwrite?", false);
            if (!overwrite)
                throw new InvalidOperationException("Operation cancelled by user.");
        }

        var yaml = YamlWriter.Serialize(cleaned);
        await File.WriteAllTextAsync(path!, yaml, new UTF8Encoding(false), ct);

        AnsiConsole.MarkupLine($"[green]Config written:[/] {path}");
        return Path.GetFullPath(path!);
    }
}