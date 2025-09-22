using SwarmBender.Services.Abstractions;

namespace SwarmBender.Services;

/// <summary>Provides initial stub content for scaffolding (overlay-based + legacy).</summary>
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

    // -------- Legacy defaults (kept so old validators/commands compile & run) --------
    public string LabelsDefaultYaml(string env) => $"""
# Global baseline labels for {env} (legacy stub; overlays are preferred)
# traefik.enable: "true"
""";

    public string DeployDefaultYaml(string env) => """
# Baseline deploy policy (legacy stub; overlays are preferred)
update_config:
  parallelism: 1
  delay: 5s
  order: start-first
restart_policy:
  condition: on-failure
""";

    public string LoggingDefaultYaml(string env) => """
# Baseline logging policy (legacy stub; overlays are preferred)
driver: json-file
options:
  max-size: "10m"
  max-file: "3"
""";

    public string MountsDefaultYaml => """
# Baseline attachments (legacy stub; overlays are preferred)
# secrets: []
# configs: []
""";

    // -------- Repo housekeeping --------
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
        "**/*.secrets",
        "ops/private/",
        "ops/private/**"
    };

    // -------- Stack template + optional top-level defs --------
    public string StackTemplateYaml => """
version: "3.8" # Swarm uses a Compose v3 subset
services:
  api:
    image: ghcr.io/your-org/api:1.0.0
    # environment, labels, deploy, logging, secrets, configs will be composed by SwarmBender.
""";

    public string StackSecretsStub => """
# Optional top-level Swarm secrets for this stack (empty map is OK).
# secrets:
#   db_password:
#     external: true
""";

    public string StackConfigsStub => """
# Optional top-level Swarm configs for this stack (empty map is OK).
# configs:
#   nginx_conf:
#     file: ./configs/files/nginx.conf
""";

    public string AliasesStub => """
# Map template service names to canonical service names under /services.
# api-gateway: api
""";

    // -------- Policies / checks --------
    public string GuardrailsYaml => """
# Guardrails (policy examples):
require:
  healthcheck: true
  logging: false
  resources_limits: false
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
# Compose v3 subset checks for Swarm stacks
allow:
  - version
  - services
  - networks
  - volumes
  - secrets
  - configs
  - x-*
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

    // -------- Overlay-based new stubs --------
    public string UseEnvVarsDefaultJson => "[]\n";

    public string SecretsProviderYaml => """
secrets:
  provider: docker-cli
  args:
    dockerPath: docker
    dockerHost: unix:///var/run/docker.sock

secretize:
  enabled: true
  paths:
    - "ConnectionStrings__*"
    - "Redis__*"
    - "Mongo__*"

name_template: "sb_{scope}_{env}_{key}_{version}"
""";

    public string SecretsMapYaml(string env) => $$"""
# Secret map for environment '{{env}}'
# KEY (flattened) -> engine secret name
# ConnectionStrings__MSSQL_Master: sb_global_{{env}}_ConnectionStrings__MSSQL_Master_abcdef
""";

    public string GlobalStackOverlayYaml(string env) => $$"""
# Global overlays for environment '{{env}}' (merged first)
services:
  "*":
    # Example: secrets requested by all services (resolved via ops/vars/secrets-map.{{env}}.yml)
    # x-sb-secrets:
    #   - key: ConnectionStrings__MSSQL_Master
    #   - key: Redis__Master
    #     target: /run/secrets/redis-master
    #     mode: 0444
    #
    # You may also put global healthcheck/logging/deploy fragments here:
    # healthcheck:
    #   test: [ "CMD", "curl", "-f", "http://localhost:4509/healthz" ]
    #   interval: 30s
    #   timeout: 5s
    #   retries: 2
    # logging:
    #   driver: "json-file"
    #   options:
    #     max-size: "10m"
    #     max-file: "3"
    # deploy:
    #   update_config: { parallelism: 1, delay: 5s, order: start-first }
    #   restart_policy: { condition: on-failure }
""";

    public string StackEnvOverlayYaml(string stackId, string env) => $$"""
# Stack '{{stackId}}' overlays for environment '{{env}}'
# Place stack-scoped fragments (root keys or per-service) here.
# Example:
# services:
#   login:
#     x-sb-secrets:
#       - key: Redis__Master
#         target: /run/secrets/redis-master
#         mode: 0444
""";
}