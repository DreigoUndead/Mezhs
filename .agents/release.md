# Versioning and release

**Every completed implementation merge into `main` is a release.** Planning-only documentation merges do not advance the product version.

- `VERSION` is the **MEŽS repository release version**. Existing npm package versions and API/config schema versions are separate technical identifiers; do not conflate them with this release number until unified deliberately.
- Semantic format `MAJOR.MINOR.PATCH`. While pre-1.0, compatible features normally bump MINOR; bug fixes/maintenance normally bump PATCH. Flag breaking behavior explicitly. Resolve competing branch version bumps against latest `main` before final review.
- Implementation PR includes updated `VERSION`, `CHANGELOG.md`, root `README.md`, `specifications/current/` and archived plan, with release date and expected version. Testing and review cover this release candidate.
- After Monkey approves the **completed candidate**, merge, verify `main` and create tag `v<version>` on the merge commit. Never tag/claim an unmerged PR as released.
- `CHANGELOG.md` records user-visible additions, changes, fixes, removals and source PR/plan links. `README.md` provides only the current overview and a concise latest-release summary.