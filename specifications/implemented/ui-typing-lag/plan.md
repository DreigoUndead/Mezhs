# Keep typing responsive in long conversations
ID: ui-typing-lag

## Goal and scope
Typing in the MEŽS Agent composer can stall visibly in long chats with rich messages, command evidence, and execution history. Prevent character input from rerendering the large application and transcript. Apply the same isolation to the ordinary MEŽS chat. Preserve message, send, pending-request, poll, replay, scrolling and Agent execution semantics.

## Approach (revised after owner simplicity review)
- Move unsent per-chat draft values into a store scoped to the mounted app. Only the composer input and send control subscribe to draft changes. The Agent and ordinary chat application components read the current draft only when submitting.
- Retain the established clear/move/restore rules: distinct drafts per chat and new-chat context; preserve edits made while a send is in flight; restore failed sends without overwriting newer input.
- Restore ordinary transcript and execution-history JSX rendering, removing the three large manual useMemo wrappers and their invalidation dependency lists from the initial implementation.
- Avoid replacing the Agent chat list on unchanged periodic polling responses; active messages and execution state still refresh on actual changes.
- Prefer isolating frequent state changes over memoizing large, inline JSX trees. Do not change the rich renderer or scroll revision logic without evidence that it is a remaining bottleneck.

## Acceptance criteria
- [ ] Typing in a populated Agent chat does not rerender the Agent application or full transcript for draft-only updates.
- [ ] Unchanged Agent chat-list polling does not redraw history; real message/status/evidence changes remain visible.
- [ ] Drafts and pending state are isolated across chats, including new-chat transitions, submission, async handoff and failure restoration.
- [ ] Ordinary chat composer and its send-button enabled state update without propagating character-level renders through the full chat app.
- [ ] Rich content, execution actions, scrolling and replay remain correct.

## Verification
- Build both frontends (TypeScript + Vite) in Windows CI.
- Run Chromium Agent long-chat typing/poll/message-update regression and existing browser draft/scroll/history tests.
- Run the existing rich UI, Agent and Core checks.
- Independent code review of the revised solution before final testing, with an independent tester when valuable.

## Risk / rationale
The original useMemo approach made transcript invalidation depend on long, manually maintained state lists. The revised design removes those caches. Draft subscriptions introduce a small app-scoped store, but avoid app-level rerenders and retain the existing draft ownership behavior. Watch for lost updates, enabled-state regressions, per-chat isolation, and asynchronous send races.

## Implementation notes
- The shared draft utility now offers a per-app store with per-key subscriptions and preserves the original useConversationDrafts hook interface for existing callers/tests.
- The Agent UI uses the shared DraftChatComposer and reads the latest draft at submit time.
- Ordinary chat subscribes its textarea and send button separately, and reads the store when sending.
- Both UIs' transcript JSX and the Agent execution-history JSX are again rendered normally; no new transcript cache is required.
- Agent periodic chat-list refresh skips identical inventories, reducing unrelated whole-app rerenders.
- Browser tests measure real transcript renders and (in this revision) application renders, not elapsed typing latency.

## Verification status
- Revised implementation committed on task branch; Windows CI and independent review pending.
- Direct measurement of latency in the running user's MEŽS app is not available from this environment.
- No version, changelog, merge or main-branch changes.
