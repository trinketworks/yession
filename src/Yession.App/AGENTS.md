# UI baseline

Rules for UI surfaces: this project, `app/tokens.css` and the pages that `app/` renders.

Read [`docs/visual-design.md`](../../docs/visual-design.md) BEFORE you change a surface. WCAG 2.0
AA is the floor for every surface ([`AGENTS.md`, "UI baseline"](../../AGENTS.md#ui-baseline)).

The "Chrome consistency" suite pins the structure: labels, `th scope`, `lang` and title
([`tests/AGENTS.md`](../../tests/AGENTS.md#ui-tests-pin-an-invariant-never-a-design)). No central
test pins the keyboard. You hold it:

- Make every action a real `<a>`, `<button>` or `<input>`. Use no click-only element.
- Tab, Enter and Space operate every action.
- When a DOM swap replaces the focused element, focus the replacement. Never strand focus.
