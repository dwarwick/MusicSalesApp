# Claude Instructions

## Git Commits

- Do not assume the user wants changes committed.
- Only run `git commit` when the user explicitly asks for a commit.
- If work is ready but the user has not asked for a commit, leave the changes uncommitted and report the status.

## Tests

- Anytime new functionality is added or existing functionality is modified, add or update tests that help prevent regressions.
- If a change is difficult to test directly, add the closest practical focused regression test and clearly report any remaining manual test coverage.
