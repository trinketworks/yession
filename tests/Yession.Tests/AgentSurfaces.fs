module Yession.Tests.AgentSurfaces

// An agent's A2UI surface as the App holds it: in the synced state, so in the doc every peer
// shares; answered through the spec's two-way binding (`EditSurfaceMsg`); and drawn in the dock
// (`SurfaceView`). The agent's side writes a surface with `show_surface` (`SurfaceTool`), whose
// cases run it against a doc; the rest write one the way a peer's update would arrive: straight
// onto the doc, or straight into the model.

open Fable.Pyxpecto
open Yjs
open A2ui
open Yession.Domain.Agent
open Yession.Domain.Chat
open Yession.Domain.Collab
open Yession.App
open Yession.App.Collab
open Yession.Tests.Support
open Yession.Peer

let private plain (id: string) (kind: ComponentKind) : Component =
    { Id = id; Accessibility = None; Weight = None; Kind = kind }

/// The use the surfaces are for: the agent asks which of some options people want, and a
/// submit that waits until at least one is chosen.
let private choiceComponents : Component list =
    let option value = { Label = Literal value; Value = value }
    let chosen = Call { Function = Required (Bound "/choice"); ReturnType = None }
    [ plain "root" (ComponentKind.Column (Static [ "question"; "choice"; "submit" ], None, None))
      plain "question" (ComponentKind.Text (Literal "Which branches should I rebase?", None))
      plain
          "choice"
          (ComponentKind.ChoicePicker (
              None,
              Some ChoiceVariant.MultipleSelection,
              [ option "main"; option "dev" ],
              Bound "/choice",
              None,
              None,
              []
          ))
      plain "submitLabel" (ComponentKind.Text (Literal "Rebase", None))
      plain
          "submit"
          (ComponentKind.Button (
              "submitLabel",
              Some ButtonVariant.Primary,
              Action.Event ("submit", [ "choice", Bound "/choice" ]),
              [ { Condition = chosen; Message = "Pick at least one branch." } ]
          )) ]

let private surfaceOf (components: Component list) (data: (string * Json) list) : Surface =
    { CatalogId = Codec.BasicCatalogId
      Theme = None
      SendDataModel = false
      Components = components |> List.map (fun c -> c.Id, c) |> Map.ofList
      DataModel = JObject data }

let private choiceSurface (data: (string * Json) list) : Surface = surfaceOf choiceComponents data

/// A surface entry written onto a doc by hand, as the agent's side will write one.
let private drawOnto (doc: Y.Doc) (surfaceId: string) (components: Component list) : unit =
    let entry : Y.Map<obj> = Y.Map.Create ()
    (doc.getMap "surfaces" : Y.Map<obj>).set (surfaceId, box entry) |> ignore
    entry.set ("catalog", box Codec.BasicCatalogId) |> ignore
    entry.set ("components", box (SyncedStateSync.componentsText (components |> List.map (fun c -> c.Id, c) |> Map.ofList))) |> ignore

let private syncBoth (a: Y.Doc) (b: Y.Doc) =
    Y.applyUpdate (b, Y.encodeStateAsUpdate a)
    Y.applyUpdate (a, Y.encodeStateAsUpdate b)

let private branches (names: string list) : Json = JArray (names |> List.map JString)

let private syncTests =
    testList "Doc" [
        testCase "an agent's surface reaches a peer's model through the doc" <| fun () ->
            let doc = Y.Doc.Create ()
            let p = Harness.run (Client.makeProgram Client.Ports.offline doc (ClientModel.init (peer "ada" "Ada")))
            drawOnto doc "ask" choiceComponents
            Expect.equal (p.Model ()).Synced.Surfaces (Map.ofList [ "ask", choiceSurface [] ]) "the model holds the surface the doc says"

        testCase "an answer reaches the doc as the surface's data" <| fun () ->
            let doc = Y.Doc.Create ()
            let p = Harness.run (Client.makeProgram Client.Ports.offline doc (ClientModel.init (peer "ada" "Ada")))
            drawOnto doc "ask" choiceComponents
            p.Dispatch (user (EditSurfaceMsg ("ask", "/choice", branches [ "main" ])))
            Expect.equal
                ((SyncedStateSync.ofDoc doc).Surfaces |> Map.tryFind "ask" |> Option.map (fun s -> s.DataModel))
                (Some (JObject [ "choice", branches [ "main" ] ]))
                "a fresh read of the doc carries the answer"

        // The reason the data model is a root of its own, keyed per field: one surface asked of
        // two people is answered by both of them.
        testCase "two people answering different fields of one surface both keep their answers" <| fun () ->
            let docA = Y.Doc.Create ()
            let docB = Y.Doc.Create ()
            let ada = Harness.run (Client.makeProgram Client.Ports.offline docA (ClientModel.init (peer "ada" "Ada")))
            let ivy = Harness.run (Client.makeProgram Client.Ports.offline docB (ClientModel.init (peer "ivy" "Ivy")))
            drawOnto docA "ask" choiceComponents
            syncBoth docA docB
            ada.Dispatch (user (EditSurfaceMsg ("ask", "/choice", branches [ "dev" ])))
            ivy.Dispatch (user (EditSurfaceMsg ("ask", "/note", JString "after lunch")))
            syncBoth docA docB
            Expect.equal
                ((SyncedStateSync.ofDoc docA).Surfaces |> Map.tryFind "ask" |> Option.map (fun s -> s.DataModel))
                (Some (JObject [ "choice", branches [ "dev" ]; "note", JString "after lunch" ]))
                "both fields, merged"

        testCase "a surface whose components do not decode is left out" <| fun () ->
            let doc = Y.Doc.Create ()
            let entry : Y.Map<obj> = Y.Map.Create ()
            (doc.getMap "surfaces" : Y.Map<obj>).set ("garbled", box entry) |> ignore
            entry.set ("catalog", box Codec.BasicCatalogId) |> ignore
            entry.set ("components", box "[{\"id\":\"root\",\"component\":\"NoSuchThing\"}]") |> ignore
            Expect.equal (SyncedStateSync.ofDoc doc).Surfaces Map.empty "nothing to draw, and nothing else lost"

        testCase "an answer to a surface that is gone is left out" <| fun () ->
            let doc = Y.Doc.Create ()
            (doc.getMap "surfaceData" : Y.Map<obj>).set (SyncedStateSync.SurfaceDataKey.make "gone" "choice", box "[\"main\"]") |> ignore
            Expect.equal (SyncedStateSync.ofDoc doc).Surfaces Map.empty "no surface conjured from its data"

        // The key's field is a JSON pointer token, so a field with a slash in it still splits
        // from the surface it belongs to, whatever the surface is called.
        testCase "a data key splits back into its surface and field" <| fun () ->
            Expect.equal
                (SyncedStateSync.SurfaceDataKey.parse (SyncedStateSync.SurfaceDataKey.make "deploy/ask" "a/b~c"))
                (Some ("deploy/ask", "a/b~c"))
                "the surface and the field, as they were"
    ]

let private withSurface (surface: Surface) : ClientModel =
    let init = ClientModel.init (peer "ada" "Ada")
    { init with Synced = { init.Synced with Surfaces = Map.ofList [ "ask", surface ] } }

let private modelTests =
    testList "Model" [
        testCase "an answer to a surface that is gone changes nothing" <| fun () ->
            let model = withSurface (choiceSurface [])
            Expect.equal
                (step (EditSurfaceMsg ("gone", "/choice", branches [ "main" ])) model).Synced
                model.Synced
                "nowhere to put it"
    ]

/// The opening tag of the element carrying `hook`, so an assertion reads that element's own
/// attributes rather than the page's.
let private tagWith (hook: string) (html: string) : string =
    match html.IndexOf hook with
    | -1 -> failwithf "no element carries %s" hook
    | at ->
        let start = html.LastIndexOf ('<', at)
        html.Substring (start, html.IndexOf ('>', at) - start + 1)

let private viewTests =
    testList "View" [
        testCase "an agent's surface is drawn" <| fun () ->
            Expect.stringContains (render (withSurface (choiceSurface []))) "data-surface=\"ask\"" "the surface is on the page"

        testCase "every option of a choice is a control" <| fun () ->
            let page = render (withSurface (choiceSurface []))
            let options = [ "main"; "dev" ] |> List.map (fun v -> tagWith (sprintf "data-surface-choice=\"%s\"" v) page)
            Expect.isTrue (options |> List.forall (fun tag -> tag.StartsWith "<button")) "each option is a button"

        testCase "a chosen option says it is chosen" <| fun () ->
            let page = render (withSurface (choiceSurface [ "choice", branches [ "dev" ] ]))
            Expect.stringContains (tagWith "data-surface-choice=\"dev\"" page) "aria-checked=\"true\"" "dev is checked"

        testCase "a button whose checks fail is not offered as ready" <| fun () ->
            let page = render (withSurface (choiceSurface []))
            Expect.stringContains (tagWith "data-surface-component=\"submit\"" page) "aria-disabled=\"true\"" "nothing chosen yet"

        testCase "a button whose checks pass is ready" <| fun () ->
            let page = render (withSurface (choiceSurface [ "choice", branches [ "main" ] ]))
            Expect.stringContains (tagWith "data-surface-component=\"submit\"" page) "aria-disabled=\"false\"" "one chosen"

        testCase "a component this app does not draw is said rather than dropped" <| fun () ->
            let image = plain "root" (ComponentKind.Image (Literal "https://example.com/a.png", None, None, None))
            Expect.stringContains
                (render (withSurface (surfaceOf [ image ] [])))
                "data-surface-unsupported=\"Image\""
                "the hole is named"

        // The launch card's `Sent`, for a surface: the pressed button holds and says so in its
        // own box, and nothing else on the surface takes a press until the session answers.
        testCase "a button whose answer is on its way holds, and the rest of the surface stands still" <| fun () ->
            let model = withSurface (choiceSurface [ "choice", branches [ "main" ] ])
            let sent = Yession.Domain.Link.PressSurfaceButton ("ask", "submit", "")
            let page = render { model with Asked = Map.ofList [ Yession.Domain.RequestId.fresh (), sent ] }
            let submit = tagWith "data-surface-component=\"submit\"" page
            Expect.stringContains submit "aria-busy=\"true\"" "the press holds"
            Expect.stringContains submit "aria-disabled=\"false\"" "held, not waiting"
            Expect.stringContains page Dom.Text.surfaceSending "it says the answer is on its way"
            Expect.stringContains (tagWith "data-surface-choice=\"dev\"" page) "aria-disabled=\"true\"" "a choice takes no press meanwhile"
            Expect.stringContains (tagWith "data-surface-dismiss" page) "aria-disabled=\"true\"" "nor does the way out"

        testCase "a surface with nothing on its way holds nothing" <| fun () ->
            let page = render (withSurface (choiceSurface [ "choice", branches [ "main" ] ]))
            Expect.stringContains (tagWith "data-surface-component=\"submit\"" page) "aria-busy=\"false\"" "nothing held"
            Expect.stringContains (tagWith "data-surface-choice=\"dev\"" page) "aria-disabled=\"false\"" "choices take a press"

        testCase "a dismissal on its way holds the way out" <| fun () ->
            let model = withSurface (choiceSurface [ "choice", branches [ "main" ] ])
            let page = render { model with Asked = Map.ofList [ Yession.Domain.RequestId.fresh (), Yession.Domain.Link.DismissSurface "ask" ] }
            Expect.stringContains (tagWith "data-surface-dismiss" page) "aria-busy=\"true\"" "the × holds"
            Expect.stringContains (tagWith "data-surface-component=\"submit\"" page) "aria-disabled=\"true\"" "and the answer waits"

        // An empty required field is not answered yet, which the button already says by
        // waiting; only an answer can be wrong.
        testCase "a field says what is wrong only once it holds an answer" <| fun () ->
            let field =
                plain "root" (ComponentKind.TextField (Literal "Ticket", Some (Bound "/ticket"), None, Some "^\\d{5}$", []))
            let empty = render (withSurface (surfaceOf [ field ] []))
            Expect.isFalse (empty.Contains "does not match") "nothing wrong yet"
            Expect.isFalse (empty.Contains "aria-invalid=\"true\"") "and not marked invalid"
            let wrong = render (withSurface (surfaceOf [ field ] [ "ticket", JString "12" ]))
            Expect.stringContains wrong "does not match" "a wrong answer says so"
            Expect.stringContains wrong "aria-invalid=\"true\"" "and is marked invalid"

        // The band is the card, so a card at the root draws its child in its place.
        testCase "a card at the root is drawn as the surface itself" <| fun () ->
            let components =
                [ plain "root" (ComponentKind.Card "body")
                  plain "body" (ComponentKind.Text (Literal "Ready?", None)) ]
            let page = render (withSurface (surfaceOf components []))
            Expect.stringContains page "Ready?" "its content is drawn"
            Expect.isFalse (page.Contains "data-surface-component=\"root\"") "with no box of its own"

        // As the card's START is: the question can scroll past the band's cap, its answer cannot.
        testCase "a column ending in a row of buttons pins that row under what scrolls" <| fun () ->
            let components =
                [ plain "root" (ComponentKind.Column (Static [ "question"; "actions" ], None, None))
                  plain "question" (ComponentKind.Text (Literal "Ready?", None))
                  plain "actions" (ComponentKind.Row (Static [ "go" ], None, None))
                  plain "go" (ComponentKind.Button ("goLabel", None, Action.Event ("go", []), []))
                  plain "goLabel" (ComponentKind.Text (Literal "Go", None)) ]
            let page = render (withSurface (surfaceOf components []))
            let pinned = page.IndexOf "data-surface-actions"
            Expect.isTrue (pinned > page.IndexOf "Ready?") "the actions follow the question"
            Expect.isTrue (pinned < page.IndexOf "data-surface-component=\"go\"") "and hold the button"

        testCase "a column ending in anything but buttons pins nothing" <| fun () ->
            Expect.isFalse ((render (withSurface (choiceSurface []))).Contains "data-surface-actions") "a lone button is content"

        testCase "a surface whose root has not arrived is not drawn" <| fun () ->
            let rootless = choiceComponents |> List.filter (fun c -> c.Id <> "root")
            Expect.isFalse
                ((render (withSurface (surfaceOf rootless []))).Contains "data-surface=\"ask\"")
                "nothing to answer yet"
    ]

/// One component of every kind the catalog has, to hold the tool's list against the renderer.
let private everyKind : ComponentKind list =
    let text = Literal "x"
    [ ComponentKind.Text (text, None)
      ComponentKind.Image (text, None, None, None)
      ComponentKind.Icon (IconSource.Named "home")
      ComponentKind.Video text
      ComponentKind.AudioPlayer (text, None)
      ComponentKind.Row (Static [], None, None)
      ComponentKind.Column (Static [], None, None)
      ComponentKind.List (Static [], None, None)
      ComponentKind.Card "c"
      ComponentKind.Tabs []
      ComponentKind.Modal ("a", "b")
      ComponentKind.Divider None
      ComponentKind.Button ("c", None, Action.Event ("e", []), [])
      ComponentKind.TextField (text, None, None, None, [])
      ComponentKind.CheckBox (text, Literal false, [])
      ComponentKind.ChoicePicker (None, None, [], Literal [], None, None, [])
      ComponentKind.Slider (None, None, 1.0, Literal 0.0, [])
      ComponentKind.DateTimeInput (text, None, None, None, None, None, []) ]

/// What the agent's call answers, run against a doc.
let private show (doc: Y.Doc) (messages: string) : Async<string> =
    async {
        let _, body = Yession.Session.SurfaceTool.tool (SyncedStateSync.applySurfaceMessages doc)
        match! body (sprintf "{\"messages\":%s}" messages) with
        | Ok answer -> return answer.Text
        | Error e -> return failwithf "the call did not happen: %s" e
    }

let private message (body: string) : string = sprintf "{\"version\":\"v0.9.1\",%s}" body

let private create (id: string) : string =
    message (sprintf "\"createSurface\":{\"surfaceId\":\"%s\",\"catalogId\":\"%s\"}" id Codec.BasicCatalogId)

let private surfacesIn (doc: Y.Doc) = (SyncedStateSync.ofDoc doc).Surfaces

let private toolTests =
    testList "Tool" [
        testCase "the tool lists exactly the components the app draws" <| fun () ->
            Expect.equal
                (everyKind |> List.filter SurfaceView.draws |> List.map SurfaceView.kindName |> List.sort)
                (List.sort Yession.Session.SurfaceTool.drawnNames)
                "the description and the refusal go by the same answer"

        // The example is what an agent copies, so it has to be one the session takes.
        testCaseAsync "the choice example in the description puts a question in front of everybody" <| async {
            let doc = Y.Doc.Create ()
            let ada = Harness.run (Client.makeProgram Client.Ports.offline doc (ClientModel.init (peer "ada" "Ada")))
            let! said = show doc Yession.Session.SurfaceTool.example
            Expect.stringContains said "applied 3 messages" "all three applied"
            let page = render (ada.Model ())
            Expect.stringContains page "data-surface=\"pick\"" "a peer draws it"
            Expect.stringContains (tagWith "data-surface-choice=\"release/2.4\"" page) "aria-checked=\"false\"" "with its choices, none taken"
            Expect.stringContains (tagWith "data-surface-component=\"go\"" page) "aria-disabled=\"true\"" "and the button waits for one"
        }

        testCaseAsync "a component the app does not draw is refused, and nothing is written" <| async {
            let doc = Y.Doc.Create ()
            let image = message "\"updateComponents\":{\"surfaceId\":\"s\",\"components\":[{\"id\":\"root\",\"component\":\"Image\",\"url\":\"https://example.com/a.png\"}]}"
            let! said = show doc (sprintf "[%s,%s]" (create "s") image)
            Expect.stringContains said "REFUSED" "refused"
            Expect.stringContains said "Image (root)" "naming the component"
            Expect.stringContains said "ChoicePicker" "and what it may use instead"
            Expect.equal (surfacesIn doc) Map.empty "the createSurface before it did not land either"
        }

        testCaseAsync "a message that does not apply refuses the whole call" <| async {
            let doc = Y.Doc.Create ()
            let elsewhere = message "\"deleteSurface\":{\"surfaceId\":\"other\"}"
            let! said = show doc (sprintf "[%s,%s]" (create "s") elsewhere)
            Expect.stringContains said "there is no surface 'other'" "says which surface is missing"
            Expect.equal (surfacesIn doc) Map.empty "and wrote nothing"
        }

        testCaseAsync "something that is not an A2UI message is refused by its place in the list" <| async {
            let doc = Y.Doc.Create ()
            let! said = show doc (sprintf "[%s,{\"hello\":1}]" (create "s"))
            Expect.stringContains said "message 2 is not an A2UI v0.9.1 message" "names the second"
        }

        // The agent revises a question people are already answering: it moves the field it
        // names, and the answers it did not name stay where people put them.
        testCaseAsync "the agent's data update keeps the answers it did not name" <| async {
            let doc = Y.Doc.Create ()
            let ada = Harness.run (Client.makeProgram Client.Ports.offline doc (ClientModel.init (peer "ada" "Ada")))
            let! _ = show doc Yession.Session.SurfaceTool.example
            ada.Dispatch (user (EditSurfaceMsg ("pick", "/note", JString "after lunch")))
            let! _ = show doc (sprintf "[%s]" (message "\"updateDataModel\":{\"surfaceId\":\"pick\",\"path\":\"/choice\",\"value\":[\"main\"]}"))
            Expect.equal
                (surfacesIn doc |> Map.tryFind "pick" |> Option.map (fun s -> s.DataModel))
                (Some (JObject [ "choice", branches [ "main" ]; "note", JString "after lunch" ]))
                "both, in the doc everyone reads"
        }

        testCaseAsync "deleteSurface takes the surface and its answers out of the doc" <| async {
            let doc = Y.Doc.Create ()
            let! _ = show doc Yession.Session.SurfaceTool.example
            let! said = show doc (sprintf "[%s]" (message "\"deleteSurface\":{\"surfaceId\":\"pick\"}"))
            Expect.stringContains said "applied 1 message." "applied"
            Expect.equal (surfacesIn doc) Map.empty "no surface"
            Expect.isFalse
                ((doc.getMap "surfaceData" : Y.Map<obj>).has (SyncedStateSync.SurfaceDataKey.make "pick" "choice"))
                "and no answer left behind"
        }
    ]

let private ada = Yession.Domain.Principal.Peer (Yession.Domain.PeerId.create "ada" |> expect)
let private messageId = Yession.Domain.MessageId.create "m1" |> expect

/// What the session records when Ada presses `go` on the example's surface.
let private answered : Yession.Domain.Surfaces.SurfaceActionTaken =
    { MessageId = messageId
      SurfaceId = "pick"
      Component = "go"
      Name = "submit"
      Context = "{\"choice\":[\"main\"]}"
      Responses = [ "main" ]
      By = ada }

/// A form whose options read differently from their values, with a field and a box beside the
/// choice, and two buttons: one that sends all three, and one that sends nothing.
let private formSurface (data: (string * Json) list) : Surface =
    surfaceOf
        [ plain
              "choice"
              (ComponentKind.ChoicePicker (
                  None,
                  Some ChoiceVariant.MultipleSelection,
                  [ { Label = Literal "Guard regex"; Value = "regex" }; { Label = Literal "Draw chips"; Value = "chips" } ],
                  Bound "/choice",
                  None,
                  None,
                  []
              ))
          plain "note" (ComponentKind.TextField (Literal "Why", Some (Bound "/note"), None, None, []))
          plain "urgent" (ComponentKind.CheckBox (Literal "Do it today", Bound "/urgent", []))
          plain "sendLabel" (ComponentKind.Text (Literal "Send", None))
          plain
              "send"
              (ComponentKind.Button (
                  "sendLabel",
                  None,
                  Action.Event ("send", [ "choice", Bound "/choice"; "note", Bound "/note"; "urgent", Bound "/urgent"; "kind", Literal (JString "form") ]),
                  []
              ))
          plain "skipLabel" (ComponentKind.Text (Literal "Not now", None))
          plain "skip" (ComponentKind.Button ("skipLabel", None, Action.Event ("skip", []), [])) ]
        data

let private projectionTests =
    testList "Text projection" [
        testCase "an answer reads as the labels the person saw, the field by its label, and a ticked box by its" <| fun () ->
            let surface = formSurface [ "choice", branches [ "regex"; "chips" ]; "note", JString "both slow"; "urgent", JBool true ]
            Expect.equal
                (Project.responses (Map.ofList [ "f", surface ]) "f" "send" "")
                [ "Guard regex"; "Draw chips"; "Why: both slow"; "Do it today" ]
                "no values, no JSON, and the agent's own literal is not an answer"

        testCase "an empty field and an unticked box answer nothing" <| fun () ->
            let surface = formSurface [ "choice", branches [ "regex" ]; "note", JString ""; "urgent", JBool false ]
            Expect.equal (Project.responses (Map.ofList [ "f", surface ]) "f" "send" "") [ "Guard regex" ] "only the choice"

        testCase "a button that carries no answer answers with its caption" <| fun () ->
            Expect.equal (Project.responses (Map.ofList [ "f", formSurface [] ]) "f" "skip" "") [ "Not now" ] "what was pressed"

        testCase "a value no input holds reads as itself" <| fun () ->
            let row = plain "approve" (ComponentKind.Button ("label", None, Action.Event ("approve", [ "name", Bound "name" ]), []))
            let surface = surfaceOf [ row ] [ "items", JArray [ JObject [ "name", JString "a" ]; JObject [ "name", JString "b" ] ] ]
            Expect.equal (Project.responses (Map.ofList [ "l", surface ]) "l" "approve" "/items/1") [ "b" ] "the item's name, as text"

        testCase "one answer is on the line after the verb" <| fun () ->
            Expect.equal
                (Act.deed (Act.SurfaceActionTaken answered) |> Yession.Domain.Phrase.said)
                "responded main"
                "the answer itself"

        testCase "several answers are counted on the line and listed under it" <| fun () ->
            let many = { answered with Responses = [ "Guard regex"; "Draw chips" ] }
            Expect.equal (Act.deed (Act.SurfaceActionTaken many) |> Yession.Domain.Phrase.said) "responded with 2 answers" "counted"
            Expect.equal
                (Act.particulars (Act.SurfaceActionTaken many) |> List.map Yession.Domain.Phrase.said)
                [ "Guard regex"; "Draw chips"; "event submit on surface pick with context {\"choice\":[\"main\"]}" ]
                "each answer, then what the agent acts on"

        testCase "the timeline unfolds several answers as a list, and keeps them off the line" <| fun () ->
            let many = { answered with Responses = [ "Guard regex"; "Draw chips" ] }
            let item : ConversationItem =
                { MessageId = messageId
                  Author = Yession.Domain.Principal.toActor ada
                  Content = ItemContent.Act (Act.SurfaceActionTaken many)
                  Status = ConversationItemStatus.Complete
                  Offset = Yession.Domain.EventOffset.create 1L |> expect
                  Woke = None
                  CausedBy = None }
            let model = ClientModel.init (peer "ada" "Ada")
            let model =
                { model with
                    Link = SessionLink.Current
                    Session = Some (Yession.Domain.SessionId.create "demo-session" |> expect)
                    Conversation = { model.Conversation with Recent = [ item ] } }
            let line = Support.render model
            Expect.stringContains line "with 2 answers" "the line counts them"
            Expect.isFalse (line.Contains "data-surface-responses") "and the list waits in the fold"
            let opened = Support.behindFold (FoldKey.Act messageId) model
            let list = opened.Substring (opened.IndexOf "data-surface-responses")
            Expect.stringContains list "Guard regex" "the first answer"
            Expect.stringContains list "Draw chips" "and the second"
    ]

let private pressTests =
    testList "Press" [
        testCase "a button whose checks fail sends nothing, and says why" <| fun () ->
            Expect.equal
                (Surfaces.press (Map.ofList [ "ask", choiceSurface [] ]) "ask" "submit" "")
                (Error "Pick at least one branch.")
                "the check's own message"

        testCase "a ready button sends its event with the context read now" <| fun () ->
            Expect.equal
                (Surfaces.press (Map.ofList [ "ask", choiceSurface [ "choice", branches [ "dev" ] ] ]) "ask" "submit" "")
                (Ok ("submit", [ "choice", branches [ "dev" ] ]))
                "the name and what the picker holds"

        testCase "something that is not a button is not pressed" <| fun () ->
            Expect.isError (Surfaces.press (Map.ofList [ "ask", choiceSurface [] ]) "ask" "question" "") "a Text"

        testCase "a button on a surface that is gone is not pressed" <| fun () ->
            Expect.equal
                (Surfaces.press Map.empty "ask" "submit" "")
                (Error "there is no surface 'ask'")
                "the surface named"

        // A button stamped from a template reads its context under the item it was drawn for,
        // which is how one surface lists several things to approve, each with its own button.
        testCase "a button in a template reads relative paths under its item" <| fun () ->
            let row =
                plain "approve" (ComponentKind.Button ("label", None, Action.Event ("approve", [ "name", Bound "name" ]), []))
            let surface =
                surfaceOf [ row ] [ "items", JArray [ JObject [ "name", JString "a" ]; JObject [ "name", JString "b" ] ] ]
            Expect.equal
                (Surfaces.press (Map.ofList [ "list", surface ]) "list" "approve" "/items/1")
                (Ok ("approve", [ "name", JString "b" ]))
                "the second item's name"

        testCaseAsync "the session records the answer as the doc holds it" <| async {
            let doc = Y.Doc.Create ()
            let! _ = show doc Yession.Session.SurfaceTool.example
            let p = Harness.run (Client.makeProgram Client.Ports.offline doc (ClientModel.init (peer "ada" "Ada")))
            p.Dispatch (user (EditSurfaceMsg ("pick", "/choice", branches [ "main" ])))
            Expect.equal
                (Yession.Session.SurfaceTool.answer (surfacesIn doc) messageId ada "pick" "go" "")
                (Ok answered)
                "the example's button, carrying the choice as JSON"
        }

        testCase "an answer owes the agent a turn, as whoever pressed" <| fun () ->
            Expect.equal
                (AgentWake.pendingReason [ Yession.Domain.SessionEvent.SurfaceActionTaken answered ])
                (Some (SurfaceAnswered "pick", ada))
                "owed to Ada"

        testCase "an answer is the agent's to read in the timeline" <| fun () ->
            Expect.equal
                (Act.sentence (Act.SurfaceActionTaken answered) |> Yession.Domain.Phrase.said)
                "responded main (event submit on surface pick with context {\"choice\":[\"main\"]})"
                "the answer as people read it, then what the agent acts on"

        testCase "an answer on the wire is the shape it will always be" <| fun () ->
            let pinned =
                """{"type":"surfaceActionTaken","payload":{"messageId":"m1","surfaceId":"pick","component":"go","name":"submit","context":"{\"choice\":[\"main\"]}","responses":["main"],"by":{"kind":"peer","peerId":"ada"}}}"""
            Expect.equal
                (Yession.Codecs.Codec.fromString Yession.App.Codecs.Events.sessionEvent pinned)
                (Ok (Yession.Domain.SessionEvent.SurfaceActionTaken answered))
                "the durable form decodes to the event"

        // Recorded before the event kept its responses. The surface is long gone, so there are
        // no labels; the context's values are the next best thing, and still not JSON.
        testCase "an answer recorded before responses reads as its values" <| fun () ->
            let older =
                """{"type":"surfaceActionTaken","payload":{"messageId":"m1","surfaceId":"pick","component":"go","name":"submit","context":"{\"choice\":[\"regex\"]}","by":{"kind":"peer","peerId":"ada"}}}"""
            match Yession.Codecs.Codec.fromString Yession.App.Codecs.Events.sessionEvent older with
            | Ok (Yession.Domain.SessionEvent.SurfaceActionTaken a) -> Expect.equal a.Responses [ "regex" ] "the value, as text"
            | other -> failwithf "expected an answer, got %A" other

        testCase "a turn woken by an answer round-trips" <| fun () ->
            let started =
                Yession.Domain.SessionEvent.AgentTurnStarted
                    { AgentTurnId = Yession.Domain.AgentTurnId.create "t1" |> expect
                      Cause = TurnCause.Woke (SurfaceAnswered "pick") }
            let codec = Yession.App.Codecs.Events.sessionEvent
            Expect.equal
                (Yession.Codecs.Codec.fromString codec (Yession.Codecs.Codec.toString codec started))
                (Ok started)
                "round-trip"

        testCase "a press round-trips as a command" <| fun () ->
            let codec = Yession.App.Codecs.Frames.session Yession.Codecs.Codec.string
            let frame =
                Yession.Domain.Link.Command (
                    Yession.Domain.Link.Request (
                        Yession.Domain.RequestId.fresh (),
                        Yession.Domain.Link.PressSurfaceButton ("pick", "go", "/items/1")))
            Expect.equal
                (Yession.Codecs.Codec.fromString codec (Yession.Codecs.Codec.toString codec frame))
                (Ok frame)
                "round-trip"

        testCase "pressing a ready button asks the session" <| fun () ->
            let model = withSurface (choiceSurface [ "choice", branches [ "main" ] ])
            Expect.equal
                (ClientModel.update (PressSurfaceMsg ("ask", "submit", "")) model |> snd)
                [ ClientEffect.PressSurface ("ask", "submit", "") ]
                "one request, naming the button"
    ]

/// What the session records when Ada dismisses the example's surface.
let private dismissed : Yession.Domain.Surfaces.SurfaceDismissed =
    { MessageId = messageId
      SurfaceId = "pick"
      Title = Some "Which branch should I rebase onto?"
      By = ada }

let private dismissTests =
    testList "Dismiss" [
        testCase "every surface offers a dismiss, whatever buttons the agent drew" <| fun () ->
            let page = render (withSurface (surfaceOf [ plain "root" (ComponentKind.Text (Literal "No buttons here", None)) ] []))
            Expect.isTrue ((tagWith Dom.Hooks.surfaceDismiss page).StartsWith "<button") "a control of the app's own"

        testCase "dismissing asks the session" <| fun () ->
            Expect.equal
                (ClientModel.update (DismissSurfaceMsg "ask") (withSurface (choiceSurface [])) |> snd)
                [ ClientEffect.DismissSurface "ask" ]
                "one request, naming the surface"

        testCase "a dismissal round-trips as a command" <| fun () ->
            let codec = Yession.App.Codecs.Frames.session Yession.Codecs.Codec.string
            let frame =
                Yession.Domain.Link.Command (
                    Yession.Domain.Link.Request (Yession.Domain.RequestId.fresh (), Yession.Domain.Link.DismissSurface "pick"))
            Expect.equal
                (Yession.Codecs.Codec.fromString codec (Yession.Codecs.Codec.toString codec frame))
                (Ok frame)
                "round-trip"

        testCase "a surface's title is its first line of text, not a button's caption" <| fun () ->
            let surface =
                surfaceOf
                    [ plain "root" (ComponentKind.Column (Static [ "go"; "card" ], None, None))
                      plain "goLabel" (ComponentKind.Text (Literal "Go", None))
                      plain "go" (ComponentKind.Button ("goLabel", None, Action.Event ("go", []), []))
                      plain "card" (ComponentKind.Card "q")
                      plain "q" (ComponentKind.Text (Literal "Ship it?", None)) ]
                    []
            Expect.equal (Project.title (Map.ofList [ "s", surface ]) "s") (Some "Ship it?") "the question in the card"

        testCase "a surface that says nothing has no title" <| fun () ->
            Expect.equal (Project.title (Map.ofList [ "f", formSurface [] ]) "f") None "no root, nothing drawn"

        testCaseAsync "the session records what the surface was about" <| async {
            let doc = Y.Doc.Create ()
            let! _ = show doc Yession.Session.SurfaceTool.example
            Expect.equal
                (Yession.Session.SurfaceTool.dismissal (surfacesIn doc) messageId ada "pick")
                (Ok dismissed)
                "the example's question"
        }

        testCase "a surface that is gone is not dismissed" <| fun () ->
            Expect.equal
                (Yession.Session.SurfaceTool.dismissal Map.empty messageId ada "pick")
                (Error "there is no surface 'pick'")
                "the surface named"

        testCase "a dismissal owes the agent a turn, as whoever dismissed it" <| fun () ->
            Expect.equal
                (AgentWake.pendingReason [ Yession.Domain.SessionEvent.SurfaceDismissed dismissed ])
                (Some (SurfaceAnswered "pick", ada))
                "owed to Ada"

        testCase "a dismissal reads as the question, then the surface the agent named" <| fun () ->
            Expect.equal
                (Act.sentence (Act.SurfaceDismissed dismissed) |> Yession.Domain.Phrase.said)
                "dismissed “Which branch should I rebase onto?” (surface pick, now taken down)"
                "no JSON, and the agent's id for it"
            Expect.equal
                (Act.sentence (Act.SurfaceDismissed { dismissed with Title = None }) |> Yession.Domain.Phrase.said)
                "dismissed surface pick (now taken down)"
                "the id when there is nothing to quote"

        testCase "a dismissal on the wire is the shape it will always be" <| fun () ->
            let pinned =
                """{"type":"surfaceDismissed","payload":{"messageId":"m1","surfaceId":"pick","title":"Which branch should I rebase onto?","by":{"kind":"peer","peerId":"ada"}}}"""
            Expect.equal
                (Yession.Codecs.Codec.fromString Yession.App.Codecs.Events.sessionEvent pinned)
                (Ok (Yession.Domain.SessionEvent.SurfaceDismissed dismissed))
                "the durable form decodes to the event"
    ]

/// `model` with `surface` drawn as `surfaceId` beside what it already holds, and a message
/// folded so the client sees it arrive (`ClientModel.asks`).
let private drawn (surfaceId: string) (surface: Surface) (model: ClientModel) : ClientModel =
    { model with Synced = { model.Synced with Surfaces = Map.add surfaceId surface model.Synced.Surfaces } }
    |> step (EditSurfaceMsg ("nothing-here", "/choice", branches []))

let private stackTests =
    testList "Stack" [
        // A surface is a question waiting on the reader like any other, so it stands in the
        // ask slot's stack, in the order the questions came, not the order their ids sort in.
        testCase "surfaces stand in the order they arrived" <| fun () ->
            let model = ClientModel.init (peer "ada" "Ada") |> drawn "zeta" (choiceSurface []) |> drawn "alpha" (choiceSurface [])
            Expect.equal (ClientModel.asks model) [ Ask.Surface "zeta"; Ask.Surface "alpha" ] "first drawn, first"

        testCase "a surface behind the front one is a line, one press away" <| fun () ->
            let page = render (ClientModel.init (peer "ada" "Ada") |> drawn "zeta" (choiceSurface []) |> drawn "alpha" (choiceSurface []))
            Expect.equal
                (page.Contains "data-surface=\"zeta\"", page.Contains "data-ask-line=\"surface:alpha\"")
                (true, true)
                "the oldest is drawn; the newer waits as its line"

        testCase "a surface whose root has not arrived asks nothing yet" <| fun () ->
            let rootless = surfaceOf (choiceComponents |> List.filter (fun c -> c.Id <> "root")) []
            let model = ClientModel.init (peer "ada" "Ada") |> drawn "early" rootless
            Expect.equal (ClientModel.asks model) [] "no question to stand"
    ]

let tests =
    testList "Agent surfaces" [
        syncTests
        modelTests
        viewTests
        stackTests
        toolTests
        pressTests
        projectionTests
        dismissTests
    ]
