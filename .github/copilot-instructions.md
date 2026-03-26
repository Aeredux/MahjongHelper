# Project Guidelines

## Development Workflow

- **No assumptions**: Do not make assumptions about requirements or implementation details. Ask the user clarifying questions when anything is ambiguous or unclear before proceeding.
- **Git commits**: After completing each checkbox task in the dev plan (`docs/dev-plan.md`), create a git commit with a descriptive message summarizing what was done.
- **Dev journey**: Document all changes, decisions, and progress in `docs/dev-journey.md`. Each entry should include what was changed and why.
- **Reverse-engineering ergonomics**: During investigation/reverse-engineering tasks, default to passive instrumentation and automatic logging so the user can keep playing; avoid requiring repeated manual copy/compare actions unless unavoidable.
