# Releases

`main` is always the release branch. **All changes—including plans, workflow instructions, documentation, code and tests—must be committed on a task branch, never directly to `main`.** One completed, owner-approved task creates one release version when it is merged into `main`. Planning-only changes do not create a release.

## Before owner approval

The task branch contains the plan from its first commit, subsequent code, tests, and required `specifications/current/` updates. Changes to the workflow itself use the same branch-first rule. Update the project `README.md` only if its features, setup or usage changed.

**Do not edit, bump, pre-allocate or generate `VERSION` or `CHANGELOG.md` on a plan or development branch.** Revisions, fixes, plan reviews and repeated testing are still part of the same task, not new versions.

Provide the owner with the PR, exact revision, evidence, release summary and **suggested bump type** (MAJOR, MINOR or PATCH). Do not claim a final version number until the current `main` version is checked at release time.

## Merge and release

1. **Wait for explicit owner approval** of the completed change. Plan approval, CI and agent PASS are not merge permission.
2. Recheck the PR revision, current `main`, conflicts and verification. Re-run affected review/tests if changes invalidate earlier evidence. If the owner's approved candidate materially changes, get renewed approval.
3. Merge the approved implementation, final plan and any workflow changes into `main` **only as part of the approved release**, and **finalize release metadata as part of that merge operation**: determine the next version from the latest `main`, then write `VERSION` and one `CHANGELOG.md` entry covering the **entire merged task**. Do this **once**, not per commit, fix, iteration or review. Where Git allows, include the metadata in the merge commit; otherwise make one immediate release-finalization commit on `main`. Do not tag or announce a release before finalization succeeds.
4. Verify the release metadata and tag the **final release commit** `v<version>`. If finalization is retried, detect an existing completed release first; **never bump twice for the same task**.
5. Delete the merged development branch and verify it is absent. Do not delete unmerged work. If tools cannot delete directly, use a temporary workflow on the merged branch with `contents: write`. It must first remove and commit its own workflow with `[skip ci]`, then delete the branch using the GitHub REST API and `GITHUB_TOKEN`. Treat HTTP 404 as already deleted and verify absence. Never merge the temporary workflow into `main`.

Use a sensible semantic bump: normally MINOR for compatible features and PATCH for fixes during pre-1.0 development; explicitly address breaking changes. Product version is separate from package/API/config schema numbers.

The agent presenting the completed change can also finalize its release after approval. A separate release agent is not mandatory.
