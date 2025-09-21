---
title: FAQ
---

# FAQ

**Q: Why Swarm instead of K8s?**  
A: Swarm is still a lightweight option for small/medium fleets. SwarmBender focuses on making Swarm sane and repeatable.

**Q: Does `render` deploy?**  
A: No. We separate concerns. Use your CI/CD (e.g., `docker stack deploy`) after rendering.

**Q: Where do secrets actually live?**  
A: In Docker Engine as Swarm secrets. Providers are used to **source** values; the rendered stack uses secret **names** only.
