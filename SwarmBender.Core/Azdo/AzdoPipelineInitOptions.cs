namespace SwarmBender.Core.Azdo;


/// <summary>
/// High-level, declarative pipeline scaffold options.
/// Wizard will decorate/fill this and pass to the generator.
/// </summary>
public sealed class AzdoPipelineInitOptions
{
    // Generic
    public string DotnetSdkVersion { get; set; } = "9.0.x";
    public string? RegistryVariableGroup { get; set; } // optional registry VG

    // Trigger config
    public TriggerOptions Trigger { get; set; } = new();

    // Variable groups
    public List<VariableGroupSpec> VariableGroups { get; set; } = new();

    // Companies (pipeline parameter list)
    public List<string> Companies { get; set; } = new() { "fusapp" };

    // Additional pipeline parameters (will be exported as env before render)
    public List<PipelineParameterSpec> ExtraParameters { get; set; } = new();

    // Environment resource config
    public string EnvironmentNamePrefix { get; set; } = "";  // e.g. "POLARBEAR_"
    public List<string> EnvironmentTags { get; set; } = new() { "APP" }; // default tag set

    // Render/deploy
    public string RenderOutDir { get; set; } = "ops/state/last"; // auto used
    public bool WriteHistory { get; set; } = true;
    public bool Force { get; set; } = false;
    public string AppsettingsMode { get; set; } = "env"; // env|config
}

/// <summary>Azure DevOps trigger configuration.</summary>
public sealed class TriggerOptions
{
    public TriggerMode Mode { get; set; } = TriggerMode.None;  // None | CI | ManualOnly
    public List<string> CiIncludeBranches { get; set; } = new() { "main" };
    public List<string> CiExcludeBranches { get; set; } = new();
    public bool PrEnabled { get; set; } = false;
}

public enum TriggerMode { None, CI, ManualOnly }

/// <summary>Declares a variable group and how variants are applied.</summary>
public sealed class VariableGroupSpec
{
    public string Name { get; set; } = default!;  // base VG, e.g. "POLARBEAR"
    public bool VariantByEnvironment { get; set; } = true; // => <NAME>_{ENV}
    public bool VariantByTenant { get; set; } = false;     // => <NAME>_{TENANT}
    public bool VariantByCompany { get; set; } = false;    // => <NAME>_{COMPANY}
}

/// <summary>Declarative pipeline parameter which can be exported as env.</summary>
public sealed class PipelineParameterSpec
{
    // name must be ADO-safe (letters, numbers, underscore)
    public string Name { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string Type { get; set; } = "string"; // string|boolean
    public string? Default { get; set; }
    public List<string> Values { get; set; } = new(); // optional enum values
    public bool ExportAsEnv { get; set; } = true; // export to env before render
    public string? EnvVarName { get; set; } // if null => upper(Name)
}