# Infisical provider

1) Configure:
```bash
sb utils infisical init
```

2) Upload plaintext batch:
```bash
sb utils infisical upload   --env dev --scope sso   --from ops/private/dev.json   --config ops/providers/infisical.yml
```

3) Sync to Swarm secrets:
```bash
sb secrets sync -e dev
```
