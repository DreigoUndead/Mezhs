---
name: systematic-debugging
description: Use for bugs, test failures, leaks, races, deadlocks, unexpected state, performance regressions, integration failures, or any technical behavior whose cause is not already proven. Establish root cause from evidence before proposing or implementing a fix.
---

# Systematic Debugging

Find the causal mechanism before changing production behavior. A plausible fix without an evidenced cause is still a guess.

## 1. Define the failure precisely

State:
- observed behavior;
- expected behavior;
- conditions that trigger it;
- what evidence is already known versus assumed.

Read errors, stack traces, logs, dumps, queries, and failing tests completely before interpreting them.

**Sanity check:** Are you investigating the actual failure, or a story you inferred from it?

## 2. Reproduce or bound the failure

Reproduce when possible. If reproduction is intermittent, identify what can be measured to distinguish good and bad runs. Do not compensate for uncertainty with retries, guards, catches, or fallbacks.

**Sanity check:** What observation would prove the current theory wrong?

## 3. Trace backward to the first wrong state

Follow data, ownership, lifetime, ordering, resources, and state transitions backward from the symptom.

Ask repeatedly:
- Where was the bad state first introduced?
- What transition made it possible?
- Which component owns that transition?
- Is the symptom merely where the bad state becomes visible?

For multi-component systems, inspect boundaries and verify what enters and leaves each component rather than guessing where the fault sits.

**Sanity check:** Did you find the source of the invalid state, or only a place where it can be suppressed?

## 4. Compare with a working path

Find the closest successful flow in the same codebase. Compare responsibility, sequence, state, lifetime, configuration, and dependencies.

Differences are evidence. Similarity is not proof.

**Sanity check:** Which concrete difference explains the failure mechanism?

## 5. Form one falsifiable hypothesis

State it explicitly:

> The failure occurs because X, evidenced by Y. If that is correct, changing or measuring Z should produce W.

Test the hypothesis with the smallest diagnostic change or observation that isolates one variable. Do not stack several speculative fixes together.

If it fails, discard or revise the hypothesis instead of preserving it with additional patches.

**Sanity check:** Is the hypothesis supported by evidence, or merely compatible with the symptom?

## 6. Decide whether the cause is local or architectural

Once the cause is known, use `software-change` to choose Reuse / Repair / Refactor / Redesign.

Repeated failed fixes, recurring invalid state in multiple places, or callers repeatedly violating the same lifecycle/invariant can indicate that the architecture owns the bug—not the latest call site.

Do not escalate to redesign merely because debugging is difficult. Escalate when evidence shows the responsibility or invariant is represented incorrectly.

## 7. Fix the cause and verify the mechanism

Prefer a regression test or minimal deterministic reproduction when practical.

Verify both:
- the reported symptom is gone;
- the causal mechanism now behaves correctly.

Then run the relevant wider tests/build/runtime checks for likely regressions.

Do not declare success from compilation alone when the bug was behavioral.

## Stop conditions

Stop and gather more evidence if you catch yourself doing any of these:
- "try this and see" without a hypothesis;
- adding a null check because a value unexpectedly became null;
- adding retry/timeout/catch logic without understanding the failure;
- changing several variables at once;
- fixing the place where the symptom appears while ownership is elsewhere;
- continuing to patch after evidence points to a broken shared invariant.

## Relationship to `software-change`

`systematic-debugging` answers **what is actually wrong and why**.

`software-change` answers **what scope of change makes the system correct and coherent once the cause is understood**.

Use both for non-trivial bugs.
