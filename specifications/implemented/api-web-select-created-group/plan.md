# API Web: select newly created group
ID: api-web-select-created-group
Level: **High-risk by explicit owner request for workflow demonstration.** The behavioral change itself is a small UI-state change. Apply every High-risk gate without inventing an architectural/security risk.

## Goal and scope
When the user creates a group in the **regular MEŽS API Web UI**, make the newly created group the active group filter immediately after successful creation. Existing conversations must not be moved by this action. A new conversation started from that view should inherit the created group as its default.

In scope:
- `src/Mezhs.Web.Lib/src/MezhsChatApp.tsx` (regular API Web UI), using the existing `/v1/categories` endpoint and existing selection helper.
- Relevant shared-UI regression tests (especially `tests/agent-web-shared-ui.ps1`), which currently reject automatic selection.
- Documentation/verification evidence as warranted by the actual behavior change.

Out of scope:
- Agent Web `src/Mezhs.Agent.Web/src/App.tsx` (its creation behavior is unchanged).
- Changes to categories/chat persistence, endpoint contracts, moving existing chats, group deletion, renaming behavior, filtering semantics outside creation, release metadata before merge.

## Context, ownership and dependency
- Task branch: `api-web-select-created-group`, created from `main` at `dfb6053daf75fbffab7d35e00331b0a2dd2213dd` **before** committing this plan.
- `main` is release-only; no change or plan commit to `main`, no version/changelog change on the task branch.
- The regular UI keeps `categoryFilter` (displayed chat filter) and `newChatCategoryId` (new-chat default). `selectCategory(id)` already synchronizes both.
- The current `saveCategory` POST branch discards its returned `Category`, then refreshes categories; it does not change either selection value. Rename uses PUT in the same function and must not change selection.
- `tests/agent-web-shared-ui.ps1` has an explicit historical assertion that *neither* regular Web nor Agent Web selects a newly created category. The historical `specifications/implemented/agent-chat-management/plan.md` still describes that original behavior. This task supersedes it **only for regular API Web**, not Agent Web. Revise only the regular-Web expectation.
- Open draft PR #51 (`api-web-group-delete-choices`) also edits this component, but this task **does not depend** on group-deletion changes. Keep a separate branch based on `main`; re-check/reconcile overlapping diff before release and do not absorb unrelated work.

## Proposed minimal approach
1. In the `editingCategory === "new"` branch, retain the created `Category` returned by successful POST. Preserve the existing category refresh.
2. After successfully refreshing the categories, call the existing `selectCategory(created.categoryId)` helper so the group becomes the active dropdown filter **and** the default for a subsequent new conversation. Do not select by name, list index, or guessed ID.
3. Preserve existing editor closure, error display, submit lock, and rename path. Do not switch selection on a failed POST or failed category refresh. A successful POST followed by a failed refresh can leave the group created but not selected, matching the current refresh-error behavior; acknowledge and verify this failure mode instead of introducing a new state-management mechanism without review.
4. Update the existing shared-UI test so it asserts regular-Web create selection through the returned category ID and continues asserting Agent Web non-switching behavior. Prefer observable interaction tests in addition to source assertions when tooling permits.

## Acceptance criteria
- [ ] Creating a group from `All groups` or `Uncategorized` selects **the newly created group ID** in the regular API Web dropdown without an additional click or page reload.
- [ ] The chat list reflects the new filter (typically empty); existing chats retain their groups.
- [ ] Starting a new conversation while that filter is selected uses the new category as its default; no pre-existing conversation is reassigned.
- [ ] Renaming a group does not select another group.
- [ ] Failed POST does not switch filters; the editor reports the error and permits retry.
- [ ] A category-list refresh failure (for example GET `/v1/categories` HTTP 500) does not claim successful selection and leaves the prior filter intact. HTTP 503 and network-read failures are retried by the existing API helper; do not treat them as immediate refresh failures.
- [ ] Agent Web creation and ordinary category navigation retain their existing semantics.
- [ ] No changes to version/changelog before approved release; no merge without explicit owner approval.

## Verification and High-risk gates
1. **Independent plan review — owner override of pre-coding PASS gate (2026-11-10).** The independent reviewer reported FAIL because the plan required creation while another named group was selected, which the existing UI does not support. The owner clarified the minimal scope (only existing creation entry points), then explicitly instructed the developer to proceed, stating the plan is acceptable. The owner has therefore overridden the plan-review PASS gate for this task. **Do not claim an independent plan-review PASS.** All subsequent independent review and test gates remain mandatory.
2. **Implementation on the same task branch**, with developer checks; replace the historical negative source assertion with positive regular-Web created-ID selection and retained Agent Web non-switching assertions. Verify rename, error paths, and default-new-chat selection in developer/independent checks. Keep change minimal.
3. Move finalized plan to `specifications/implemented/api-web-select-created-group/plan.md` **on this branch** before code review, not to `main`.
4. **Independent code review — required before final testing.** Different agent, neutral handoff, full diff and plan, PASS / FAIL / BLOCKED. Review must include test adequacy and potential overlap with PR #51.
5. **Independent testing — required after code review.** Different agent from developer and reviewer. Run relevant frontend builds and `tests/agent-web-shared-ui.ps1` in a full workspace, plus actual UI/API checks for success, rename, POST failure, refresh failure, and new-conversation default. Record exact SHA, base SHA, command exits, and PASS / FAIL / BLOCKED; CI alone is not an independent test.
6. Present PR, SHA, review, tests, trade-offs and patch-release proposal to the owner. Wait for **separate explicit merge approval**; recheck current `main`, PR #51 interaction and candidate SHA before release.

## Risks and trade-offs
- The category POST may succeed while the subsequent GET fails, leaving persisted group creation but the UI editor in an error state; this is an **existing** two-request failure mode. The proposed scoped change preserves it. Reviewer should assess whether keeping it is acceptable rather than silently expanding scope.
- Selection after asynchronous requests can race with user actions; the existing saving editor disables competing group selection controls during the request. Verify this assumption.
- A new category ID must be taken from the server response, never inferred from the displayed name; renamed/duplicate names must not change the intended selection.
- PR #51 and this task modify the same component. Rebase/conflict resolution may require fresh review and tests before merge.
