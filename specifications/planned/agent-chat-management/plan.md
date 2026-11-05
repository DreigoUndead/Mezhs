# Agent UI chat management
ID: agent-chat-management
Level: High-risk — conversation deletion spans core chat persistence, Agent SQLite state and Executor-owned runtime evidence.

## Goal and scope

Bring Agent Web chat navigation and management closer to the standard MEŽS Web UI, limited to the owner-approved features:

1. Individual agent chat deletion via the sidebar `⋯` menu.
2. Bulk management: select individual/all currently shown agent chats, delete selected and delete all shown with clear confirmation.
3. Client-side agent chat search.
4. Create/delete groups, assign existing agent chats to groups and filter by group (including All and Uncategorized).
5. Activity dates, total and filtered conversation counts in the sidebar.

Preserve Agent-specific policy/connection/model controls, pause/resume, execution history, debug logs, and command handling. **Out of scope:** pinning, replay, attachments, changing policy or execution semantics, category renaming, and any version/changelog change until approved release.

## Existing owners and design

- `src/Mezhs.Web.Lib/src/MezhsChatApp.tsx` contains the reference sidebar, conversation manager and category interactions; `src/Mezhs.Agent.Web/src/App.tsx` owns Agent Web selection/polling/agent-specific controls. Extract and reuse stable presentational or selection behavior where it reduces duplicated concepts without forcing Agent policies into the normal UI. Keep host-specific API operations in their hosts.
- `ChatStore` already owns categories and the persisted `ChatRecord.CategoryId`. Reuse the shared `/v1/categories` and `PATCH /v1/chats/{chatId}` APIs. No second category storage or Agent-only category endpoint. The groups therefore are shared with normal chats on the same API host; Agent Web counts/filtering include **only Agent chats**.
- Expose `CategoryId` in `AgentChatView` using the current normal chat record. Search locally across title, policy, connection, origin and group labels. Continue server-side last-activity ordering based on the existing Agent chat `UpdatedAt`; show a formatted date without changing how activity is recorded. Keep the sidebar's selected chat stable through search/refresh and handle a selected chat disappearing.
- For a new Agent chat started while a group is selected, assign the selected category after the chat ID is attached using the existing core category endpoint. If assignment fails, preserve the created chat, report the failure and refresh; do not extend the queued Agent execution schema just to carry an optional UI grouping preference.

## Deletion architecture and invariants

The existing `DELETE /v1/chats` and `DELETE /v1/chats/{chatId}` delete only `ChatStore` data. Because Agent API hosts `MapMezhsApi()` too, a normal Web client can delete an Agent chat through these routes. A UI-only `DELETE /v1/agent-chats` would leave this bypass.

- Use **one** deletion entry point for existing core DELETE endpoints: a host-specific deletion service/strategy registered by the normal API with its standard `ChatStore.DeleteChats` behavior, and replaced in Agent API with an Agent-aware implementation. Keep the core project independent of Agent/Executor types. Agent Web should call the existing DELETE routes, not duplicate a second deletion API.
- Validate the complete bulk selection before mutating anything: nonempty distinct IDs, existing chats; no queued/running/cancel-requested root Agent execution, active attached message, or nonterminal Executor shell associated with any selected Agent chat. A busy chat causes a visible conflict/error; do not implicitly cancel/kill work. Ordinary chats retain their normal deletion behavior.
- Coordinate deletion with Agent admission/recovery, not just a non-atomic "check then delete". Provide a durable per-chat deletion guard/tombstone in Agent state (or an equally strong serialized invariant): once deletion begins, new root submissions, queued claims, chat attachment/recovery and shell launches cannot revive that chat. Ensure the guard is checked at the actual transactional admission boundary, not only before enqueueing. Complete or reconcile interrupted deletion after restart; handle persistence failure without reporting success or silently orphaning data.
- Delete the selected chat transcripts and associated Agent-owned rows (AgentChats, Agent Executions, staged attachment references) coherently, while preserving unrelated chats. Treat an already-missing or inconsistent cross-store record explicitly; do not silently report a complete delete when cleanup failed. Keep SQL operations parameterized.
- **Retention policy for review:** completed Executor-owned shell execution records remain durable audit evidence (the Executor store owns them and may be referenced independently); no nonterminal shell may survive a successful chat delete. The UI confirmation must not claim that historical Executor evidence is removed. Do not introduce a new Executor deletion API solely for this feature.
- Respect the normal API's existing 404 and bulk error semantics as far as possible. Guard deletion through the normal API too, so the standard Web UI cannot sidestep Agent safety rules on an Agent host.

## UI behavior

- Adapt the standard `⋯` row menu and conversation manager to Agent Web, preserving accessibility, error states and confirmation. A bulk manager searches within the currently filtered Agent chat subset; counts and select-all clearly reflect what is shown. Never target ordinary non-Agent chats.
- Display group selection and create/delete/assignment controls like the normal UI, with Agent-only per-group counts. Deleting a group leaves chats intact (uncategorized) through the existing core API.
- Preserve selected chat state, per-chat draft isolation, new-chat workflow, polling, active runtime display and scroll position. Protect against a stale refresh restoring deleted rows; reset selected chat to another existing Agent chat or new-chat state after deletion. Maintain the selected group filter when appropriate.
- Use a concise destructive confirmation for individual and bulk deletes; distinguish completed chat/Agent data deletion from retained historical Executor audit evidence. Show server errors verbatim enough for users to understand a busy/failed deletion.

## Acceptance criteria

- [ ] A chat can be deleted through its row menu; deleting the current chat leaves a usable view without stale messages/drafts.
- [ ] The bulk manager selects/deselects chats, filters the eligible list, deletes selected/all shown, and never deletes chats outside its intended Agent selection.
- [ ] Search matches relevant Agent metadata; groups create/delete/assign/filter correctly; groups are shared with the normal API but counts reflect Agent chats only.
- [ ] Activity dates and total/filtered chat counts update correctly after new messages, edits, selection, grouping, refresh and deletion.
- [ ] Single and bulk deletion remove normal chat and Agent-owned data, reject active or queued Agent/Executor work, and cannot be bypassed through the standard API's DELETE routes.
- [ ] Deletion is race-safe against Agent execution admission and robust to partial failure/restart. Unrelated chat histories and completed Executor audit history remain intact.
- [ ] Normal Web UI and normal API chat deletion/group behavior do not regress.
- [ ] Pin, replay and attachment support are not introduced by this task.

## Verification and workflow

1. Independent **High-risk plan review** against the current implementation. Report PASS/FAIL/BLOCKED and proposed corrections to the owner before changing this plan. Do not start coding until the plan review is complete; obtain owner approval for substantive revisions of decisions.
2. Implement on branch `agent-chat-management`. Target `Mezhs.Agent.Api`, `Mezhs.Agent.Web`, the normal API deletion composition and the minimum shared Web library components. Update `specifications/current/` only for actual behavior; move this final plan to `specifications/implemented/agent-chat-management/plan.md` on the development branch.
3. Developer verification: type/build checks, Agent Web and normal Web regression suites, core and Agent API smoke/architecture tests, targeted interactive UI verification and persistence/deletion tests.
4. Independent code review before independent final testing. Verify API single/bulk deletes, metadata cleanup, active queued/running/shell conflicts, concurrent enqueue/claim and deletion, restart after incomplete deletion, groups/search/counts, selection/polling races, and compatibility with standard Web.
5. Report exact reviewed commit, evidence, remaining risks and proposed MINOR release bump. **Do not merge implementation into `main` without separate explicit owner approval**; do not update `VERSION` or `CHANGELOG.md` during planning/development.
