# Dev Journey

## 2026-03-25: Initial Design Plan

**What:** Created the development plan in `docs/dev-plan.md`.

**Why:** Needed a structured roadmap before starting implementation. The plan covers the full architecture — from tile mapping discovery through game state reading, server communication, suggestion UI, auto-play, and polish.

**Key decisions:**
- Fetched the Mahjong server API docs (`localhost:8080/docs`) to understand the exact request/response formats for `suggest-move`, `evaluate-call`, and `validate-move` endpoints.
- Planned 6 phases: tile discovery → game state reader → server client → suggestion UI → auto-play → polish.
- Will use a dedicated file logger (not just Dalamud logs) for tile mapping discovery, to enable AI-assisted iterative debugging.
- Highlight mode (display-only) and auto-play mode (click automation) will be separate toggleable features.
- Created `.github/copilot-instructions.md` for workspace-wide development guidelines (no assumptions, git commits per task, dev journey documentation).
