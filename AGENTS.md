# MEŽS — Contributor and Agent Entry Point

Before editing, read [the workflow](.agents/workflow.md) and the guidance for your **current stage**.

- [Specification](.agents/specification.md) · [Coding](.agents/coding.md) · [Testing](.agents/testing.md) · [Code review](.agents/review.md) · [Release](.agents/release.md)
- [Current system specifications](specifications/current/) · [Approved plans](specifications/planned/) · [Completed changes](specifications/implemented/)
- [Repository-local skills](.agents/skills/) · [Architecture](docs/architecture.md)

**Mandatory:** One approved plan per change; planning PR first, implementation branch from the main containing that plan. Independent testing and independent code review before Monkey's **explicit post-review approval** to merge implementation. `main` is always releasable. Never push or merge an implementation into `main` without that approval.

For the initial adoption, `standardize-development-workflow` is the one-time bootstrap exception: its plan and implementation may enter the same review PR. This exception does not waive independent validation or Monkey's merge approval.