# UI baseline

Rules for UI surfaces: this project, `app/tokens.css` and the pages that `app/` renders.
[`AGENTS.md`, "UI baseline"](../../AGENTS.md#ui-baseline) says what to read first and sets the
WCAG floor.

The "Chrome consistency" suite pins labels, `th scope`, `lang` and title
([`tests/AGENTS.md`](../../tests/AGENTS.md#ui-tests-pin-an-invariant-never-a-design)). No central
test pins the keyboard. You hold it:

- Make every action a real `<a>`, `<button>` or `<input>`. Use no click-only element.
- Tab, Enter and Space operate every action.
- When a DOM swap replaces the focused element, focus the replacement. Never strand focus.
