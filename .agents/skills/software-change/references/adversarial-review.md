# Adversarial self-review

Review the proposed change as if it came from an unfamiliar contributor.

## Requirement
- Does every behavior change trace back to an actual requirement or necessary consequence?
- Did the solution quietly broaden scope?
- What fact or invariant makes the solution correct?

## Placement and ownership
- Is this the right place for the behavior?
- Does the owner have the information and lifetime needed to enforce it?
- Is any caller forced to understand internals it previously did not need to know?

## Duplication and standardization
- Is there now more than one way to do the same thing?
- Did we duplicate policy, lifecycle, mapping, state transitions, validation, or transaction rules?
- Are multiple callers still required to remember the same sequence?
- Could the architecture remove an unnecessary caller decision?
- If a common abstraction was added, what real repetition or inconsistency justified it?
- Are we centralizing a stable responsibility or merely similar-looking code?

## State and lifecycle
- Is there one source of truth?
- Are ownership, release, transaction, cancellation, and disposal rules still consistent?
- Can async/concurrent paths observe invalid intermediate state?

## Complexity and elegance
- Which new classes, flags, branches, wrappers, parameters, state fields, and fallbacks are strictly necessary?
- Why does each new concept need to exist?
- Can any be deleted without weakening correctness or architecture?
- Did the abstraction reduce caller responsibility, or only move lines?
- Did obsolete code remain after the new mechanism replaced it?
- Is there a shorter, simpler, more direct solution that remains robust and obvious?

## Independence and coupling
- Did the change introduce coupling between things that have independent lifetimes or reasons to change?
- Did it introduce interfaces/layers that do not isolate a real responsibility?
- Are extension points limited to actual variation?

## Regression surface
- Which callers share the changed mechanism?
- Which edge paths use a different lifetime or transaction boundary?
- Which existing tests prove those paths, and what remains untested?
- What could this break outside the immediate symptom?

## Final challenge

Try to describe a simpler coherent design. Then try to describe a more centralized design that makes misuse impossible. Compare both against the current implementation.

Do not prefer either automatically. The right answer may be local, or it may require moving responsibility into the architecture. Choose the one that is supported by real invariants and demonstrated reuse, not by attachment to your own implementation.
