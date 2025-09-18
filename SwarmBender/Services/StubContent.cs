
using SwarmBender.Services.Abstractions;

namespace SwarmBender.Services;

/// <summary>Provides initial stub content for scaffolding.</summary>
public sealed class StubContent : IStubContent
{
    public string GroupsYaml => """
# Map service -> group. Leave empty if not used.
# api: web
""";

    public string TenantsYaml => """
# Map service -> tenant. Leave empty if not used.
# api: default
""";

    public string EnvDefaultJson(string env) => $$"""
{
  "//": "Global defaults for all services in {{env}} environment.",
  "APP__Telemetry__Enabled": "true"
}
""";

    public string LabelsDefaultYaml(string env) => $"""
# Global baseline labels for {env} (Traefik or others).
# traefik.enable: "true"
""";

    public string DeployDefaultYaml(string env) => """
# Baseline deploy policy (override in services/<svc>/deploy/<env> as needed)
update_config:
  parallelism: 1
  delay: 5s
  order: start-first
restart_policy:
  condition: on-failure
""";

    public string LoggingDefaultYaml(string env) => """
# Baseline logging policy
driver: json-file
options:
  max-size: "10m"
  max-file: "3"
""";

    public string MountsDefaultYaml => """
# Baseline attachments (secrets/configs) for all services in this environment.
# secrets: []
# configs: []
""";

    public string OpsReadme => """
SwarmBender Ops Folder

- This folder contains machine-readable policies, checks, and CLI-generated state/reports.
- Do not commit `ops/state` or `ops/reports` to git.
- Use SwarmBender CLI subcommands (render/validate/diff/rotate/etc.); no shell scripts are included.
""";

    public string[] GitignoreLines => new[]
    {
        "ops/state/",
        "ops/reports/",
        "secrets/",
        "configs/files/",
        "**/*.secret",
        "**/*.secrets"
    };

    public string StackTemplateYaml => """
version: "3.8" # Swarm uses a Compose v3 subset
services:
  api:
    image: ghcr.io/your-org/api:latest
    # environment, labels, deploy, logging, secrets, configs will be composed by SwarmBender.
""";

    public string StackSecretsStub => """
# Top-level Swarm secrets definitions for this stack (optional).
# secrets:
#   db_password:
#     external: true
#   api_key:
#     file: ./secrets/api_key.txt
""";

    public string StackConfigsStub => """
# Top-level Swarm configs definitions for this stack (optional).
# configs:
#   nginx_conf:
#     file: ./configs/files/nginx.conf
""";

    public string AliasesStub => """
# Map template service names to canonical service names under /services.
# api-gateway: api
""";

    public string GuardrailsYaml => """
# Guardrails (policy examples):
require:
  healthcheck: true
  logging: true
suggest:
  deploy_order_start_first: true
  resources_limits: true
""";

    public string LabelsPolicyYaml => """
# Reserved/required label keys (example; extend as needed)
required: []
reserved: []
""";

    public string ImagesPolicyYaml => """
# Image policy (defaults: forbid latest, require tag or digest)
forbid_latest: true
require_tag_or_digest: true
allow: []  # optional regex list
allow_tagless: false
""";

    public string ComposeV3Yaml => """
# Compose v3 subset checks for Swarm stacks (example; extend in validate later)
allow:
  - version
  - services
forbid:
  - build
  - depends_on
""";

    public string RequiredKeysYaml => """
# Required env keys per service/group (example; consumed by validate later)
services: {}
groups: {}
""";

    public string GitHubActionsYaml => """
# Example only (placeholder for CI setup)
name: SwarmBender CI
on:
  push:
    tags: [ 'v*' ]
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'
      - run: dotnet build ./SwarmBender.Cli -c Release
""";

    public string AzurePipelinesYaml => """
# Example only (placeholder for Azure DevOps pipeline)
trigger:
- main
pool:
  vmImage: ubuntu-latest
steps:
- task: UseDotNet@2
  inputs:
    packageType: 'sdk'
    version: '9.x'
- script: dotnet build ./SwarmBender.Cli -c Release
""";
}
