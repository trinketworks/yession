# UI baseline

Rules for UI surfaces: this project, `app/tokens.css` and the pages that `app/` renders.
[`AGENTS.md`, "UI baseline"](../../AGENTS.md#ui-baseline) says what to read first and sets the
WCAG floor.

Some of this floor is pinned centrally.
[`tests/AGENTS.md`](../../tests/AGENTS.md#ui-tests-pin-an-invariant-never-a-design) names the
suites and what each pins. You hold the rest:

- Make every action a real `<a>`, `<button>` or `<input>`. Use no click-only element.
- Tab, Enter and Space operate every action.
- When a DOM swap replaces the focused element, focus the replacement. Never strand focus.
