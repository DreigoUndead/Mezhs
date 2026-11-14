# ChatGPT transient read recovery
ID: chatgpt-transient-read-retries
Level: Standard — narrow bug fix in ChatGPT Account read requests, no schema or architecture changes.
Base: main @ 70be3d41a26cbf106e0db8ac0e86c29846ec1a0c
Branch: chatgpt-transient-read-retries

## Observed failure and scope
Recent Agent messages failed with Electron `net::ERR_HTTP2_PROTOCOL_ERROR` while checking account session and while retrieving an already-submitted ChatGPT response. Another message saw `ERR_NETWORK_IO_SUSPENDED`. The observed failure is a transient transport exception escaping a safe read, **not** evidence of why Chromium/network/remote server failed.

Repair the existing ChatGPT Account integration's read path; do not change Agent scheduling, Electron process lifetime, or provider write semantics.

## Approach
- Retry only failed **safe HTTP GET/HEAD reads** after a known transient Chromium network exception, with a small bounded backoff. Treat successful HTTP status, non-retryable HTTP failures and application errors as before.
- Cover authentication session reads and ChatGPT conversation-state polling, including response-body consumption. Keep retries in the ChatGPT integration, which owns method and endpoint semantics. Preserve all existing POST behavior: no automatic prompt resubmission.
- Emit minimal diagnostics: HTTP method, sanitized endpoint path/category, Chromium error code, retry count; never log auth headers, tokens, request bodies, full conversation IDs or response bodies.
- Use deterministic mock-session regression tests for transient failure/recovery, retry exhaustion, no retry for POST/non-transient errors and intact response parsing.

## Acceptance criteria
- [ ] `GET /api/auth/session` and `GET /backend-api/conversation/{id}` recover from one transient error without a new model submission.
- [ ] Repeated transient read failures exhaust bounded attempts and retain the original failure; no endless retries.
- [ ] HTTP 401/404 and non-network failures retain existing behavior.
- [ ] `POST /backend-api/f/conversation` is not retried, even on a matching network error.
- [ ] Logs exclude sensitive endpoint identifiers, credentials and body content.

## Verification and workflow
Implement on this branch, add targeted Node tests, run existing ChatGPT account/inaccessible-conversation regressions and relevant broader tests. Archive the plan to `specifications/implemented/` before an independent Standard code review, then final targeted verification. Do not edit VERSION/CHANGELOG or merge before separate owner approval.

## Risks and limits
No evidence yet distinguishes remote server, network path or Chromium origin of the HTTP/2 failure. Retries mitigate **safe read** interruptions, not the underlying network cause. POST and streaming requests are deliberately excluded from replay.
