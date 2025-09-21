---
title: render
---

# `sb render`

Renders final, deployable `stack.yml` by deeply merging overlays and applying token/env expansion.

## Sources (in order)
1. `stacks/all/<env>/stack/*.yml` (global overlays)
2. `stacks/<stackId>/<env>/stack/*.yml` (stack overlays)
3. `services/<svc>/<env>/*.yml` (service overlays)

> Later files override earlier ones. Map nodes are merged recursively; list/scalar conflicts are reported.

## Special handling
- `environment` emitted as `- KEY=VALUE` list (keeps order when possible)
- `labels` emitted as `- "key=value"` list
- `healthcheck.test` kept as YAML flow array
- `${ENVVARS}` token expands to a single-line `KEY=VALUE` space-separated string
- Process env overlay with allow-list from `use-envvars.json` (file wins over process env)

## Secrets
- Service overlays can include `x-sb-secrets:` with either a simple list of keys or objects with `key`, `target`, `mode`, `uid`, `gid`
- A secrets map at `ops/vars/secrets-map.<env>.yml` is used to resolve real Swarm secret names
- Missing map entries are surfaced as warnings in `validate`

## Output
```bash
sb render sso -e prod --out ops/state/last --history
# -> ops/state/last/sso-prod.stack.yml
# and time-stamped copy under ops/state/history/
```
