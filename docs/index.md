---
layout: default
title: SwarmBender
---

# SwarmBender

A fast, batteries-included CLI to **render Docker Swarm stacks** and **manage secrets** across environments.

> Project site: `{{ site.url }}{{ site.baseurl }}`

![SwarmBender Logo]({{ '/assets/logo.svg' | relative_url }})

## Quick Install

```bash
dotnet tool install --global SwarmBender.Cli
# or update
dotnet tool update --global SwarmBender.Cli
# run
sb -h