# Coding

Follow [the workflow](workflow.md), [software-change](skills/software-change/SKILL.md) and [systematic-debugging](skills/systematic-debugging/SKILL.md) for bugs.

- Start a **new chat** for each development step or fix. Read the plan and existing branch state.
- For first implementation, branch from `main` using the plan ID. For fixes, keep the **same development branch**.
- Find the existing owner of the behavior. Reuse existing mechanisms. Avoid unrelated edits.
- Update the final plan as design decisions evolve.
- **Use Codegraph when available** to navigate references and dependencies. After completing code changes, regenerate any committed dependency graph using the repository's exporter. Do this before independent testing and review. Never hand-edit generated graphs.
- Add relevant tests and verify the diff.
- Update `specifications/current/` only when existing behavior/contracts change. Write only the resulting behavior, not a copy of the plan or workflow. These updates are reviewed with the code.
- Prepare release documentation and archive the final plan on the same branch.
- Commit and push. Never merge without approval.
- Give the following prompt to a **new testing chat**:

> Test branch `<id>` at commit `<sha>`. Read `.agents/testing.md` and the final plan in `specifications/implemented/<id>/`. Run the relevant tests and verify any updated current specifications. Return PASS, FAIL or BLOCKED with actual evidence. On failure, provide a fix prompt for a new developer chat. On success, provide a review prompt for a new reviewer chat.