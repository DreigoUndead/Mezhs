# API Web UI group deletion choices
ID: api-web-group-delete-choices
Level: High-risk — permanent bulk deletion with a concurrency edge case; API contracts remain unchanged by owner direction.

## Goal and scope
The normal MEŽS API Web group delete action offers **Unassign conversations**, **Delete conversations**, and **Cancel**. Show the affected conversation count and clear consequences. Agent Web's separate group deletion control is unchanged.

## Owner direction and scope limit (2026-11-10)
The owner requires a simple correction using the APIs already in place; do not introduce or redesign endpoints, synchronization mechanisms, cross-store transactions or deletion ownership. This scope does **not** create a guarantee against concurrent group reassignment between the final membership read and chat deletion. Record that remaining destructive race explicitly; do not present the UI recheck as an atomic or server-enforced guarantee.

## Existing ownership and API contract
- Core `GET /v1/chats` enumerates chats, including Agent-backed chats on the Agent host.
- `DELETE /v1/categories/{categoryId}` unassigns all remaining category members and removes the category.
- `DELETE /v1/chats/{chatId}` and bulk `DELETE /v1/chats` delete selected IDs. When routed by the Agent host they retain the established Agent-first/Core-second cleanup and partial-failure behavior.
- There is no group-membership condition on the existing chat DELETE. It is incorrect to claim that concurrent movement of a selected chat to another group is prevented. **No API files are changed.**
- Version, changelog and main remain release-only.

## Implementation and correction behavior
1. Open a native accessible modal dialog with the group name and fresh current count. It offers the three explicit choices.
2. On action, fetch membership again; if the selected set changed, update the count, require a new choice, and issue no DELETE.
3. For **Unassign**, use existing category DELETE only. For **Delete**, use existing single/bulk chat DELETE first, then existing category DELETE. Preserve all ordinary/Agent deletion semantics.
4. Bound dialog requests with a cancellation signal and 12-second timeout. A timed-out write may have committed; on failure, reconcile Core chats and categories. Clear a now-deleted selected chat and disable retries when outcome cannot be verified. Cancel and Escape become available once the bounded operation completes.
5. Use the browser's native `<dialog>.showModal()` for background inertness, tab focus containment, Escape and focus restoration. Keep irreversible deletion and data-retention disclosures.

## Verification
Developer checks on revision `d614c8eea0773eb812a0c40a87867dc461461980`:
- CI run `https://github.com/DreigoUndead/Mezhs/actions/runs/42830601675`: **PASS**, including `dotnet build Mezhs.sln -c Release --nologo`, `pwsh -NoProfile -File tests/agent-web-shared-ui.ps1`, and `node --test tests/agent-web-rich-formatting.test.cjs tests/chat-ui-browser.test.cjs tests/rich-model-ui-rendering.test.cjs tests/rich-model-ui-browser.test.cjs tests/group-delete-browser.test.cjs`.
- Chromium browser regression exercises focus isolation/restoration, lost DELETE response reconciliation, aborted preflight and Escape. Updated regression additionally covers membership changes discovered at preflight; it must be re-run on the final commit.
- Independent code review then independent testing remain required. Ensure test evidence uses the final exact SHA. Neither CI nor developer checks count as independent approval.

## Risks / remaining limitations
- **P1 concurrency race remains:** if a conversation moves out of a group after the last GET and before existing ID-based DELETE, it can be deleted anyway. Existing endpoints cannot make a client-side check atomic. Owner-directed scope excludes an API change; flag this risk in review rather than asserting it is fixed.
- Newly assigned chats after final inventory may be unassigned during category deletion, rather than deleted.
- Group removal may fail after successful chat removal; reconciliation and retry are best-effort, with no cross-store rollback.
- Abort/timeout stops the UI wait but cannot prove the server stopped work; a complete page refresh may be needed following an unverifiable result.
- Never merge to `main` without explicit owner approval.
