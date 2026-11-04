# Releases

`main` is always releasable. Every completed **implementation** merge is a new release. Planning documents committed to `main` do not change the product version.

Before requesting owner approval, prepare these files on the development branch:

- `VERSION` — project release version in `MAJOR.MINOR.PATCH` format (not necessarily the same as API or package schema versions).
- `CHANGELOG.md` — date and user-visible additions, changes and fixes.
- `README.md` — current project description and version.
- `specifications/current/` — the resulting behavior.
- `specifications/implemented/<id>/` — final plan and verification results.

Use MINOR for compatible features and PATCH for fixes while developing toward 1.0. Review breaking changes carefully.

After testing and code review pass, **wait for the project owner's explicit approval**. Then merge, confirm `main`, and tag `v<version>`. If `main` changed, reconcile the version and rerun affected checks before approval/merge.