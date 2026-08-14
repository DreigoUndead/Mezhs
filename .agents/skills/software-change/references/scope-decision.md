# Scope decision: Reuse / Repair / Refactor / Redesign

Use this when the appropriate change size is unclear.

## Reuse

Choose Reuse when the existing abstraction already represents the requirement and the missing behavior is configuration, wiring, invocation, or use of an existing extension point.

Signals:
- a working analogous path already does it;
- a helper/policy/builder/base already owns the invariant but the caller bypassed it;
- no invariant or ownership boundary needs to change.

Failure mode to avoid: reimplementing the mechanism locally.

## Repair

Choose Repair when the current owner and abstraction are correct but contain a defect.

Signals:
- the behavior belongs exactly where it currently lives;
- callers should remain unchanged;
- one implementation detail violates an otherwise-valid invariant;
- a targeted correction restores the intended design.

Failure mode to avoid: introducing a new layer to route around the defect.

## Refactor

Choose Refactor when the requirement fits the product/system, but the current ownership or boundary makes a clean implementation impossible.

Signals:
- multiple callers would need the same workaround;
- state is owned at the wrong lifetime;
- the same policy would have to be duplicated;
- repeated implementations have started to drift or make the same decisions differently;
- a new requirement exposes two responsibilities previously mixed together;
- the local fix requires flags/exceptions that leak internals into callers;
- a stable common base/mechanism has become evident from real repetition.

A refactor should change structure while preserving the intended external contract except where the requirement explicitly changes it.

Failure mode to avoid: calling a workaround "minimal" while permanently creating two models of the same behavior.

## Redesign

Choose Redesign when a foundational assumption must change.

Signals:
- the current domain model cannot express the requirement;
- an invariant previously treated as universal is no longer valid;
- a public contract/protocol/lifecycle must change;
- preserving the current abstraction would make behavior contradictory.

Redesign requires explicit impact analysis: callers, compatibility, migration/state, tests, and rollout.

Failure mode to avoid: smuggling a new system model through scattered conditionals.

## Abstraction timing

Do not choose Refactor merely because code could be made generic.

Prefer evidence:
- repeated mechanics;
- repeated caller obligations;
- real inconsistencies between implementations;
- a clearly shared invariant;
- a stable variation boundary.

The second or third real implementation is often when these facts become visible, but there is no fixed count. Refactor when the shared responsibility is demonstrated, not when reuse is merely imaginable.

## Tie-breaker

When two scopes seem possible, compare them by coherence, not line count:

1. Which leaves one source of truth?
2. Which keeps responsibility with the component that has the necessary information and lifetime?
3. Which requires fewer meaningless decisions at call sites?
4. Which makes misuse harder without hiding meaningful variation?
5. Which removes rather than duplicates superseded behavior?
6. Which changes only assumptions the requirement actually invalidates?
7. Which is simpler to explain after the change?
8. Which is supported by evidence rather than hypothetical future needs?

Prefer the option that leaves the system easier to reason about and harder to accidentally misuse.
