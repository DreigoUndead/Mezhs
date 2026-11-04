# Development workflow — current behavior

See [the development workflow](../../.agents/workflow.md).

A plan is committed to `main`, then reviewed and updated until no changes remain. The plan reviewer produces a development prompt. Implementation runs on a new branch named after the plan. A developer generates a testing prompt. Failed tests return to coding. Passed tests generate an independent review prompt. Review issues return to coding and testing. When tests and review pass, Monkey reviews the final candidate and explicitly approves or rejects merging. Every implementation merge into `main` creates a release; planning documentation does not.

The final plan moves to `specifications/implemented/` and the resulting behavior is maintained in `specifications/current/`.