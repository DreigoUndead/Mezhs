# Releases

`main` is always releasable. Every approved implementation merge creates a release. Plan-only commits do not change the version.

Before final review/testing, prepare on the development branch:

- `VERSION`: repository release version (`MAJOR.MINOR.PATCH`), separate from API/schema/package versions.
- `CHANGELOG.md`: relevant user-visible changes.
- `README.md`: update **only if** usage, features, setup or other front-page information changed.
- `specifications/current/`: update **only when** documented behavior changes.
- `specifications/implemented/<id>/`: final plan moved here from `planned/`; not required as a file for Small changes documented only in the PR.
- Test/review evidence: record inspected SHA and results in `verification.md` or the PR.

While pre-1.0, normally bump MINOR for compatible features and PATCH for fixes; treat breaking changes deliberately. Reconcile competing version numbers with current `main`.

## Owner approval and merge

1. Present the complete PR, tested SHA, review and test evidence to the owner. **Wait for explicit approval of this candidate.** A PASS or earlier plan approval is not permission to merge.
2. Before merging, recheck the exact PR head, `main` head, checks and potential conflicts. If code, requirements, dependencies or conflict resolution changed since verification, redo affected review and testing. If the owner's reviewed candidate changed materially, seek **renewed approval**. Even a conflict-free update to `main` may require integration tests if components overlap.
3. Merge only when still approved and valid. Verify `main` and tag its merge commit `v<version>`.
4. **Delete the merged development branch** and verify it is absent. Never delete another contributor's unmerged work.
5. If tooling cannot directly delete branches, use a temporary GitHub Actions workflow **only on the merged branch** with `contents: write`. It must first remove/commit its own workflow file with `[skip ci]`, then delete `git/refs/heads/<branch>` using GitHub REST and `GITHUB_TOKEN`. Treat HTTP 404 as already deleted, verify absence, and never merge that workflow into `main`.

The agent presenting the finished work may execute the release in the **same chat after explicit owner permission**. Use a separate agent only when needed for expertise or context quality. Prefer refreshing generated Codegraph data on the implementation branch before review; if stale after merge, repair it through a separately approved update.
