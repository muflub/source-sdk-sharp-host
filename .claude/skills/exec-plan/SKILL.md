---
name: exec-plan
description: Execute a multi-phase plan file end-to-end in an isolated worktree
---
Given a plan file path as $ARGUMENTS:
1. Create a git worktree named after the plan; never work in the main tree.
2. Read the plan, list every phase and its verification gate as TodoWrite items.
3. Execute phases in order. After each phase: run the full test suite (zero warnings required), then commit with a message naming the phase.
4. Do NOT pause between phases for confirmation.
5. Keep tool output small - pipe build logs to a file and read only failures.
6. When all phases pass, rebase cleanly onto the integration branch, fast-forward, and report reclaimed disk.
