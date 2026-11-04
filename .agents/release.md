# Releases

`main` is always releasable. Each completed **implementation** merge is a release. Planning documentation committed to `main` does not bump the version.

Prepare these **on the development branch before testing/review**:

- `VERSION`: `MAJOR.MINOR.PATCH` repository release version (separate from API/schema or package versions).
- `CHANGELOG.md`: release date and user-visible additions/changes/fixes.
- `README.md`: accurate project front page and usage instructions.
- `specifications/current/`: update behavior contracts only when relevant.
- `specifications/implemented/<id>/`: final plan and verification information.

Normally bump MINOR for compatible features or PATCH for fixes before 1.0. Resolve competing version bumps against the latest `main`.

## Merge and cleanup

1. **Wait for the owner's explicit approval** of the tested and reviewed PR. Never infer it from a plan approval, a green check or a reviewer PASS.
2. Reconfirm the PR head and checks. Merge to `main` and verify the resulting release, then tag the merge commit `v<version>`.
3. **Delete the merged development branch** and verify it is gone. Do not delete unmerged branches or another contributor's work.
4. If the available GitHub connector cannot delete branches, use a temporary GitHub Actions workflow **only on the merged branch** with `contents: write`. The workflow first removes and commits its own workflow file using `[skip ci]`, then deletes `git/refs/heads/<branch>` through GitHub REST using `GITHUB_TOKEN`. Treat HTTP 404 as already deleted. Verify the branch is absent afterward. Never merge this temporary workflow into `main`.
5. Prefer regenerating Codegraph on the implementation branch before review. If it remains stale after merge, fix it through a separate reviewed update, not an unapproved direct change to `main`.