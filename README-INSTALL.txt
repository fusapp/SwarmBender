Azure DevOps Pipeline Wizard — Integration Guide
====================================================

1) Add YamlDotNet to SwarmBender.csproj
---------------------------------------
Copy the contents of "SwarmBender/SwarmBender.csproj.snippet.txt" into your project file.

2) Register the services
------------------------
DI:
    services.AddSingleton<SwarmBender.Services.Abstractions.IAzdoPipelineGenerator, SwarmBender.Services.Azdo.AzdoPipelineGenerator>();

CLI:
    See "SwarmBender.Cli/Commands/Utils/Azdo/UtilsCommandRegistrar.Snippet.cs" and add the command:
      sb utils azdo pipeline init

3) Run the wizard
-----------------
    sb utils azdo pipeline init
Answer prompts. Use --dry-run to preview without writing.

4) Generated pipeline
---------------------
File is written under: opt/pipelines/azdo/<stackId>-deploy.yml (unless you changed the path).

What the pipeline does:
- (optional) logs in to registry with REGISTRY_URL/USERNAME/PASSWORD variables
- computes ENV_LOWER from parameter "environmentName"
- optional "sb secrets sync -e $ENV_LOWER"
- "sb validate -e $ENV_LOWER --details"
- "sb render <stackId> -e $ENV_LOWER --out ops/state/last --write-history"
- "docker stack deploy --with-registry-auth --prune ..."

5) Customize
------------
- Environment resource naming: prefix/mapping/fixed strategies
- Variable groups: none/prefix/mapping/fixed
- Tags for ADO environment resource
- Toggle registry login and secrets sync steps

Enjoy!
