# Sparse configuration overrides

ID: `config-overrides`
Branch: `feature/config-overrides`
Level: **Standard**

## What

Replace implicit full `*.local.yaml` configuration copies with sparse, Git-ignored `*.override.yaml` files for Core, Agent, and shared instructions. Default configuration loading deletes the matching legacy local copy and creates each missing override with one comment. Explicit `--config` and `--mezhs-config` paths remain exact and do not delete or gain implicit personal files.

Apply one shared YAML merge rule to old and new configuration shapes: mappings merge recursively; sequences of mappings with `id` fields merge by ID and may add entries; other sequences and scalar values replace the base value. Remove the obsolete repository-local `*.local.yaml` files without migrating their contents.

VERSION, release changelog entries, deployment, and live provider/browser testing are outside this task.

## Why

Full local copies hide new defaults introduced by later releases and reuse filenames that may conflict with configuration from other environments. Sparse overrides preserve tracked defaults while keeping personal differences isolated.

## Acceptance criteria

- [x] Default Core startup creates and loads `mezhs.override.yaml` without replacing existing content.
- [x] Default Agent startup creates and loads `agent.override.yaml` and `instructions.override.yaml` without replacing existing content.
- [x] A connection override containing only `id`, `name`, and `limits` preserves the base integration fields.
- [x] A connection ID absent from the base configuration is appended as a new integration.
- [x] Agent and instruction configuration use the same recursive merge behavior.
- [x] Explicit configuration paths load exactly the selected files without an adjacent override.
- [x] Comment-only override files leave base configuration unchanged.
- [x] Implicit/default loads delete matching legacy `*.local.yaml` files and `*.override.yaml` is Git-ignored.
- [x] README and current behavior specification describe the new contract.

## How

`ConfigPath` only owns default-versus-explicit path selection. One shared `LoadOverridableYaml` method owns legacy deletion, safe first-start creation of comment-only overrides, parsing, and overlay semantics before the existing typed Core, Agent, and instruction validation runs. Relative paths continue to resolve from the base configuration directory.

The merge stays intentionally small: only ID-bearing object sequences receive item-level merging. This supports connections while avoiding invented identity rules for arbitrary YAML lists.

## Steps

1. **Plan and Development** — define the override contract, implement the shared loader and startup file creation, update callers, tests, ignore rules, README, and current specification.
   - Done when: all acceptance criteria are represented in code and focused regression coverage.
2. **Code Review** — self-review the complete diff and run Codegraph review under the owner's one-go workflow direction.
   - Done when: no unresolved functional findings remain.
3. **Testing** — run automated build, focused configuration/policy regression, architecture checks, and staged diff validation. No live testing is required by owner direction.
   - Done when: required automated checks pass and limitations are recorded.

## Verification

Verified revision `84410756a7128f7de03622778644ba8b13bbd076` before this documentation correction:

- `dotnet build Mezhs.sln -c Release --nologo` — PASS, 0 warnings and 0 errors.
- `tests/policy-decoder.ps1` — PASS, including existing-entry merge, new-entry append, Agent/instruction overrides, comment-only creation, preservation, and explicit paths.
- `tests/architecture.ps1` — PASS.
- `tests/agent-architecture.ps1` — PASS.
- `git diff --check` — PASS.
- `codegraph review` — status `ok`; two reported compatibility diagnostics were false positives on valid YamlDotNet collection calls confirmed by compilation and runtime tests.

No live provider, Electron, or browser testing is required. The build reported existing npm audit findings; no dependencies changed in this task.

Consolidation follow-up based on `bb15add840d0054eea87e27358dcf3c80fc27c7f`:

- Affected Release build — PASS, 0 warnings and 0 errors.
- `tests/policy-decoder.ps1` — PASS with legacy Core, Agent, and instruction deletion exercised through configuration loading.
- Core and Agent architecture regressions — PASS.
- `git diff --check` — PASS.
- `codegraph review` — status `ok`, no functional findings.

## Changes

### 2026-11-23 — One-go workflow override

**Change:** Development, self-review, and automated testing remain in one chat; independent Plan Review, Code Review, and Testing roles are not required. Live testing is excluded.
**Reason:** The owner classified the change as low risk, directed completion in one go, and later explicitly confirmed that no live testing is required.
**Authority:** Owner requests in this task.
**Trade-off:** Review and testing are not independent.

### 2026-11-23 — Plan added after implementation

**Change:** Added this Standard task plan after implementation rather than before it.
**Reason:** The task was initially misclassified as Small, whose workflow permits the request itself to serve as the plan. Shared configuration semantics warranted Standard classification and a plan file.
**Authority:** Owner identified the missing plan and requested workflow compliance.
**Trade-off:** The pre-implementation planning sequence cannot be recreated retroactively; this plan records the actual scope, decisions, and evidence without claiming otherwise.

### 2026-11-23 — Implementation completed

**Change:** Implemented and pushed shared sparse overrides at `84410756a7128f7de03622778644ba8b13bbd076`; deleted the three ignored root `*.local.yaml` files and created comment-only ignored replacements.
**Reason:** Fulfill the accepted configuration override contract.
**Authority:** Owner feature request.
**Trade-off:** Deleted local-file contents were intentionally not migrated and are not recoverable through Git.

### 2026-11-23 — Consolidate override lifecycle ownership

**Change:** Moved legacy `*.local.yaml` deletion and `*.override.yaml` creation into the shared `LoadOverridableYaml` method used by Core, Agent, and instruction configuration. `ConfigPath` now only selects paths and whether the default is overridable.
**Reason:** Keep one owner for reading, migration cleanup, creation, and merging, following the Batman architecture and simplicity checks.
**Authority:** Owner follow-up request.
**Trade-off:** The first implicit/default load permanently deletes the matching legacy full copy without migrating its values; explicit config loads do not delete it.
