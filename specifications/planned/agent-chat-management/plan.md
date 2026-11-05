# Agent UI chat management
ID: agent-chat-management
Level: High-risk — deletion crosses core chat files, Agent SQLite state and Executor processes.

## Scope

Bring these standard MEŽS chat-management features to Agent Web:

1. Delete one chat from its `⋯` menu.
2. Bulk select/delete chats, including all chats currently shown by the manager.
3. Search Agent chats.
4. Create/delete groups, assign chats to groups and filter by All, group or Uncategorized.
5. Show activity dates and total/filtered conversation counts.

Keep existing Agent policy, connection, model, pause/resume, execution-history, debug-log, draft and polling behavior. **Out of scope:** pinning, replay, new attachment features, category renaming and changes to Agent execution policy.

## Approach

- Follow the existing chat-management UI in `Mezhs.Web.Lib/MezhsChatApp.tsx`; reuse simple shared pieces only when useful. Agent Web retains its own controls and polls only Agent chats.
- Reuse `/v1/categories` and `PATCH /v1/chats/{chatId}`. Expose `CategoryId` in `AgentChatView`. Groups are shared across normal and Agent chats on the same API host, but Agent Web search, filters and counts show **Agent chats only**. Deleting a group unassigns **all** chats in that group, including normal chats; explicitly say so in the confirmation. Chats themselves are kept.
- Search locally by title, policy, connection, origin and group. Order and display activity using the later of core `ChatRecord.UpdatedAt` and Agent `AgentChatRecord.UpdatedAt`, so new messages and Agent state changes are reflected without adding another stored timestamp.
- If a new Agent chat is started with a group selected, assign its group through the existing API once its chat ID is known. Failure to assign must not lose the newly created chat.

## Safe deletion

The Agent host also exposes the normal `DELETE /v1/chats` and `DELETE /v1/chats/{chatId}` routes. These must not bypass Agent safety checks.

- Keep **one deletion mechanism** behind those existing routes. The normal API uses its current `ChatStore` behavior; the Agent host supplies Agent-aware handling. Do not add a parallel Agent deletion endpoint or couple the core API to Agent/Executor types.
- Validate the **entire** single/bulk request before deleting: distinct, existing chat IDs, with no queued/running core messages, no nonterminal Agent execution (including work not yet claimed into `AgentChats`), and no nonterminal Executor shell. Query all relevant execution states, not a capped history list. If any chat is busy, reject the whole request without cancelling or killing work.
- Make validation and deletion race-safe with a **single coordinated deletion guard** for affected chats, persisted in Agent state for restart recovery. Enforce it at core message admission/replay, Agent enqueue/claim/attachment/recovery, and Executor shell creation/restart. A retained historical shell execution must never be restartable after its chat is deleted. Do not rely on a check-then-delete sequence.
- Remove the core transcript and associated Agent rows (`AgentChats`, Agent executions and staged attachment references). If multi-store cleanup is interrupted, keep deletion blocked and reconcile on restart rather than reviving a chat or claiming complete success. Preserve unrelated chats and ordinary API deletion behavior.
- Retain completed **Executor execution records** as audit history, and retain existing shared `FileStore` files; physical orphan-file cleanup is not part of this task. Deletion confirmation must state that conversation/Agent data is removed but historical Executor records and stored files may remain.

## Agent Web behavior

- Use the standard row menu and bulk manager patterns with keyboard-accessible controls, clear confirmation and visible server errors. Search/select-all/delete-all operate on the manager's **currently displayed Agent chats**, never ordinary chats or hidden rows.
- Preserve the active chat, drafts, polling, scroll and selection through filters/refreshes. After deletion, prevent stale responses from restoring removed chats or messages; switch a deleted selected chat to a valid remaining chat or new-chat state.
- Show group-specific Agent counts, last activity and total/filtered counts. Refresh these after chat activity, grouping and deletion.

## Acceptance and verification

- [ ] Single and bulk deletion target only intended chats; busy or invalid batches fail without partial deletion.
- [ ] Deletion through **either** core DELETE route is safe for Agent chats, even before chat claim; ordinary API behavior remains compatible.
- [ ] Concurrent core messages, Agent submissions/claims, shell creation/restart and interrupted deletion cannot revive deleted data or start work on deleted chats.
- [ ] Agent-owned data is removed, completed Executor history and shared files are retained, and the confirmation is accurate.
- [ ] Search, groups, dates, counts, drafts, selection and polling behave correctly; shared group deletion warns about normal chats.
- [ ] Normal Web and API behavior do not regress; pinning and new replay/attachment UI are not added.

Verify with builds/type checks, normal and Agent UI tests, core/Agent API tests, focused persistence and concurrency tests, restart-recovery tests, and targeted interactive UI checks. High-risk work requires independent code review **before** independent final testing.

## Workflow

1. Complete the independent High-risk plan review before implementation.
2. Implement on branch `agent-chat-management`. Update `specifications/current/` for actual behavior; move this plan to `specifications/implemented/agent-chat-management/plan.md` on the development branch.
3. Report test/review evidence, exact commit, remaining risks and proposed MINOR bump. **Do not merge implementation into `main` without separate owner approval.** Leave `VERSION` and `CHANGELOG.md` unchanged until the approved release.
