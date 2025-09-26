using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SwarmBender.Cli.Commands;

/// <summary>Scaffolds repo layout and (optionally) a specific stack with presets.</summary>
public sealed class InitCommand : Command<InitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Optional stack id to scaffold (e.g., 'sso'). If omitted, only repo layout is created.")]
        [CommandArgument(0, "[STACK_ID]")]
        public string? StackId { get; init; }

        [Description("Root path (defaults to cwd).")]
        [CommandOption("--root <PATH>")]
        public string Root { get; init; } = Directory.GetCurrentDirectory();

        [Description("Environments CSV used for overlays (default: dev,prod)")]
        [CommandOption("-e|--env <CSV>")]
        public string Envs { get; init; } = "dev,prod";
    }

    public override int Execute(CommandContext context, Settings s)
    {
        var root = s.Root;
        var envs = s.Envs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        ScaffoldRepoBase(root, envs);

        if (!string.IsNullOrWhiteSpace(s.StackId))
        {
            var id = s.StackId!;
            var preset = DetectPreset(id);
            ScaffoldStack(root, id, envs, preset);
            AnsiConsole.MarkupLine($"[green]Stack scaffolded:[/] [bold]{id}[/] (preset: {preset})");
        }

        // .gitignore
        WriteIfMissing(Path.Combine(root, ".gitignore"), GitignoreTemplate());

        // ops/sb.yml (comment-rich template)
        WriteIfMissing(Path.Combine(root, "ops", "sb.yml"), SbYamlTemplate());

        AnsiConsole.MarkupLine("[green]Scaffold completed.[/]");
        AnsiConsole.MarkupLine($"[grey]Root: {root}[/]");
        return 0;
    }

    // ---------------- base scaffold ----------------

    private static void ScaffoldRepoBase(string root, string[] envs)
    {
        var stacksAll = Path.Combine(root, "stacks", "all");
        var opsState  = Path.Combine(root, "ops", "state");
        var opsVars   = Path.Combine(root, "ops", "vars", "private");

        EnsureDirWithKeep(stacksAll);
        EnsureDirWithKeep(Path.Combine(opsState, "last"));
        EnsureDirWithKeep(Path.Combine(opsState, "history"));
        EnsureDirWithKeep(opsVars);

        foreach (var env in envs)
        {
            var envRoot  = Path.Combine(stacksAll, env);
            var envJson  = Path.Combine(envRoot, "env");
            var stackDir = Path.Combine(envRoot, "stack");

            EnsureDirWithKeep(envRoot);
            EnsureDirWithKeep(envJson);
            EnsureDirWithKeep(stackDir);

            WriteIfMissing(Path.Combine(stackDir, "global.yml"),
                """
                # Global overlays for this environment (applied to all stacks).
                # Files in this folder are applied alphabetically; last wins.
                services:
                  "*":
                    logging:
                      driver: json-file
                """);

            WriteIfMissing(Path.Combine(envJson, "appsettings.json"),
                """
                {
                  // Flattened into env bag (both dot and __ forms).
                  "FeatureX": { "Enabled": true },
                  "Redis__Host": "redis"
                }
                """);
        }
    }

    // ---------------- stack scaffold ----------------

    private static void ScaffoldStack(string root, string stackId, string[] envs, string preset)
    {
        var stackRoot = Path.Combine(root, "stacks", stackId);
        EnsureDirWithKeep(stackRoot);

        if (preset.Equals("sso", StringComparison.OrdinalIgnoreCase))
            WriteSsoTemplate(stackRoot, stackId);
        else
            WriteMinimalTemplate(stackRoot, stackId);

        // per-env folders for this stack
        foreach (var env in envs)
        {
            var envRoot  = Path.Combine(stackRoot, env);
            var envJson  = Path.Combine(envRoot, "env");
            var stackDir = Path.Combine(envRoot, "stack");

            EnsureDirWithKeep(envRoot);
            EnsureDirWithKeep(envJson);
            EnsureDirWithKeep(stackDir);

            // example overlay
            WriteIfMissing(Path.Combine(stackDir, "svc.yml"),
                $"""
                # Example overlay for {stackId}/{env}
                services:
                  api:
                    logging:
                      driver: json-file
                    labels:
                      com.example.env: "{env}"
                """);

            // example env json (stack-local)
            WriteIfMissing(Path.Combine(envJson, "appsettings.json"),
                """
                {
                  "ConnectionStrings": {
                    "Main": "Server=db;User=sa;Pwd=secret"
                  },
                  "Tracing__Enabled": true
                }
                """);
        }

        // allowlist example for OS env import
        WriteIfMissing(Path.Combine(stackRoot, "use-envvars.json"),
            """
            [
              "CONNECTIONSTRINGS__*",
              "REDIS_*",
              "MONGO_*"
            ]
            """);
    }

    // ---------------- presets ----------------

    private static string DetectPreset(string stackId)
        => stackId.Equals("sso", StringComparison.OrdinalIgnoreCase) ? "sso" : "minimal";

    private static void WriteMinimalTemplate(string stackRoot, string stackId)
    {
        WriteIfMissing(Path.Combine(stackRoot, "docker-stack.template.yml"),
            """
            # Compose v3 (swarm) template for stack: ${STACK_ID}
            services:
              api:
                image: fusapp/api:1
                command: ["sh","-c","while true; do echo hello; sleep 30; done"]
                deploy:
                  labels:
                    - owner=${COMPANY_NAME}
                    - stack=${STACK_ID}
                environment:
                  - ASPNETCORE_URLS=http://+:8080
                ports:
                  - "8080:8080"
            """.Replace("${STACK_ID}", stackId, StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteSsoTemplate(string stackRoot, string stackId)
    {
        // multi-service example: web (reverse proxy), sso-api, redis
        WriteIfMissing(Path.Combine(stackRoot, "docker-stack.template.yml"),
            """
            # SSO preset template for stack: ${STACK_ID}
            services:
              web:
                image: traefik:v2.11
                command:
                  - "--providers.docker.swarmMode=true"
                  - "--entrypoints.web.address=:80"
                ports:
                  - "80:80"
                deploy:
                  labels:
                    - traefik.enable=true
                networks: { __list__: [ "edge" ] }

              sso-api:
                image: ghcr.io/example/sso-api:1
                environment:
                  - ASPNETCORE_URLS=http://+:5000
                deploy:
                  labels:
                    - traefik.enable=true
                    - traefik.http.routers.sso.rule=Host(`sso.local`)
                    - traefik.http.services.sso.loadbalancer.server.port=5000
                networks: { __list__: [ "edge" ] }

              redis:
                image: redis:7-alpine
                command: ["redis-server","--appendonly","yes"]

            networks:
              edge: { external: { __bool__: true, name: "edge" } }
            """.Replace("${STACK_ID}", stackId, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------- helpers & templates ----------------

    private static void EnsureDirWithKeep(string path)
    {
        Directory.CreateDirectory(path);
        var keep = Path.Combine(path, ".gitkeep");
        if (!File.Exists(keep)) File.WriteAllText(keep, "");
    }

    private static void WriteIfMissing(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (!File.Exists(path))
            File.WriteAllText(path, contents);
    }

    private static string GitignoreTemplate() =>
        """
        # --- SwarmBender scaffold ---
        /ops/state/last/
        /ops/state/history/
        /ops/vars/private/

        # build/test artifacts
        bin/
        obj/
        TestResults/
        **/playwright-report/
        **/playwright/.cache/

        # OS/editor junk
        .DS_Store
        Thumbs.db
        .idea/
        .vscode/
        """;

    private static string SbYamlTemplate() =>
        """
        # SwarmBender config (ops/sb.yml)
        version: 1

        render:
          appsettingsMode: env
          outDir: ops/state/last
          writeHistory: true
          overlayOrder:
            - stacks/all/{env}/stack/*.y?(a)ml
            - stacks/{stackId}/{env}/stack/*.y?(a)ml

        tokens:
          user:
            COMPANY_NAME: fusapp
            ENVIRONMENT_RESOURCENAME: contabo

        secretize:
          enabled: true
          paths:
            - ConnectionStrings.*
            - ConnectionStrings__*
            - Redis__*

        secrets:
          engine:
            type: docker-cli
            args:
              dockerPath: docker
              dockerHost: unix:///var/run/docker.sock
          nameTemplate: "sb_{scope}_{env}_{key}_{version}"
          versionMode: content-sha
          labels:
            owner: swarmbender

        providers:
          order:
            - type: file
            - type: env
            # - type: azure-kv
            # - type: infisical

          file:
            extraJsonDirs: []

          env:
            allowlistFileSearch:
              - stacks/{stackId}/use-envvars.json
              - stacks/all/use-envvars.json

          # azure-kv:
          #   enabled: true
          #   vaultUrl: "https://YOUR-VAULT-NAME.vault.azure.net/"
          #   keyTemplate: "{key}"
          #   replace:
          #     "__": "--"

          # infisical:
          #   enabled: true
          #   baseUrl: "https://app.infisical.com"
          #   workspaceId: ""
          #   envMap:
          #     dev: "dev"
          #     prod: "prod"
          #   pathTemplate: "/{scope}"
          #   keyTemplate: "{key}"
          #   replace:
          #     "__": "_"
          #   include:
          #     - "ConnectionStrings__*"
          #     - "Redis__*"
          #     - "Mongo__*"

        metadata:
          groups:
            - id: web
              description: Web & Edge workloads
            - id: data
              description: Data services
            - id: background
              description: Workers / schedulers
          tenants: []

        schema:
          required:
            - render.outDir
            - providers.order
          enums:
            render.appsettingsMode: [ env, config ]
        """;
}