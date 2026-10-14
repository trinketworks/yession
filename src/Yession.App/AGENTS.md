# UI baseline

Rules for UI surfaces: this project, `app/tokens.css` and the pages `app/` renders.

Read `docs/visual-design.md` BEFORE you change a surface. It states what the product looks like,
why, and the order to settle a disagreement in. Its values are `app/tokens.css`, short on purpose.

WCAG 2.0 AA is the floor for every surface, not a follow-up. "Chrome consistency" pins the
structure (labels, `th scope`, `lang`, title). No central test pins the keyboard: every action is a
real `<a>`, `<button>` or `<input>`, with no click-only elements. Tab, Enter and Space operate it.
A DOM swap that replaces the focused element must refocus its replacement. Never strand focus.
