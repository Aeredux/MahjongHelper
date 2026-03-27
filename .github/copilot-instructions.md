# Project Guidelines

## Development Workflow

- **No assumptions**: Do not make assumptions about requirements or implementation details. Ask the user clarifying questions when anything is ambiguous or unclear before proceeding.
- **Git commits**: After completing each checkbox task in the dev plan (`docs/dev-plan.md`), create a git commit with a descriptive message summarizing what was done.
- **Dev journey**: Document all changes, decisions, and progress in `docs/dev-journey.md`. Each entry should include what was changed and why.
- **Reverse-engineering ergonomics**: During investigation/reverse-engineering tasks, default to passive instrumentation and automatic logging so the user can keep playing; avoid requiring repeated manual copy/compare actions unless unavoidable.
- **Self-serve debugging**: When debugging runtime behavior (server responses, game state values, UI readings), always log diagnostic data to files in `%APPDATA%/MahjongHelper/` so you can read the results yourself. Do not ask the user to read values from the in-game plugin UI — the plugin reloads automatically on rebuild, so file logging is the reliable feedback loop.
