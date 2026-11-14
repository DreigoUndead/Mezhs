# Agent status dots: compact hover labels and unread completions

**Level:** Standard. Small cross-layer addition to the existing Agent list projection, plus local UI read tracking and accessible presentation; no changes to execution semantics, authorization, scheduling or database schema.
**Branch:** `agent-status-hover-unread`, based on main `179c0da50a17e7024d76eaa2571cb6ee2498348e`.
**Owner request:** Replace the permanently visible status label next to dots with a hover/focus reveal or tooltip; show a blue dot for an unviewed completed Agent execution until its chat is opened; determine whether normal API Web already has these dots.

## Findings

- Agent Web already displays one status dot with permanent text in sidebar and selected header; it also repeats latest Agent execution status as a header badge.
- Regular API Web (`MezhsChatApp`) has **no execution-status dots**. It lists the connection and last activity, and does not consume the Agent-only batch projection. Adding dots for normal model chats is **not** included in this task.
- The existing batched `/v1/agent-chats` projection returns `executionStatus` but no root execution identity; the UI therefore cannot distinguish a newly completed turn from an already-read previous completion without guessing from unrelated `updatedAt` changes.

## Design

1. Keep the existing batched Agent status query and status precedence. Extend its existing result with the **same selected root Agent execution ID** (`executionStatusId`) on Agent chat list views, not a new query, endpoint, table or status.
2. Keep Agent read markers in browser `localStorage`, keyed by ChatId and latest completed root execution ID. Completion is blue iff the visible status is `Completed` and the ID does not equal the last read ID. Selecting an Agent chat marks its current completed execution read and therefore turns it green; completion while that chat remains selected is read immediately. A subsequent run has a distinct ID and becomes unread if the chat is not selected. Other states retain their existing colors, including paused precedence.
3. A previously completed Agent with no local read marker may initially show blue until opened. Read state is **per browser origin**, not synchronized between devices; no server-side read receipt.
4. Make dots compact by default; reveal status text with CSS hover/focus animation (including parent chat-button keyboard focus), retain an accessible `aria-label` and native `title` tooltip, respect reduced-motion preferences. Avoid nested tabbable controls inside chat buttons. Remove the duplicate static Agent status badge from the selected header; keep actual execution evidence labels on command cards.
5. Do not change regular API Web: it has no Agent execution projection. Document this distinction.
6. Add regression coverage for batch query identity/status pairing, unread/selected/next-run rules, accessible hover-only rendering, old status colors and no N+1. Build Agent UI and .NET solution; run related Agent and UI checks.

## Workflow

- Implement on the task branch; update `specifications/current/agent-runtime.md`, `docs/architecture.md` as needed.
- Move plan to `specifications/implemented/agent-status-hover-unread/plan.md` before independent review.
- Independent code review and targeted verification are required for Standard; owner approval is separate for merge.
- Leave `VERSION`, `CHANGELOG.md` and `main` untouched. A PATCH bump is the tentative release proposal.

## Implementation note

Completed on the task branch. The typed Agent chat list now exposes the root execution ID alongside the status from the existing batch query, so unread tracking does not depend on modification timestamps or add list N+1 requests. Standard independent code review and verification are still required before owner merge approval.

## Independent code review — 2026-11-13

- **Initial verdict: FAIL** at candidate `ab89858f81d71dd351534e1760d8170a40c91371` vs base `179c0da50a17e7024d76eaa2571cb6ee2498348e`; see [PR #56 review](https://github.com/DreigoUndead/Mezhs/pull/56#pullrequestreview-5898615213) and [Core CI run 43514409019](https://github.com/DreigoUndead/Mezhs/actions/runs/43514409019).
- **Finding:** The new PowerShell smoke assertions incorrectly treated the Agent list JSON array as a single pipeline object, producing multiple `executionStatus` and `executionStatusId` values when querying one chat. CI showed two list values and one matching detail value. This was a test collection-shape defect; the CI log did not establish an API status/ID pairing defect.
- **Reviewer fix:** Commit `83755f79e86fe6207649d3a137d2c1fc9e24cb18` on this branch: assign REST list response without an enclosing `@(...)`, enumerate the resulting collection through the filter, and require exactly one matching Agent chat for both initial and subsequent completed root executions. Production code and original status precedence remain unchanged.
- **Continuity:** New independent Code Reviewer must review the updated branch and CI evidence before a PASS. Standard targeted verification follows review PASS; owner merge approval remains separate. No VERSION/CHANGELOG or main changes.
