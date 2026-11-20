# Versioning

Versioning defines how an approved release receives its
version, changelog entry and tag.

## Release rules

- One completed, owner-approved task produces one release
  version, regardless of its commits, fixes or review cycles.
- Planning-only changes do not create a release.
- Do not modify [VERSION](../VERSION) or
  [CHANGELOG.md](../CHANGELOG.md), or create release tags,
  on development branches.
- Propose a bump type (MAJOR, MINOR or PATCH), but determine
  the actual version from the latest `main` at release time.

Use semantic versioning appropriate to the project.
Normally, compatible features receive MINOR bumps and fixes
receive PATCH bumps. Explicitly consider breaking changes,
especially for pre-1.0 versions.

Product versions are separate from API, package and
configuration schema versions.

## Changelog

Follow the existing [Changelog](../CHANGELOG.md) format.

Add the newest release at the top, after the introduction,
using a version and release date.

Describe the complete released task, not individual commits
or intermediate fixes.

Use relevant sections:

- **Added / Changed / Fixed:** Observable behavior, important
  technical changes and compatibility implications.
- **Verification and approval:** Actual review and testing
  outcomes, relevant evidence or references, and owner approval.
- **Known limitations:** Remaining restrictions, accepted
  risks and meaningful verification gaps.

Omit sections that do not apply.

Keep entries concise, factual and understandable without
reading the task plan.

Do not claim tests passed unless they were run and passed.
Distinguish checks of the final revision from checks of
earlier revisions. Record accepted verification exceptions
rather than silently presenting incomplete testing as PASS.

## Release finalization

1. Read the current version from `main` and determine the
   next version.
2. Update `VERSION` and add one `CHANGELOG.md` entry covering
   the entire approved task.
3. Include the release metadata in the approved merge when
   possible. Otherwise, create one immediate finalization
   commit on `main`.
4. Verify the finalized metadata, then tag the final release
   commit as `v<version>`.

Do not tag or announce a release before finalization succeeds.

If finalization is retried, check whether the release already
exists before making changes. Never bump twice for the same task.
