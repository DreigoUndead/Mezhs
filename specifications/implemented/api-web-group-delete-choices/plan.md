# API Web UI group deletion choices
ID: api-web-group-delete-choices
Level: Standard — destructive bulk deletion, shared Core/Agent chat groups, UI/API sequencing.

## Goal
In the normal MEŽS API Web UI, replace the one-way browser confirmation on the group delete control with an explicit modal offering **Unassign conversations**, **Delete conversations**, and **Cancel**. Keep the existing non-destructive category DELETE path available.

## Context and ownership
- Task branch: `api-web-group-delete-choices`, based on `main`. Keep main release-only and do not edit version or changelog during development.
- `src/Mezhs.Web.Lib/src/MezhsChatApp.tsx` owns the normal API Web UI. Agent Web retains its current group-delete experience in this scoped change.
- `GET /v1/chats` lists all Core chats, including those backing Agent conversations when running under the Agent API host. Group membership is shared.
- `DELETE /v1/categories/{categoryId}` currently removes the category and unassigns its chats.
- Reuse `DELETE /v1/chats/{chatId}` or bulk `DELETE /v1/chats` for destructive deletion, so the Agent host keeps its Agent-first/Core-second cleanup. Delete the category only after successful chat deletion.
- Keep server endpoints unchanged unless inspection discovers a correctness gap that requires an API change; do not add an alternative chat deletion path or cross-store transaction.

## Implementation
1. Add an accessible group-deletion dialog showing the group name and number of conversations from a fresh `GET /v1/chats`, not from the sidebar's current filter.
2. The explicit choices are:
   - **Unassign conversations**: existing `DELETE /v1/categories/{id}` request; preserve all conversations (including Agent chats).
   - **Delete conversations**: delete *all* chats assigned to that category, including Agent chats, using the existing bulk/single chat deletion API; then delete category.
   - **Cancel**: dismiss with no write requests.
3. Before executing either action, refresh the chat list. If the set of category chat IDs changed since the dialog was shown, update the count, display a warning, and require the user to choose again rather than operating on a stale selection.
4. Disable competing delete actions while in flight; prevent dialog dismissal while pending. Reuse the existing stale-chat guard and reset selected chat if removed. Refresh the category/sidebar lists on success.
5. Show an explicit irreversible deletion disclosure (chat/Agent state removed; legacy backups and historical Executor/file records may remain; shell stop not guaranteed). Report errors inside the dialog; leave retry possible if the first action succeeded and subsequent category deletion failed. Never claim that both steps are atomic.
6. Style with the existing API Web UI dialog treatment, keyboard Escape/cancel and accessible labels. Do not change Agent Web or unrelated group/manager controls.

## Verification

Developer verification: source-level checks against the committed dialog, ordering, API routes, and CSS passed. A full frontend build and live API/browser smoke tests have not been run in this connector environment; independent code review and independent testing are still required.
- Add targeted shared-UI source regression assertions for option names, count, operations, and existing data-retention disclosures.
- Build both Web frontends if available and run `tests/agent-web-shared-ui.ps1` and relevant chat API smoke tests in a full developer workspace.
- Manually verify group with zero/multiple chats; Agent + normal chats; cancel; unassign; delete; selected conversation deletion; fetch/delete failures; changes to membership while dialog is open; repeat after partial success.
- Before handoff, move this plan on the same task branch from `specifications/planned/` to `specifications/implemented/`, then request independent code review and independent testing; neither is self-approval.

## Risks and accepted scope
- This UI orchestrates two API operations, not a distributed transaction. If category deletion fails after chat deletion, the category may remain with zero chats; report and allow retry.
- A concurrent writer may assign a new chat immediately after the pre-action refresh. The current category DELETE unassigns any such late chat; this UI cannot guarantee concurrent writers' chats are deleted. Fully atomic group-and-chats deletion would require a separate server-side design and review.
- Never merge to `main` without explicit owner approval.
