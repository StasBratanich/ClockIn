# ClockIn — Instructions for Codex

Read `README.md` and `CODING_STANDARDS.md` before changing code. Follow the existing project structure and conventions.

Work on one agreed task at a time. Before editing, inspect the relevant files, identify important edge cases, and briefly explain the intended approach. Ask the developer to decide unresolved product behavior instead of inventing a rule.

Make focused changes and run checks relevant to the files changed. At the end, report what changed, which checks actually ran, and any remaining limitation.

The developer reviews every change and performs Git operations. Do not stage, commit, push, merge, or change branches unless explicitly asked to perform that specific action.

## Orchestration workflow

1. The main Codex agent identifies the next incomplete subtask in `WORK_PLAN.md`, checks its prerequisites, and inspects the current repository state. A `Planned` status is not approval to resolve open decisions; unresolved product or architecture choices must be raised for approval.
2. Once the subtask is ready, the main agent invokes the `clockin_implementer` agent with a bounded, approved task. The implementation agent reads the repository instructions, relevant requirements and decisions, and existing code before editing; it implements only that subtask, runs relevant checks, and reports changed files, decisions, checks/results, and unresolved issues.
3. After implementation, the main agent gathers the exact change set against the pre-implementation baseline, including staged and unstaged changes and the full contents of new files. Read-only Git commands may be used when repository instructions allow them; no Git mutation is delegated or performed.
4. The main agent sends that complete change set together with the subtask requirements to `code_reviewer`. The reviewer remains read-only, uses no Git commands or desktop UI control, and reviews independently of the implementation agent's summary. If the complete change set cannot be obtained, the main agent reports the limitation and asks for what is missing.
5. The main agent performs two `code_reviewer` iterations over the complete subtask diff. Actionable findings are sent back to the implementation agent for focused fixes, and those fixes are reviewed in the next iteration. If any change is made after the second iteration, the final change set is reviewed again before handoff. The main agent keeps the reviewer read-only and preserves the existing reviewer restrictions.
6. The main agent presents the final diff and explanations to the developer for independent review. Only the developer decides when to stage, commit, and push. A subtask is not marked `Done` before that review decision.
