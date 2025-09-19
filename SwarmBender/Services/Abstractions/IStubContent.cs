namespace SwarmBender.Services.Abstractions;

/// <summary>Provides initial stub content for scaffolding (overlay + legacy stubs).</summary>
public interface IStubContent
{
    // --- Metadata ---
    string GroupsYaml { get; }
    string TenantsYaml { get; }

    // --- Env defaults (JSON) ---
    string EnvDefaultJson(string env);

    // --- Legacy default stubs (kept for backward-compat) ---
    string LabelsDefaultYaml(string env);
    string DeployDefaultYaml(string env);
    string LoggingDefaultYaml(string env);
    string MountsDefaultYaml { get; }

    // --- Repo housekeeping ---
    string OpsReadme { get; }
    string[] GitignoreLines { get; }

    // --- Stack template + optional top-level defs ---
    string StackTemplateYaml { get; }
    string StackSecretsStub { get; }
    string StackConfigsStub { get; }
    string AliasesStub { get; }

    // --- Policies / checks ---
    string GuardrailsYaml { get; }
    string LabelsPolicyYaml { get; }
    string ImagesPolicyYaml { get; }
    string ComposeV3Yaml { get; }
    string RequiredKeysYaml { get; }
    string GitHubActionsYaml { get; }
    string AzurePipelinesYaml { get; }

    // --- Overlay-based new stubs ---
    /// <summary>Allowlist for process environment variables (JSON array/object). </summary>
    string UseEnvVarsDefaultJson { get; }

    /// <summary>Secrets provider configuration (e.g., docker-cli).</summary>
    string SecretsProviderYaml { get; }

    /// <summary>Empty secret map skeleton for a given environment.</summary>
    string SecretsMapYaml(string env);

    /// <summary>Global overlay placed at stacks/all/&lt;env&gt;/stack/global.yml</summary>
    string GlobalStackOverlayYaml(string env);

    /// <summary>Stack-scoped overlay placed at stacks/&lt;stackId&gt;/&lt;env&gt;/stack/00-stack.yml</summary>
    string StackEnvOverlayYaml(string stackId, string env);
}