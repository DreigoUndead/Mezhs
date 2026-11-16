# Keep typing responsive in long conversations
ID: ui-typing-lag

## Goal and scope
Typing in the MEŽS Agent composer can stall visibly in long chats, especially with rich messages, command evidence, and execution records. Reduce unnecessary synchronous React rendering when only the draft text changes. Also protect shared rich-message rendering in the regular chat UI. Do not change message, submission, polling, scrolling, or agent execution semantics.

## Approach
- Keep the Agent transcript element stable across draft-only updates; invalidate it when transcript data, execution evidence, active statuses, chat selection, or command-action state changes.
- Memoize the shared Markdown/rich-message component so unchanged content and parsed document do not re-render during unrelated parent updates.
- Preserve all existing draft and send ownership and behavior. Avoid a new global draft state mechanism.

## Acceptance criteria
- [ ] Typing in a populated Agent chat does not regenerate the transcript solely because draft text changes.
- [ ] Messages, execution footers, active status labels, shell command actions and history restoration still refresh when their inputs change.
- [ ] Drafts stay isolated across chats and retain text across send/failed send.
- [ ] Normal MEŽS chat continues to render updated rich responses correctly.

## Verification
- Build both frontends (TypeScript + Vite).
- Targeted browser regression for draft transitions, transcript refresh and long conversations.
- Existing rich UI tests and agent status tests where available.
- Independent code review before final testing.

## Risk
The cached transcript must invalidate on every material state change; failing to include a dependency could leave stale buttons or output. Keep the cache scoped to only the transcript, not the composer or sidebar.
