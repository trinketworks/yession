module PageScriptFixture.Typed

open Microsoft.Playwright

/// A page driven through calls that take selectors, keys and URLs — none of them JavaScript.
let drive (page: IPage) =
    task {
        let! _ = page.GotoAsync "http://localhost/"
        do! page.ClickAsync "[data-send]"
        do! page.Keyboard.PressAsync "Enter"
        let! count = page.Locator("[data-row]").CountAsync ()
        let! text = page.Locator("[data-row]").First.TextContentAsync ()
        return count, text
    }
