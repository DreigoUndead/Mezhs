# Standardize the development workflow

- ID: `standardize-development-workflow`
- Type: documentation and workflow
- Status: implementation on review branch; awaiting final validation and owner approval

## Goal

Make agent-driven development safe without requiring multiple agents for trivial edits. Maintain a clear human merge gate.

## Final workflow

1. **Classify the task** as Small, Standard or High-risk. State the reason. Owner may override and risk may require escalation.
2. **Plan:** Small changes can use a short PR/branch description. Standard and High-risk plans go to `main` first; High-risk plans require independent plan review.
3. **Code:** implement on a task branch; keep the final plan aligned with delivered behavior. Move it from `planned/` to `implemented/` on the branch **before** final review/testing. Update `specifications/current/` when relevant. Run developer checks and refresh Codegraph if needed.
4. **Review → Test:** review code and test coverage first, then run final verification. Standard changes use independent code review; High-risk changes require independent review and testing. Small changes can remain in a single chat, including checks.
5. **Fix loop:** defects return to development, followed by affected review and tests. Don't open a new chat for every trivial operation. Use independent agents when required.
6. **Context:** hand off when reasoning quality drops, context becomes confused or complexity warrants independence. There is **no invented token threshold**.
7. **Owner approval:** present exact candidate, risks and evidence. Pause until Monkey explicitly approves. The same final agent may merge afterward if authorized.
8. **Release:** recheck PR SHA and current `main`; revalidate changed/conflicting work, then merge. **Write the version and one changelog entry only during merge finalization on `main`**, for the entire task. Tag the finalized release and delete the branch. Update README/current specs only when affected.

## Cleanup and retained guidelines

- Root `AGENTS.md` directs agents to the workflow. Reusable instructions live in `.agents/`.
- Coding sanity questions, Codegraph instructions and architecture ownership are preserved in `.agents/coding.md` and `docs/architecture.md`.
- Historical TODO observations live in `docs/backlog/`, not the agent instructions.
- No unused CODEOWNERS, redundant planned-folder README or standalone plan-template directory.
- User-facing root README explains features, setup and use.

## Verification needed

Independent review of this workflow change, documentation consistency checks, and Monkey's explicit merge approval. No runtime changes are intended.
