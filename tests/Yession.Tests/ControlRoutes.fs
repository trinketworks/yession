module Yession.Tests.ControlRoutes

// The control channel's address book: every route a Session may call, spelled once in
// `Yession.Manager.ControlRoute` and read by both the Manager's `Control` and the Session's
// `ControlClient`. These are the promises that make "one spelling" true — that the list the
// parser reads is the whole union, and that what a client addresses is what the server claims.
//
// No capability: pure functions.

open Fable.Pyxpecto
open Yession.Manager

/// A distinct number per case, by an exhaustive match: a case added to the union does not
/// compile here until it is numbered, and `ControlRoute.all` is then held to carrying it.
let private ordinal (route: ControlRoute) : int =
    match route with
    | ControlRoute.Name -> 0
    | ControlRoute.Summary -> 1
    | ControlRoute.Activity -> 2
    | ControlRoute.RegisterClient -> 3
    | ControlRoute.SubscribeHook -> 4
    | ControlRoute.UnsubscribeHook -> 5
    | ControlRoute.SetSecret -> 6
    | ControlRoute.ListSecrets -> 7
    | ControlRoute.DeleteSecret -> 8
    | ControlRoute.ResolveSecret -> 9
    | ControlRoute.BeginConnection -> 10
    | ControlRoute.CompleteConnection -> 11
    | ControlRoute.PutConnection -> 12
    | ControlRoute.PutConnectionGrant -> 13
    | ControlRoute.DisconnectConnection -> 14
    | ControlRoute.RejectConnection -> 15
    | ControlRoute.ResolveConnection -> 16
    | ControlRoute.Notifications -> 17
    | ControlRoute.McpServers -> 18
    | ControlRoute.ConnectionStatuses -> 19

let private otherMethod (route: ControlRoute) : string =
    match ControlRoute.method route with
    | "GET" -> "POST"
    | _ -> "GET"

let tests =
    testList "control routes" [

        testCase "the list the parser reads names every route exactly once" <| fun () ->
            Expect.equal
                (ControlRoute.all |> List.map ordinal |> List.sort)
                [ 0 .. 19 ]
                "a route missing from `all` is a route the Manager answers 404 to, whatever the client sends"

        testCase "every control route parses back from its own method and path" <| fun () ->
            for route in ControlRoute.all do
                Expect.equal
                    (ControlRoute.parse (ControlRoute.method route) (ControlRoute.path route))
                    (Some route)
                    (sprintf "%s %s" (ControlRoute.method route) (ControlRoute.path route))

        testCase "no two control routes share a method and path" <| fun () ->
            let claims = ControlRoute.all |> List.map (fun route -> ControlRoute.method route, ControlRoute.path route)
            Expect.equal (List.length (List.distinct claims)) (List.length claims) "a shared claim is a route nobody can reach"

        testCase "every control route's path sits under /control/" <| fun () ->
            for route in ControlRoute.all do
                Expect.isTrue
                    ((ControlRoute.path route).StartsWith "/control/")
                    "`Control` hands a request to the router only under this prefix"

        testCase "a path under /control/ that names no route is unclaimed" <| fun () ->
            Expect.equal (ControlRoute.parse "POST" "/control/secrets/reveal") None "no such verb"

        testCase "a route's path under the wrong method is unclaimed" <| fun () ->
            for route in ControlRoute.all do
                Expect.equal
                    (ControlRoute.parse (otherMethod route) (ControlRoute.path route))
                    None
                    (sprintf "%s is a %s route" (ControlRoute.path route) (ControlRoute.method route))

        testCase "a route's path with a trailing slash is unclaimed" <| fun () ->
            Expect.equal
                (ControlRoute.parse "POST" (ControlRoute.path ControlRoute.Name + "/"))
                None
                "the paths are exact, as the literals they replaced were"

        testCase "a control route at a base url is that base joined to its path" <| fun () ->
            Expect.equal
                (ControlRoute.at "http://127.0.0.1:4000" ControlRoute.SetSecret)
                "http://127.0.0.1:4000/control/secrets/set"
                "the join"

        testCase "a trailing slash on the base url does not double the separator" <| fun () ->
            Expect.equal
                (ControlRoute.at "http://127.0.0.1:4000/" ControlRoute.Name)
                (ControlRoute.at "http://127.0.0.1:4000" ControlRoute.Name)
                "with or without"
    ]
