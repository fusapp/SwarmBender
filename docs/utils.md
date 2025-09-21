---
title: utils
---

# `sb utils`

Utility helpers for provider ecosystems.

## Infisical: upload
Upload flattened secrets from a JSON file (e.g., your `appsettings.json`) to Infisical (plaintext API v4).

```bash
sb utils infisical upload   --env dev   --scope sso   --from ops/private/dev.json   --config ops/vars/providers/infisical.yml
```

- Creates folders on demand (`/sso`, and nested) if configured (`autoCreatePathOnUpload: true`).
- Applies include/replace/keyTemplate rules before upload.

## Infisical: init (wizard)
Create or overwrite `ops/vars/providers/infisical.yml` interactively.

```bash
sb utils infisical init
```
