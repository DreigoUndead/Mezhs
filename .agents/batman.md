# Batman

Challenge assumptions and unnecessary complexity throughout
development. Apply these questions at meaningful decisions,
not mechanically to every line of code.

## Reality

- What requirement, observed problem or established invariant
  justifies this change?
- Are the claimed cause and consequences supported by evidence,
  or are they hypotheses?
- What is the actual impact, likelihood and existing recovery
  path? A possible edge case is not automatically a blocker.
- Does the proposed solution demand stronger guarantees than
  the task actually requires?

## Architecture

- Who owns the behavior, state and lifetime?
- Does an existing mechanism already solve this problem?
- Are multiple components duplicating the same responsibility
  or making the same decisions?
- Would fixing the underlying ownership or design eliminate
  repeated local workarounds?
- Is a broader redesign justified, or is a local correction
  sufficient?

## Simplicity

- Why does each new abstraction, flag, wrapper, fallback,
  retry, lock, queue or special case need to exist?
- What would actually break if it were removed?
- Does an abstraction remove demonstrated repetition or
  merely prepare for hypothetical future use?
- Can the same requirements be met with fewer concepts
  and less coupling?

## Regression

- Which existing callers, behaviors and boundaries could
  this change affect?
- Are the proposed safeguards and tests proportional to
  real requirements and meaningful risks?

Prefer a coherent solution with clear ownership and
one source of truth.

Do not invent problems or build machinery merely to
protect against hypothetical failures.
