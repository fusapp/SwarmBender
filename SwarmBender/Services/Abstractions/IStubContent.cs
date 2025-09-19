namespace SwarmBender.Services.Abstractions;

/// <summary>
/// Provides initial stub content for scaffolding (templates, policies, etc.).
/// </summary>
public interface IStubContent
{
    string GroupsYaml { get; }
    string TenantsYaml { get; }

    string EnvDefaultJson(string env);
    string LabelsDefaultYaml(string env);
    string DeployDefaultYaml(string env);
    string LoggingDefaultYaml(string env);
    string MountsDefaultYaml { get; }

    string OpsReadme { get; }
    string[] GitignoreLines { get; }

    string StackTemplateYaml { get; }
    string StackSecretsStub { get; }
    string StackConfigsStub { get; }
    string AliasesStub { get; }

    string GuardrailsYaml { get; }
    string LabelsPolicyYaml { get; }
    string ImagesPolicyYaml { get; }

    string ComposeV3Yaml { get; }
    string RequiredKeysYaml { get; }
    string GitHubActionsYaml { get; }
    string AzurePipelinesYaml { get; }
    
    string UseEnvVarsDefaultJson { get; }
}
