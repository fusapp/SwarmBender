# Secrets lifecycle

SwarmBender can:
- flatten JSON/appsettings to env-like keys,
- map keys to versioned Swarm secrets,
- write a `secrets-map.<env>.yml`,
- validate & render service `secrets:` blocks.

CI-friendly providers:
- `env` and JSON file sources
- Infisical (pull/push, folder auto-create)
