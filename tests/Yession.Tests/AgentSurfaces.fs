module Yession.Tests.AgentSurfaces

// An agent's A2UI surface as the App holds it: in the synced state, so in the doc every peer
// shares; answered through the spec's two-way binding (`EditSurfaceMsg`); and drawn in the dock
// (`SurfaceView`). Nothing in the product writes a surface yet, so each case writes one the way a
// peer's update would arrive: straight onto the doc, or straight into the model.

open Fable.Pyxpecto
open Yjs
open A2ui
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

        testCase "a surface whose root has not arrived is not drawn" <| fun () ->
            let rootless = choiceComponents |> List.filter (fun c -> c.Id <> "root")
            Expect.isFalse
                ((render (withSurface (surfaceOf rootless []))).Contains "data-surface=\"ask\"")
                "nothing to answer yet"
    ]

let tests =
    testList "Agent surfaces" [
        syncTests
        modelTests
        viewTests
    ]
