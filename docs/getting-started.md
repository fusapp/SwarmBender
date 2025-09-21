---
title: Getting Started
---

# Getting Started

## Prerequisites
- .NET 9 SDK
- Docker (Swarm mode for deploy targets)
- Git

## Install
```bash
dotnet tool install --global SwarmBender.Cli --version <VERSION>
sb -h
```

## Initialize a repo
```bash
# create root scaffolding
sb init --env dev,prod

# add a stack
sb init sso --env dev,prod
```

This creates the expected tree:

```
stacks/
  all/
    dev/
      env/
      stack/
    prod/
      env/
      stack/
  sso/
    docker-stack.template.yml
    aliases.yml
services/
metadata/
ops/
```

Next steps:
- Put base overlays under `stacks/all/<env>/stack/*.yml`
- Put per-stack overlays under `stacks/<stackId>/<env>/stack/*.yml`
- Put service-specific overlays under `services/<svc>/<env>/*.yml`
