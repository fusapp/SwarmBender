using System.Collections.Generic;

namespace SwarmBender.Services.Models;

/// <summary>How ADO Environment name will be produced at runtime.</summary>
public enum EnvNameStrategy { Prefix, Fixed }

/// <summary>How variable groups are injected.</summary>
public enum VarGroupsMode { None, FixedList, Prefix }

/// <summary>High-level request coming from CLI to render a single pipeline YAML.</summary>
public sealed record AzdoPipelineRequest(
    string RootPath,
    string StackId,
    string OutDir,                        // e.g. "opt/pipelines/azdo"
    IReadOnlyList<string> Environments,   // e.g. ["dev","prod"] (parameters list)
    string DefaultEnv,                    // e.g. "dev"
    EnvNameStrategy EnvStrategy,
    string EnvName,                       // "INFRA" when Prefix; "ProdInfra" when Fixed
    VarGroupsMode VarGroupsMode,
    string? VarGroupsFixedCsv,            // "COMMON,FOO_BAR" when FixedList
    string? VarGroupsPrefix,              // "APP" -> expands to "APP_{ENV}"
    bool IncludeSecretsSync,              // include sb secrets doctor/sync
    string AppSettingsMode,               // "env" or "config"
    string RenderOutDir,                  // e.g. "ops/state/last"
    bool WriteHistory,                    // render --write-history
    bool IncludeRegistryLogin,            // docker login step
    string? RegistryServerVar,            // REGISTRY_SERVER
    string? RegistryUserVar,              // REGISTRY_USERNAME
    string? RegistryPassVar              // REGISTRY_PASSWORD
);