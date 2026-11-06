# Agent UI chat management
ID: agent-chat-management
Level: High-risk — deletion crosses core chat files, Agent SQLite state and Executor processes.

## Scope

Provide consistent chat-management behavior in **Agent Web and regular MEŽS Web** (both use the shared core API):

1. Delete an individual conversation from its `⋯` menu, and bulk select/delete currently displayed conversations.
2. Search conversations and show last-activity dates plus total/filtered counts.
3. Create, **rename**, delete and filter shared groups; move conversations between groups.
4. Rename a conversation in its `⋯` menu with a persisted, editable title.
5. Keep Agent Web scoped to **Agent chats only** in its listings, searches and bulk actions, while the regular Web UI manages regular chats.

Keep existing policy, connection/model, pause/resume, execution history, debug log, draft, and polling behavior. **Out of scope:** pinning, new replay/attachment features, and changes to Agent execution policy.

## Approach

- Keep the two Web UIs visually and functionally aligned for chat management, retaining their distinct composer, policy and provider controls. Do not introduce parallel chat-management endpoints or unnecessary shared abstractions.
- Use `/v1/categories` (create/rename/delete) and `PATCH /v1/chats/{chatId}` for category assignment and title changes. Store a validated custom title in `ChatRecord` and show it preferentially over generated first-message/first-task titles in both Web UIs. Existing category-only PATCH requests remain compatible.
- Expose `CategoryId` in `AgentChatView`. Groups are shared across regular and Agent chats on the same API host, but Agent Web search, filters and counts show **Agent chats only**. Deleting a group unassigns **all** its conversations, including regular ones; say so in the confirmation. The conversations are kept.
- Filter locally by title, relevant connection/policy/origin data and group. Order and show Agent activity using the later of core `ChatRecord.UpdatedAt` and Agent `AgentChatRecord.UpdatedAt`.
- New chats started under a selected group inherit that group; creating a **new group never changes the current filter**. New Agent group assignment uses the existing API once the chat ID exists, without losing the new chat if assignment fails.

## Safe deletion

The Agent host also exposes the normal `DELETE /v1/chats` and `DELETE /v1/chats/{chatId}` routes. These must not bypass Agent safety checks.

- Keep **one deletion mechanism** behind those existing routes. The normal API uses its current `ChatStore` behavior; the Agent host supplies Agent-aware handling. Do not add a parallel Agent deletion endpoint or couple the core API to Agent/Executor types.
- Validate the **entire** single/bulk request before deleting: distinct, existing chat IDs, with no queued/running core messages, no nonterminal Agent execution (including work not yet claimed into `AgentChats`), and no nonterminal Executor shell. Query all relevant execution states, not a capped history list. If any chat is busy, reject the whole request without cancelling or killing work.
- Make validation and deletion race-safe with a **single coordinated deletion guard** for affected chats, persisted in Agent state for restart recovery. Enforce it at core message admission/replay, Agent enqueue/claim/attachment/recovery, and Executor shell creation/restart. A retained historical shell execution must never be restartable after its chat is deleted. Do not rely on a check-then-delete sequence.
- Remove the core transcript and associated Agent rows (`AgentChats`, Agent executions and staged attachment references). If multi-store cleanup is interrupted, keep deletion blocked and reconcile on restart rather than reviving a chat or claiming complete success. Preserve unrelated chats and ordinary API deletion behavior.
- Retain completed **Executor execution records** as audit history, and retain existing shared `FileStore` files; physical orphan-file cleanup is not part of this task. Deletion confirmation must state that conversation/Agent data is removed but historical Executor records and stored files may remain.

## Web UI behavior

- Use a compact, consistent sidebar in both Web UIs: a stable **[group selector] [add/rename] [delete]** row and, while editing, **[name] [confirm] [cancel]**. Use icon buttons, disabled Delete when no named group is selected, and fixed row heights. The group and search input edges must align.
- Place **[search input] [+ new chat/agent] [manage gear]** in one row, with accessible names and hover hints. Do not add redundant group/search headings, a separate top New button, or a group picker in the conversation header.
- Each chat `⋯` menu contains a title textbox, group dropdown and Delete. Save a non-empty renamed title on blur/Enter; close the menu on loss of focus, outside click or Escape.
- Show compact two-line conversation rows with policy/connection/origin details and date on one metadata line. Use a readable, scrollable bulk manager with truncated long titles, select-all, individual deletion, Delete selected and Delete all shown; close via Close, outside click or Escape.
- Search/select-all/delete-all operate only on the **currently displayed** conversations in that Web UI and manager filter. Agent Web must never include ordinary chats in its bulk operations.
- Preserve active chat, drafts, polling, scroll and selection through filters/refreshes. After deletion, prevent stale responses from restoring removed chats or messages; reset a deleted selected chat to a valid remaining chat or new-chat state.
- Preserve group-specific Agent counts, activity dates and total/filtered counts, updating after chat activity, group changes and deletions.

## Acceptance and verification

- [ ] Single and bulk deletion target only intended chats; busy or invalid batches fail without partial deletion.
- [ ] Deletion through **either** core DELETE route is safe for Agent chats, even before chat claim; ordinary API behavior remains compatible.
- [ ] Concurrent core messages, Agent submissions/claims, shell creation/restart and interrupted deletion cannot revive deleted data or start work on deleted chats.
- [ ] Agent-owned data is removed, completed Executor history and shared files are retained, and the confirmation is accurate.
- [ ] Search, counts, dates, drafts, selection and polling behave correctly; creating a group does not auto-select it, and deleting a shared group warns that regular chats are also unassigned.
- [ ] Both Web UIs have matching compact group CRUD, sidebar controls, context-menu editing, filtering and bulk manager behavior; focus and dismissal work by keyboard and pointer.
- [ ] Renamed conversation titles are validated, persisted across reload/restart, shown in both Web UIs and do not change the chat category; category PATCH remains compatible.
- [ ] Both Web UI builds and shared UI/API smoke tests pass; pinning and new replay/attachment UI are not added.

Verify with builds/type checks, regular and Agent Web UI tests, core/Agent API tests, title/category persistence tests, focused deletion/concurrency and restart-recovery tests, and targeted interactive UI checks. High-risk work requires independent code review **before** independent final testing.

## Workflow

1. Complete the independent High-risk plan review before implementation.
2. Implement on branch `agent-chat-management`. Update `specifications/current/` for actual behavior; move this plan to `specifications/implemented/agent-chat-management/plan.md` on the development branch.
3. Report test/review evidence, exact commit, remaining risks and proposed MINOR bump. **Do not merge implementation into `main` without separate owner approval.** Leave `VERSION` and `CHANGELOG.md` unchanged until the approved release.
