---
title: init
---

# `sb init`

Initialize the project or a specific stack scaffold.

## Usage
```bash
# root scaffold
sb init --env dev,prod [--no-global-defs]

# stack scaffold
sb init <STACK_ID> --env dev,prod [--no-defs] [--no-aliases]
```

### What gets created
- Root: `stacks/`, `services/`, `metadata/`, `ops/` (+ baseline policy/check files)
- Stack: `stacks/<id>/docker-stack.template.yml`, `stacks/<id>/aliases.yml`
- Global env folders: `stacks/all/<env>/` with `env/` and `stack/` subfolders
