# Clipsy - Rules

## Map (`.../PROJECT_MAP.md`)
- **Open first** at session start. Map is authoritative (edit map on code mismatch).
- **Sync in same commit** when you:
  - Add/rename/move/delete files or folders.
  - Change file roles, update assets, or modify workflow entry points.
- **Format**: Bullet lists. Update the "Heavy files" list if a file crosses ~300 LOC. No narratives.

## Post-Change Workflow
1. Kill running instances.
2. Compile **Release** build.
3. Start the built app.
4. Notify user it is ready for manual testing.

## Git
- Commit per logical task (clean messages).
- Map updates must be in the same commit.
- Push when task is complete.
- No "made with Claude" in commits

## Other

- Comments in the code should be only in REQUIRED places, take up ONE (maximum two) lines and be written in English.
