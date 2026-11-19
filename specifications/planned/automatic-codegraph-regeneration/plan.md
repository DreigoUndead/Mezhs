# Automatic Codegraph regeneration
ID: automatic-codegraph-regeneration

Status: **Planning draft — consequential owner choices open; no implementation authorized**
Level: **High-risk** (CI repository-write permission plus concurrent updates to task branches)
Base: `main`, starting revision `ed98bb5c3f9c0a07fbf6589b4b6819fdba0519ee`
Task branch: `automatic-codegraph-regeneration`

## Goal and scope

Automatically regenerate and commit `.agents/codegraph/graph.json` on task-branch pushes using the existing `node .agents/codegraph/export.cjs`. Commit only if the generated file actually differs. Never push to `main` without the owner's explicit release approval.

In scope: one GitHub Actions workflow; minimal workflow/documentation edits about automatic graph refresh; targeted verification including branch races, self-trigger behavior, CI/test SHA alignment, and tool-version reproducibility.

Out of scope: a new exporter, codegraph parsing changes, agent orchestration, GitHub App/PAT credential provisioning without explicit owner choice, and changes to release/version/changelog before owner-approved merge.

## Existing behavior and feasibility

- `.agents/codegraph/export.cjs` already runs the Codegraph CLI, validates the JSON schema and tracked/portable paths, and rewrites `graph.json`. Reuse it unchanged unless verification reveals a genuine CI portability issue.
- `.agents/coding.md` names Codegraph **3.0.0**. Existing `.github/workflows/mezhs-core.yml` uses Node.js 22 and runs on PRs and pushes to main; it does not install Codegraph. `.github/workflows/whatsapp-api.yml` is unrelated.
- The CLI package is **`@lzehrung/codegraph`** (not unscoped `codegraph`). Install a pinned `@lzehrung/codegraph@3.0.0` on a Node.js **22.16+** runner, verify `codegraph --version` and `codegraph doctor`, then execute the exporter.
- Codegraph is not a source of truth; the graph is a generated navigation artifact.

## Proposed minimal approach (subject to owner choice)

1. Add one `push` workflow for eligible non-main branches, with explicit `permissions: contents: write`, a bounded job timeout, and concurrency grouped by branch, `cancel-in-progress: false`. Do not run automatically on `main` or `pull_request_target`; never check out untrusted PR code with a privileged PR-target token.
2. Checkout the relevant branch, install pinned Codegraph on Node 22, verify the installed CLI/native runtime, and execute `node .agents/codegraph/export.cjs`.
3. Stage **only** `.agents/codegraph/graph.json`; if no staged content change, exit successfully without a commit. Otherwise create one bot-authored `chore(codegraph): regenerate graph` commit and push back to the same branch without force. Do not modify `main`, versions, changelog, or other generated files.
4. Prevent stale writes by using ordinary non-force push: if a concurrent branch update makes a push non-fast-forward, fail clearly; a newer queued branch run refreshes the new tip. Keep the logic simple; no custom retry queue, long-lived locks, or external service.
5. After successful adoption, update `.agents/coding.md` and `.agents/workflow.md`: authors need not commit a manual Codegraph refresh for pushed task branches, but must ensure the bot graph commit is present **before** independent review/testing. Preserve manual export as an offline/failure fallback and verify exact final SHA before handoff.

## Consequential owner choices — decide before implementation

**A. Token and exact-sha CI behavior:**

- **Recommended simplicity:** use repository `GITHUB_TOKEN`. GitHub suppresses downstream push/PR workflow triggers for its commits, so existing PR CI can remain on the pre-graph commit. Require an explicit final-SHA test/review check; never present an earlier PR run as CI evidence for the generated head. No new secrets or CI orchestration.
- **Stronger automatic CI on the bot-created head:** a tightly scoped GitHub App installation token (or other separately approved non-`GITHUB_TOKEN` credential) can cause normal PR CI to run. Filter graph-only commits out of the generator trigger to prevent loops. Adds credential/security setup; do not invent or provision it without owner consent.
- Do **not** silently claim full automated CI at the final SHA with the first choice. If that is required, the owner must choose a stronger design.

**B. Branch coverage:**

- **Recommended:** trigger on all non-`main` branches, since existing task branches have no common naming prefix; optional exclusions for `backup/**` and `review/**` require owner decision.
- Alternative: adopt a task-branch naming convention/opt-in filter. This avoids unexpected graph commits to non-task branches but excludes current task branches that do not follow the convention.

**C. Manual obligation:** remove the requirement for each developer to regenerate/commit the graph only *after* verified automation is deployed; retain the exporter for local/offline use and recovery.

## Acceptance criteria

- [ ] Every eligible task-branch source push runs the exporter with verified pinned CLI and a suitable Node version.
- [ ] Unchanged graph produces zero commits; changed graph yields one graph-only commit.
- [ ] No automatic write to `main`, even for a change to workflow files.
- [ ] Bot-generated commits cannot cause an infinite workflow loop.
- [ ] Concurrent task-branch pushes cannot overwrite a newer branch commit. A superseded job may fail and is not counted as a successful regeneration.
- [ ] Independent review and test evidence identify the exact final branch SHA, including any Codegraph bot commit, and distinguish automated CI runs from manual/local verification.
- [ ] Existing exporter graph is not manually modified; documentation reflects the adopted responsibility.
- [ ] Version/changelog remain untouched on the development branch; merge requires separate owner approval.

## Verification

1. Validate workflow syntax, job permissions, branch filters and triggering semantics.
2. Verify `@lzehrung/codegraph@3.0.0` availability and CLI flags on the selected runner; run `codegraph --version`, `codegraph doctor`, then `node .agents/codegraph/export.cjs`.
3. Validate idempotence (second regeneration has no change), no-op push, graph-changing source push and exactly one graph-only bot commit.
4. Verify branch-scope exclusions and no main push; simulate overlapping pushes and confirm no lost/forced updates or infinite loop.
5. Confirm CI/verification evidence against the final head SHA, not only the earlier triggering push SHA.
6. Independent High-risk plan review before implementation, independent code review, and independent final testing under the existing workflow.

## Pending decisions / gates

Await owner decisions A and B before implementation. Review the plan independently after decisions are incorporated. Plan approval does **not** grant merge permission.
