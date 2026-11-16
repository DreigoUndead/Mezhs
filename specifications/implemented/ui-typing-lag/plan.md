# Keep typing responsive in long conversations
ID: ui-typing-lag

## Goal and scope
Typing in the MEŽS Agent composer can stall visibly in long chats, especially with rich messages, command evidence, and execution records. Reduce unnecessary synchronous React rendering when only the draft text changes. Also protect shared rich-message rendering in the regular chat UI. Do not change message, submission, polling, scrolling, or agent execution semantics.

## Approach
- Keep the Agent transcript element stable across draft-only updates; invalidate it when transcript data, execution evidence, active statuses, chat selection, or command-action state changes.
- Memoize the ordinary chat's transcript viewport so draft-only updates do not rebuild unchanged rich messages.
- Preserve all existing draft and send ownership and behavior. Avoid a new global draft state mechanism.

## Acceptance criteria
- [x] Typing in a populated Agent chat does not regenerate the transcript solely because draft text changes.
- [x] Messages, execution footers, active status labels, shell command actions and history restoration still refresh when their inputs change.
- [x] Drafts stay isolated across chats and retain text across send/failed send.
- [x] Normal MEŽS chat continues to render updated rich responses correctly.

## Verification
- Build both frontends (TypeScript + Vite).
- Targeted browser regression for draft transitions, transcript refresh and long conversations.
- Existing rich UI tests and agent status tests where available.
- Independent code review before final testing.

## Risk
The cached transcript must invalidate on every material state change; failing to include a dependency could leave stale buttons or output. Keep the cache scoped to only the transcript, not the composer or sidebar.

## Implementation notes
- The Agent UI memoizes its transcript and execution-history JSX at stable React-state boundaries, invalidating on message, execution, status, selection and shell-action changes. Unchanged chat-list polling preserves the memoized transcript.
- The ordinary chat UI memoizes the conversation viewport so changing only the draft does not rebuild message markup. Draft state and send semantics are not modified.
- The Chromium Agent-status browser test now instruments the real transcript component during a long-chat typing sequence, checks it remains stable, and checks incoming messages invalidate it.
- Existing browser draft/scroll tests are intended to run unchanged. No new import or dependency edge requires Codegraph regeneration.

## Verification status
- Developer static diff inspection complete; CI/browser execution and independent review pending.
- No version, changelog or main-branch changes.
