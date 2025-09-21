---
title: validate
---

# `sb validate`

Validates stacks against policy (labels/images/guardrails) and JSON structure of `appsettings*.json`.

## Usage
```bash
# validate all stacks across detected envs
sb validate -e dev,prod --details

# validate a single stack
sb validate sso -e prod --details
```

### What it checks
- Top-level compose v3 keys (allowed/forbidden)
- Image policy (e.g., forbid `:latest`)
- Presence of healthchecks/logging (if required by guardrails)
- JSON parseability of any `appsettings*.json` under `stacks/all/<env>/env/` and `services/<svc>/env/<env>/`
- Required env keys per service/group (if defined in `ops/checks/required-keys.yml`)
- Secrets/configs definition shape in `stacks/<id>/(secrets|configs).yml`
