# SwarmBender

SwarmBender is a cross-platform .NET global tool (`sb`) that renders, validates and operates Docker Swarm stacks with composable overlays and provider-backed secrets.

## Quick install
```bash
dotnet tool install -g SwarmBender
# or update
dotnet tool update -g SwarmBender
```

## Quick start
```bash
# 1) init (root + optional stack)
sb init --env dev,prod
sb init sso --env dev,prod

# 2) validate
sb validate -e dev --details

# 3) render
sb render sso -e dev --out ops/state/last --preview
```

- Docs:
  - [Usage](usage.md)
  - [Secrets lifecycle](secrets.md)
  - [Infisical provider](infisical.md)
