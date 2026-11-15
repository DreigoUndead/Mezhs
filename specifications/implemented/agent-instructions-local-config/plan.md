# Shared agent instructions and personal configuration copies

ID: `agent-instructions-local-config`
Level: **Standard** — configuration loading and startup prompt changes with regression risk, but no command enforcement or integration scheduling changes.

Status: **Implemented on task branch; independent review and final testing pending.**

Implementation code revision: `ed16fc67f73d8d0f5a1f5fd0e8f0c885646fcd97`.
Developer CI: [MEŽS Core run 43717572046](https://github.com/DreigoUndead/Mezhs/actions/runs/43717572046) PASS (Windows solution build, agent policy regression suite, Agent API/UI, provider and concurrency/restart suites).
Full regression run succeeded before this documentation-only plan move. Independent code review and final testing remain workflow gates. Codegraph regeneration was unavailable without a local checkout/codegraph CLI and remains a verification limitation.

## Goal and scope

- Keep committed `mezhs.yaml`, `agent.yaml`, and new `instructions.yaml` as repository defaults.
- On normal startup, create adjacent Git-ignored `*.local.yaml` copies only when missing and load those copies. Preserve custom local settings across restarts and releases. Do not merge YAML.
- With explicit `--config` or `--mezhs-config`, load the exact requested file, without implicit copying or redirection.
- Keep `instructions.yaml` beside `agent.yaml`, containing a single reusable text entry `mezhs_default`. Policies reference it as `{mezhs_default}`; resolve it at configuration load. A local agent config consumes the adjacent `instructions.local.yaml`, created from the default on first use.
- Resolve `{OS}` using the runtime host OS in the bootstrap prompt.
- Keep instructions concise: MEŽS environment, literal SH/ATTACH protocol and non-executable `<escape>`, available toolset, supported UI, and follow repository workflow during development. Link to repository docs; no manifesto links. Remove redundant instruction text.
- Preserve runtime command enforcement, per-policy connection/model/limits, and completion behavior.
- Agent tool reference (`docs/agent-tools.md`) and supported UI reference (`docs/supported-rich-ui.md`) are synchronized only at the release stage when affected; record this rule once in `.agents/release.md` as agreed.

## Approach

1. Extend the shared config path locator with an opt-in local-copy resolver for the normal Core and Agent host entry points. Resolve paths beside the found default config; don't change relative path resolution in existing loaders.
2. Add `instructions.yaml` (one text ID), reference it from the three default policies, and resolve that ID while decoding policy YAML, with clear errors for unknown/duplicate/unreadable IDs. Leave policies without references valid. No recursion, nesting, conditionals, or general template engine.
3. On Agent startup, provision `instructions.local.yaml` adjacent to the default only for the standard local Agent configuration path. For custom explicit config paths, find companion instructions in the same directory; use `instructions.local.yaml` with `*.local.yaml` agent paths and `instructions.yaml` otherwise.
4. Substitute `{OS}` when composing the startup prompt, not into saved policy settings. Preserve other runtime placeholder replacement, executable command syntax, and policy completion guidance.
5. Remove duplicate SH/ATTACH explanation between policy text and automatic prompt generation while preserving compatibility of existing custom policies as practical. Ensure the runtime `messages` fragments remain scoped to results/corrections/shell details.
6. Update `.gitignore`, config README usage, focused tests, and relevant current behavior specification. Do not update VERSION/CHANGELOG or release-only reference documents on this task branch.
7. During the eventual approved release, create/update `docs/agent-tools.md` and verify `docs/supported-rich-ui.md` only if changed functionality requires it.

## Acceptance criteria

- [ ] Initial default startup creates `mezhs.local.yaml`, `agent.local.yaml`, and `instructions.local.yaml` as applicable, uses them, and doesn't overwrite them later.
- [ ] `*.local.yaml` is ignored by Git, while defaults remain tracked.
- [ ] Core and Agent hosts honor explicit config paths; Agent supports independent Core config path.
- [ ] A local Agent config resolves local instructions; default/custom agents resolve the corresponding sidecar file without changing intended relative paths.
- [ ] All three default policies use `{mezhs_default}` and their effective initial prompts contain expanded instructions with the real OS name and working SH/ATTACH syntax.
- [ ] Incorrect references and malformed instruction files fail with actionable errors; legacy policies without references remain loadable.
- [ ] Existing command execution, attachment handling, completion policy, OS/shell metadata, and policy snapshots are not functionally weakened.
- [ ] Command-parser and Rich UI fixes from other tasks are outside this scope.

## Verification

- Tests: no local files initially; already customized local files; repeated launches; paths with whitespace; explicit --config and --mezhs-config; missing defaults; invalid local YAML; missing `mezhs_default`; duplicate IDs; no-reference policies; OS placeholder substitution; all default policies.
- Run existing config/agent policy tests, Agent API smoke/architecture tests, full relevant .NET builds and any available CI. Test actual prompt content and snapshot generation, not just source text.
- Independent code review before final verification as Standard; independent tester where useful. Report exact tested revision, CI/local evidence, limitations and requested owner merge approval.
- Keep `main` untouched until explicit owner approval of the complete implementation.
