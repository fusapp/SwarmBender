---
title: secrets
---

# `sb secrets` (lifecycle)

Manage Swarm secrets: **sync**, **doctor**, and **prune**.

## Sync
Create missing secrets (from providers), write/update the secrets map.

```bash
# choose provider engine: env / file / docker-cli
export SB_SECRETS_ENGINE=docker-cli

# global scope
sb secrets sync -e prod -t global

# stack/service scope
sb secrets sync -e dev -t sso
```

## Doctor
Compare map vs Docker Engine (missing/orphaned).

```bash
sb secrets doctor -e prod
```

## Prune
Remove orphaned secrets from Engine (not in map).

```bash
sb secrets prune -e prod --dry-run
```

> The generated map lives at `ops/vars/secrets-map.<env>.yml`.
