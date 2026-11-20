# SH per-command parameters

ID: sh-command-parameters
Branch: feature/sh-command-parameters
Level: Standard

## What

Support optional positive integer attributes on root shell commands, for example `<SH timeout=60 limit=500>command</SH>`. `timeout` is seconds; `limit` is the maximum number of command-output characters passed into the agent's follow-up prompt. Both may be omitted independently. Plain `<SH>` remains unchanged.

The requested timeout must not exceed the effective policy's `commandTimeoutSeconds`. The existing 16,000-character command-result prompt cap remains in force even if `limit` is larger. Executor history keeps the full command output. Neither attribute changes the shell body or policy command validation.

## Why

Allow an agent to select a shorter execution timeout and smaller follow-up result for individual shell commands without changing policy or losing persisted diagnostic evidence.

## Acceptance criteria

- [ ] Unquoted positive decimal attributes `timeout` and `limit` are recognized on root `SH`; existing commands are unchanged.
- [ ] Invalid, duplicate, unsupported or nonpositive parameters reject the batch before shell execution.
- [ ] A requested timeout above the effective policy's maximum rejects the batch; individual accepted timeouts reach Executor.
- [ ] Requested result limits truncate only the model follow-up output, with clear truncation notice; durable shell execution results remain intact.
- [ ] Commands inside inert rich UI scopes remain inert, and source spans/command bodies are preserved.
- [ ] Parser, execution and result-prompt regressions are covered by tests and reference instructions are updated.

## How

Extend the canonical rich-tag registry and command candidate to carry validated SH attributes to the Agent Parser; restrict bare numeric attribute syntax to SH. The Agent Interpreter validates the timeout against the immutable effective policy and passes it through the existing Shell/Executor execution context. Apply per-result output limit only in AgentPromptBuilder, which already enforces the global 16,000-character cap. Do not introduce a second parser, a new Executor setting or a persistence migration.

## Steps

1. **Plan** — Record scope and limits on a task branch.
2. **Development** — Implement parser → interpreter → shell/result-prompt flow, targeted tests and protocol documentation.
3. **Code Review (independent)** — Verify authority, fail-closed parsing, timeout enforcement, output history and simplicity.
4. **Testing** — Run parser, Agent protocol and relevant project tests, including live Executor checks where available.
5. **Deploy** — Obtain owner approval before merging to main.

## Verification

Run `dotnet run --project tests/Mezhs.RichUi.TestApp`, `pwsh -File tests/agent-protocol-result.ps1` and `pwsh -File tests/policy-decoder.ps1` on the configured Windows host. Inspect durable Executor records for per-command timeout and untruncated output; verify malformed batches produce no shell records.

## Changes

### 2026-11-20 — Initial interpretation

**Change:** Treat `timeout` as seconds and `limit` as follow-up prompt output characters, with policy and global caps.
**Reason:** This keeps the example useful without granting additional execution authority or deleting durable output.
**Authority:** Implementation assumptions for the owner's feature request; subject to owner correction.

### 2026-11-20 — Development implementation

**Change:** The canonical rich parser carries SH timeout/limit attribute values to Agent commands; Interpreter checks the policy timeout cap and Shell passes an accepted timeout to Executor; AgentPromptBuilder truncates only follow-up output. Updated default instruction and shell-context messages and added parser, render-grammar and prompt-result regressions, plus a live rejection case for over-policy timeouts.
**Reason:** Keep a single syntax/authority path and retain full durable Executor results.
**Verification:** Source and patch checks completed via GitHub connector. Tests were added but not executed: this session has no Windows/.NET repository test host. Independent Code Review and live Executor verification are pending.

### 2026-11-20 - Local developer verification and integration coverage

**Change:** Corrected a local variable shadowing compile error in RichDocumentParser and added live Agent protocol checks for accepted per-command timeout and prompt output limit.
**Reason:** The first Release compilation found CS0136. Added positive-path integration coverage for previously unverified shell execution, Executor timeout and prompt-only truncation.
**Verification:** .NET 10 Agent API Release build passed with 0 warnings and 0 errors; Rich UI parser 101/101 passed; tests/policy-decoder.ps1 passed; tests/agent-protocol-result.ps1 passed including rejected over-policy batches with no side effects, timeout=2 limit=5 preserving full durable output while truncating model prompt, and timeout=1 yielding a TimedOut Executor record. Ran on Windows PowerShell 5.1 in an isolated worktree.
**Remaining:** Independent Code Review, subsequent Testing role and explicit owner merge approval are pending.

### 2026-11-20 — Independent Code Review (step 3)

**Outcome:** PASS for implementation revision `0c8ec2c8f7ef49f2324dc31bc93e536a6ca83fcc`; no implementation changes required.
**Review:** Checked the complete PR diff, parser and rich-tag traversal, policy validation, Shell/Executor result handling, AgentWorker recovery path, and regression coverage. SH attributes are parsed by the shared parser; invalid options fail the batch before execution; timeouts are bounded by the effective policy before shell dispatch; prompt-only output truncation does not alter Executor history. No confirmed blocking defect or unjustified architecture was found.
**Verification:** Independent static source/diff review. The prior developer's Windows/.NET test results are recorded above; this reviewer did not independently rerun those tests.
**Remaining:** Independent Testing (step 4), followed by owner-approved Deploy (step 5). Do not merge without owner approval.
