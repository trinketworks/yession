module TemplateHolePositionFixture.Positions

open Lit

/// One hole in every position the rule has an opinion about. A line marked `// YES020` MUST be
/// reported; every other hole here MUST NOT be.

// Admitted: child content and an attribute's value, quoted or not, whole or in part.

let content (s: string) = html $"""<b>{s}</b>"""

let quoted (s: string) = html $"""<b class="{s}">x</b>"""

let partOfAValue (s: string) = html $"""<b class="a {s} b">x</b>"""

let unquoted (s: string) = html $"""<b class={s}>x</b>"""

let aListener (f: string -> unit) = html $"""<button @click={f}>go</button>"""

let aFlag (b: bool) = html $"""<input ?disabled={b} />"""

let aQuotedBrace (s: string) = html $"""<b title="{{ {s} }}">x</b>"""

let textAngle (s: string) = html $"""<b>1 < 2 {s}</b>"""

let afterATag (s: string) = html $"""<b class="c">x</b> {s}"""

// Rejected: a hole where Lit's parse binds no part.

let aName (hook: string) (v: string) = html $"""<b {hook}="{v}">x</b>""" // YES020

let aBareName (hook: string) = html $"""<b class="c" {hook}>x</b>""" // YES020

let aTag (tag: string) = html $"""<{tag}>x</b>""" // YES020
