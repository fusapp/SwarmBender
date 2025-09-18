# SwarmBender Solution

Two-project layout:

- `SwarmBender/` — core library (services, abstractions, models)
- `SwarmBender.Cli/` — CLI frontend, depends on core

## Build

```bash
dotnet build SwarmBender.Cli -c Release
```

## Run

```bash
# init
dotnet run --project SwarmBender.Cli -- init
dotnet run --project SwarmBender.Cli -- init payments -e dev,prod

# validate
dotnet run --project SwarmBender.Cli -- validate
dotnet run --project SwarmBender.Cli -- validate backoffice --out ops/reports/preflight/all.json
```
