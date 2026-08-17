---
name: software-change
description: Use for non-trivial implementation, bug fixing, refactoring, architecture work, or code review when existing design matters. Continuously challenge placement, duplication, regression risk, necessity, complexity, abstraction timing, maintainability, interface choice, async usage, and architectural fit; then choose reuse, repair, refactor, or redesign. Skip simple syntax/lookups with no codebase context.
---

# Software Change

Make the requested behavior correct while keeping the system internally coherent. Do not optimize for the smallest diff, the largest cleanup, or the most abstract design.

## Continuous sanity loop

At meaningful design decisions, challenge the current solution:

- Is this the right place for this responsibility?
- Does another mechanism already own or duplicate it?
- Is there a semantic API, protocol, transport, command, or state mechanism that already owns this behavior?
- Am I reaching for HTML/DOM because it is necessary, or merely because the behavior is visible there?
- Does this operation actually require async?
- What could this break?
- Will this make maintenance harder?
- Should these parts depend on each other?
- Is this bloated?
- Can it be shorter or simpler without hiding behavior?
- Is this branch/check/state actually necessary?
- Does the underlying architecture need to change instead?
- Can the architecture remove a decision callers should not have to make?
- What invariant makes this solution correct?
- Is it elegant: simple, direct, robust, obvious, and hard to misuse?

These are questions, not rules. Their purpose is to challenge assumptions, not force every solution toward minimalism or abstraction.

Read `references/engineering-sanity-loop.md` when the design is non-obvious.

## Choose the right scope

Classify the change before settling on an implementation:

- **Reuse** — the existing mechanism already supports the requirement.
- **Repair** — the owner and abstraction are correct, but implementation is wrong.
- **Refactor** — ownership, lifetime, boundary, or common responsibility must change to support the requirement cleanly.
- **Redesign** — a foundational invariant, public model, or system assumption must change.

Choose the **smallest coherent scope**, not the smallest diff.

Read `references/scope-decision.md` when the scope is unclear.

## Workflow

### 1. Establish the actual requirement

Identify observable behavior, real constraints, and what must remain unchanged. Do not silently solve a broader problem you inferred.

**Sanity check:** Are you solving the user's requirement or your own imagined version of it?

### 2. Build a system model from evidence

Read the relevant source when available. Find the current owner, callers, state/source of truth, lifetime, transactions/concurrency, analogous implementations, and existing helpers/builders/policies/generators/base abstractions.

For integrations with external applications or web UIs, inspect the actual semantic interface first: existing API calls, network requests, protocol messages, transport contracts, commands, or application state transitions. Do not infer the mechanism from visible HTML when the underlying behavior can be observed directly.

Use domain-specific project guidance for system facts instead of substituting generic patterns.

**Sanity check:** Did you find the owner of the behavior, or only the place where the symptom appears?

### 3. State the invariant and classify scope

Explain in plain language what must always remain true. Then choose Reuse / Repair / Refactor / Redesign.

If local code needs awkward exceptions, duplicated state, or caller-specific knowledge, consider whether that is evidence of a wrong boundary rather than a reason to add more checks.

**Sanity check:** Are you fixing the responsible architecture or bypassing it?

### 4. Decide whether to centralize or wait

Do not abstract a first occurrence merely because reuse is imaginable.

Repeated mechanics, repeated caller decisions, or inconsistencies are evidence that a stable common responsibility may have emerged. A second or third real implementation is often where this becomes visible, but the count is not the rule.

Centralize when the shared responsibility is real. The goal is not DRY alone: it is fewer concepts, fewer repeated decisions, and fewer opportunities to implement an invariant incorrectly.

Read `references/architectural-leverage.md`.

**Sanity check:** Are you standardizing demonstrated behavior or hypothetical future behavior?

### 5. Design and implement one coherent mechanism

Prefer the existing owner when it remains correct. Change the owner when the requirement proves it wrong.

Avoid second implementations, second sources of truth, speculative fallbacks, unnecessary defensive checks, abstractions that only move lines, and caller APIs that require everyone to remember the same ceremony.

When refactoring, remove or simplify behavior made obsolete by the new design.

**Sanity check:** After the change, is there one obvious place to understand the behavior?

### 6. Simplify and verify

Once the design is correct, remove unnecessary branches, state, wrappers, ceremony, and coupling. Prefer conceptual simplification over clever terseness.

Then verify the actual requirement with the strongest available evidence: tests, build, reproduction, logs, call-site inspection, database behavior, or runtime behavior.

For bugs, use the repo-local `systematic-debugging` skill to establish the failure mechanism before changing code.

**Sanity check:** Did simplification preserve clarity, and did verification exercise the real failure/requirement rather than only compilation?

### 7. Adversarially review your own solution

Treat it as somebody else's code. Use `references/adversarial-review.md`.

Specifically challenge:

- invented assumptions;
- duplicate mechanisms/state;
- scope that is too small or too large;
- premature abstraction;
- missed opportunities to enforce a shared invariant centrally;
- unnecessary DOM/UI automation where a semantic interface exists;
- unnecessary async/await where no asynchronous work or control flow requires it;
- obsolete code left behind;
- regression paths;
- whether a simpler, more elegant design exists.

If the review changes the scope classification, revise the design instead of patching the patch.

## Interface selection rule

Prefer semantic interfaces over presentation surfaces.

If the application already exposes the behavior through an API, network protocol/request, transport contract, command, or stable state mechanism, use that mechanism rather than reproducing it through HTML/DOM interaction.

HTML/DOM automation is a **last resort**. Use it only when evidence shows that no suitable non-UI mechanism exists or when the requested behavior is inherently UI-only. A visible button, link, label, or page flow is evidence that the feature exists, not evidence that clicking or scraping it is the correct integration boundary.

Before adding DOM selectors, click sequences, arbitrary sleeps, or assumptions about page state, inspect the real request/state transition performed by the application. Prefer reproducing that semantic operation directly.

When DOM automation is genuinely unavoidable, keep it isolated at the provider/UI boundary, minimize selectors and timing assumptions, and do not let presentation details become a second source of truth for application behavior.

## Async discipline

Use async only for genuinely asynchronous work or when an asynchronous contract requires it. Do not add `async` merely because a caller is async, because a task-returning API exists, or to make neighboring signatures uniform.

Keep pure parsing, validation, mapping, state inspection, and other synchronous operations synchronous. If a method only forwards an existing `Task` and needs no `await`-dependent control flow, return the task directly rather than creating another async state machine.

Async should express a real wait/lifetime boundary, not become a default coding style.

## Architectural leverage rule

When many callers must obey one invariant, ask whether the system can enforce it once. Strong abstractions reduce degrees of freedom: callers declare intent or supply real variation while the canonical mechanism owns the mechanics.

Do not confuse this with "make a base class." Standardize only stable responsibilities demonstrated by real use.

## Elegance rule

Treat elegance as an engineering signal: few moving parts, clear ownership, little duplicated state, direct behavior, safe failure, obvious invariants, and code proportional to the problem. Shorter is useful when it removes concepts, not when it merely compresses syntax.

## Restraint rule

Do not "make the code better" as an independent goal. Keep attacking the solution until every remaining piece has a reason to exist and sits where it naturally belongs. Sometimes that is three lines; sometimes it is the common base that only became justified after several real implementations exposed the shared responsibility.
