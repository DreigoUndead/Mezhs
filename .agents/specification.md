# Specification and planning

1. Inspect existing behavior, current specs, architecture and owners before designing a change. Use [Grill Me](skills/grill-me/SKILL.md) for new features or nontrivial architectural changes; one consequential question at a time.
2. Every change gets a `specifications/planned/<id>/plan.md` using [the small template](../specifications/templates/change.md), including purpose, scope, acceptance, affected components, approach and verification. Expand to separate `spec.md` and `tasks.md` only when useful.
3. Submit `plan/<id>` to Monkey. Merge the approved plan into `main` before starting `<id>` implementation.
4. Plans are working designs, **not frozen history**: refine as implementation reveals better solutions. The completed spec documents what was actually delivered; Git records the earlier drafts.
5. Keep `specifications/current/` as the authoritative behavior contract. An implemented change updates current behavior specifications and archives its historical plan.
6. Do not ask the user for facts available in the repository. Do not pretend guesses are requirements. Escalate materially changed feature purpose or trust boundary.