# Independent testing

A fresh testing MEŽS agent (not the coding agent) must:

1. Record target branch and commit SHA; read the **final** `specifications/implemented/<id>/` plan and relevant `specifications/current/`.
2. Execute acceptance tests, relevant unit/integration/runtime checks, regressions, security and compatibility checks. Do not rely on the implementer's claims. Record actual commands, outputs, coverage and limitations.
3. Run the established `.github/workflows/mezhs-core.yml` checks where appropriate. If local .NET/PowerShell tooling is unavailable, use GitHub Actions on the **same branch**, with temporary validation artifacts removed before final review. Never report an unrun test as PASS.
4. Report **PASS / FAIL / BLOCKED**, linked to tested commit and requirement evidence. On failure return reproducible findings to coding without silently editing production behavior. On PASS, produce a ready-to-run independent review prompt.

### Handoff prompt

"Independently review MEŽS branch `<id>` at `<tested-sha>` against `main`. Read `.agents/review.md`, `specifications/implemented/<id>/`, `verification.md` and the full diff. Testing result: <status, commands, evidence>. Check behavior, architecture, security, regressions and release metadata. Return PASS/FAIL/BLOCKED and actionable findings. Do not merge or silently fix production code. On PASS, prepare Monkey's approval package."

Notes: compile-only or mock tests do not establish live upstream browser protocol behavior; avoid claiming untested coverage. Do not kill running developer applications to obtain a passing build.