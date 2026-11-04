# Releases

`main` is always releasable. Each completed **implementation** merge creates a release. Plan-only documentation commits do not bump the version.

Prepare the release files **on the development branch, before independent testing and review**:

- `VERSION`: project release version, in `MAJOR.MINOR.PATCH` format. It is separate from package and API/config schema versions.
- `CHANGELOG.md`: date and user-visible changes.
- `README.md`: accurate front page, usage instructions and current project description.
- `specifications/current/`: update affected behavior specifications **only if needed**; review those edits with the code.
- `specifications/implemented/<id>/`: final plan and verification information.

While pre-1.0, normally bump MINOR for compatible features and PATCH for fixes. Recheck the next version against current `main`.

After independent testing and review, **wait for explicit owner approval**. Then merge and tag `v<version>`.

If generated Codegraph data is missing or stale after the merge, refresh it via a separate reviewed update. Prefer refreshing it on the implementation branch before review.