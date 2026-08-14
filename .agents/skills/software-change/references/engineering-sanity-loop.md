# Engineering sanity loop

Use these questions as a continuous challenge to a proposed design. They are not commandments and they do not all need to be asked for every line of code. Apply them at meaningful decisions: ownership, state, API shape, lifetime, branching, reuse, abstraction, and extension points.

## Placement

**Is this the right place to do this?**

Put behavior where the necessary information, lifetime, and responsibility naturally live. The point is not layering for its own sake; it is to avoid callers learning internals that should be owned elsewhere.

Warning signs:
- a controller/service/caller must know internal lifecycle details;
- multiple call sites need the same ordering or cleanup sequence;
- a symptom is patched far away from the state that causes it.

## Duplication

**Does some logic already do this?**

Search before adding a second implementation. Duplication is especially dangerous when it duplicates policy, lifecycle, state transitions, validation rules, or transaction semantics.

Do not treat visual similarity alone as proof that code should be unified. Ask whether the implementations represent the same responsibility and invariant.

## Regression

**What could this break?**

Trace shared mechanisms, callers, data contracts, state, lifecycle, concurrency, transactions, persistence, and compatibility as appropriate.

The larger the ownership level of the change, the larger the potential blast radius—but a central fix can still be safer than many local workarounds when the invariant is truly shared.

## Maintenance

**Will this make maintenance harder?**

Count concepts, not only lines. New flags, states, alternate paths, duplicated mechanisms, hidden coupling, and caller-specific exceptions all increase maintenance cost.

A design is suspicious when understanding one behavior requires reading several unrelated places.

## Independence and coupling

**Should this be independent, and from what?**

Independence is useful when it isolates a real responsibility, lifecycle, failure mode, or source of change. It is not useful when it merely creates interfaces/wrappers around stable direct calls.

Prefer meaningful boundaries over abstract boundaries.

## Bloat

**Is this bloated?**

Look for machinery whose only purpose is to support other machinery: wrappers, factories, fallback chains, state flags, null checks, retries, configuration knobs, and abstraction layers without demonstrated variation.

Bloat is often evidence that the problem is being solved at the wrong level.

## Simplification

**How can this be shorter or simpler?**

Shorten by removing concepts, repeated decisions, duplicated state, branches, and ceremony. Do not compress code into clever expressions that hide behavior.

The goal is conceptual compression, not character count.

## Necessity

**Is this check/logic/state really necessary? Why does this code need to exist?**

Every branch, fallback, cache, state field, wrapper, and synchronization mechanism should protect a demonstrated requirement or invariant.

A useful challenge is: what breaks if this line/concept is removed? If the answer is unclear, investigate before keeping it.

## Architecture

**Does the underlying architecture need changes?**

Local complexity can be evidence that ownership, lifetime, or the domain model is wrong. Do not protect an invalid abstraction with caller workarounds merely because changing it is larger work.

Conversely, do not redesign a sound architecture to solve one local defect.

## Elegance

**Is the solution elegant?**

Treat elegance as a convergence of useful properties:

- short enough to understand quickly;
- simple enough to explain directly;
- robust against the real failure modes;
- obvious in ownership and behavior;
- few moving parts;
- one source of truth where possible;
- difficult to misuse;
- no unnecessary recovery paths;
- code proportional to the actual problem.

A useful physical analogy is a thermal cutoff: it is close to the condition it protects, direct, deterministic, independent of unnecessary machinery, and fails toward safety. Software will not always be that simple, but the analogy is useful when evaluating whether layers of coordination are solving a problem that the owning component could enforce directly.

## Correctness basis

**What fact or invariant makes this solution correct?**

Do not accept "it seems to work" as the basis. Name the ownership rule, lifecycle fact, domain constraint, protocol guarantee, or data invariant that the design relies on.

If you cannot state why it is correct, you probably do not understand it well enough to finalize it.

## Architectural leverage

**Can the architecture remove the opportunity to make this mistake?**

When many callers must make the same decision correctly, consider moving that decision to a canonical mechanism, base, builder, generator, declarative model, or owner that can enforce it once.

The strongest reuse does not merely save code. It removes unnecessary decisions from future implementations.

See `architectural-leverage.md`.
