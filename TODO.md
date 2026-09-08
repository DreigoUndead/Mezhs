# TODO

## Agent foundation follow-ups

- [ ] **Process/service isolation:** design and implement one dedicated MEŽS supervisor/launcher that owns starting, stopping, restarting, and detached service lifetime. Do not solve this with `start /b`, ad-hoc PowerShell detachment, or similar shell tricks.
- [ ] **Common SQLite/LogSql storage foundation:** extract/reuse the generic SQLite ownership from `Mezhs.Log.Sql` for main MEŽS API and Agent persistence. Keep generic database path/connection/transaction/migration mechanics separate from `Mezhs.Log.Shared` log-root and notes-file semantics. Migrate the existing main/API/Agent SQLite writers only after that common ownership boundary is clean.
