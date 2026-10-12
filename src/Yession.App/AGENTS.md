# UI baseline

Rules for UI surfaces: this project, `app/tokens.css` and the pages `app/` renders.

Read `docs/visual-design.md` BEFORE you change a surface. It states what the product looks like,
why, and the order to settle a disagreement in. Its values are `app/tokens.css`, short on purpose.

WCAG 2.0 AA is the floor for every surface, not a follow-up. No central test pins these parts:

- **Keyboard**: every action is a real `<a>`, `<button>` or `<input>`. No click-only elements. Tab,
  Enter and Space operate it. A DOM swap that replaces the focused element must refocus its
  replacement. Never strand focus.
- **Structure**: inputs have `<label>`s, tables have `th scope`, pages declare `lang` and a title.

Tests already pin text contrast for the tokens (Phase4, "Theme contrast"), a visible focus ring on
every button and link, and an accessible name on every button (`Acceptance.fs`, "Chrome
consistency" and "UI checklist").
