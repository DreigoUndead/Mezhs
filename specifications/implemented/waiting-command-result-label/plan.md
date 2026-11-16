# Integration admission waiting labels
ID: waiting-command-result-label
Level: Standard — extends an existing UI-copy-only change to carry transient admission metadata across the Core/Agent API and Agent Web UI.

## Goal and scope
- Show **Waiting** only when MEŽS actually delays a model send for integration capacity or configured start spacing.
- On capacity blocking, show `Waiting for integration slot (N/N)…` where N is that connection's positive `maxParallel`. All N slots are occupied when the existing nonblocking semaphore admission attempt fails. The numbers are occupied slots / allowed slots, **not** queue position.
- On spacing blocking, show `Waiting for integration start interval…`.
- Once admitted, show **Running** while provider work proceeds, even if the provider reports `waiting`, thinking, preparation, submission or response retrieval.
- On non-admission command-result continuation, use a neutral label without implying throttling.
- Preserve orange Waiting dot only for genuine admission block; no new persisted status, scheduler, or concurrency behavior.

## Approach
1. Reuse `BrowserIntegrationBase` existing admission reason callbacks and `MessageService` transient per-message admission state. Surface read-only reason and capacity only for the active model message associated with the latest running Agent execution.
2. Append optional `admissionWaitReason` and `admissionMaxParallel` to `AgentChatView`; keep backward compatibility and do not alter the global `Running` execution state.
3. Agent Web uses these fields to render a specific busy label only when the selected chat's effective status is `Waiting`; model activity labels take priority otherwise.
4. Add targeted Agent API capacity/spacing and provider-isolation assertions, plus a UI test for status and label precedence.

## Verification
- Inspect the exact diff for changes outside the wait-state projection.
- Agent admission projection integration test: real capacity and spacing waiting, details on list and single-chat, clearing on admission, provider waiting not throttled.
- Real-browser Agent status/label regression and Agent Web TypeScript build via CI.
- No version or changelog changes before explicit owner approval of merging the completed branch.
