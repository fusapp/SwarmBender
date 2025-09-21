---
title: providers
---

# Providers

SwarmBender can source secret **values** from multiple providers before syncing to Swarm secrets.

## Built-ins
- **env** — read from process environment
- **file** — read from `ops/private/<env>.json` (flattened)

Configure under `ops/vars/providers/providers.yml`:
```yml
providers:
  - type: env
  - type: file
```

## Infisical
Two parts:

1) **Source provider** — fetch values from Infisical and populate the map:
```yml
# ops/vars/providers/providers.yml
providers:
  - type: infisical
```

2) **Client config** — API endpoints and mappings:
```yml
# ops/vars/providers/infisical.yml
baseUrl: "https://your-infisical.example"
downloadEndpoint: "api/v3/secrets/raw"
uploadEndpoint: "api/v4/secrets/batch"
foldersEndpoint: "api/v3/folders"
workspaceId: "<GUID>"
path: "/{scope}"
tokenEnvVar: "INFISICAL_TOKEN"
include:
  - "ConnectionStrings__*"
replace:
  "__": "_"
keyTemplate: "{key}"
envMap:
  prod: "prod"
  dev: "dev"
```
