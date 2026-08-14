# Architectural leverage: standardize without over-abstracting

The purpose of a common base, builder, generator, attribute model, or framework mechanism is not merely to remove duplicate lines. Its strongest value is reducing complexity and reducing the space in which future implementations can be wrong.

## Correctness by construction

Prefer designs where the normal path is naturally the correct path.

Examples of useful leverage:

- callers declare intent while a shared mechanism owns the mechanics;
- one transaction owner enforces commit/rollback policy instead of every caller reproducing it;
- model metadata drives consistent UI/filter/persistence behavior instead of hand-wiring each screen;
- a base abstraction owns a stable loop/lifecycle while subclasses supply only real variation.

A good abstraction removes decisions that callers should not need to make.

Ask:

- What can the caller forget?
- Which of those things should not be caller responsibilities?
- Can the rule be enforced once instead of documented everywhere?
- If the shared implementation is fixed, do all consumers automatically receive the fix?
- Is there one canonical path, or several equivalent ways to do the same thing?

## Reduce degrees of freedom

Every unnecessary choice is a potential inconsistency.

When one invariant has one valid implementation, prefer one canonical mechanism over several equally supported patterns. Provide extension points only for meaningful variation.

This is especially valuable for generated or LLM-assisted code: a narrow correct path leaves less room for accidental invention.

Do not confuse this with making APIs inflexible. Remove choices that have no semantic value; preserve choices that represent real domain or implementation variation.

## Wait for evidence

Do not build a framework because the first implementation could theoretically be reused.

A useful progression is:

1. Implement the first concrete case cleanly.
2. When another real case appears, compare responsibilities and variation.
3. When repeated mechanics, repeated decisions, or inconsistencies become visible, inspect whether a stable common abstraction has emerged.
4. Centralize only the part that is genuinely shared.

The "third time" is a useful human signal because repetition and drift are often obvious by then, but it is not a literal rule. Two cases can be enough when the invariant is obviously identical; ten cases may still not justify one abstraction if the similarity is superficial.

The trigger is **evidence of a common responsibility**, not a count.

## What to centralize

Good candidates:

- lifecycle and ownership rules;
- transaction/cleanup sequences;
- stable algorithm skeletons with small real variation;
- repeated policy decisions;
- serialization/mapping conventions;
- validation or safety invariants;
- generated UI mechanics;
- repeated state transitions.

Weak candidates:

- two pieces of code that only look similar;
- one-off formatting;
- hypothetical future variants;
- abstractions that expose nearly all original details anyway;
- wrappers that reduce line count but not caller responsibility.

## Standardize mechanics, expose variation

A strong common abstraction typically has:

- a stable owner;
- a stable lifecycle or algorithm skeleton;
- a small surface;
- narrow extension points for genuine variation;
- no duplicated source of truth;
- fewer caller obligations than before.

If callers must still understand and reproduce most of the mechanism, the abstraction has little leverage.

## Avoid premature standardization

Premature abstraction creates its own error surface:

- wrong common assumptions become hard to remove;
- unrelated cases become coupled;
- extension points proliferate to compensate;
- future developers work around the framework instead of using it;
- the abstraction becomes more complex than the concrete code it replaced.

When an abstraction needs many flags to make its first few consumers fit, reconsider whether the common concept is real.

## Architectural leverage test

Before introducing or expanding a shared abstraction, answer:

1. What exact responsibility is shared?
2. What invariant should be implemented only once?
3. What repeated decisions disappear from callers?
4. What variation remains, and is it represented explicitly?
5. What evidence shows this abstraction is needed now?
6. Does the abstraction make incorrect use harder?
7. Does it reduce total concepts, or merely move code?
8. Would a future fix apply centrally to all relevant consumers?

If these answers are weak, wait.
