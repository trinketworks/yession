module Yession.Tests.ManagerForms

// What the Manager page's MCP forms MEAN, read off a body with no Manager behind it. The
// distinction every case turns on: a form that named `session` and left it blank said "every
// session" (the page's first option does exactly that), and a form that did not name it said
// nothing — and nothing must never be read as everything.

open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Tools
open Yession.Host
open Yession.Tests.Support

let private oneSession (id: string) = OneSession (SessionId.create id |> expect)

let private named (name: string) = McpServerName.create name |> expect

let tests =
    testList "Manager MCP forms" [
        testCase "a withdrawal that names no session field is refused" <| fun () ->
            Expect.isError (ManagerUi.withdrawalOfForm "name=serial") "absent is not every session"

        testCase "a withdrawal with a blank session withdraws the every-session declaration" <| fun () ->
            Expect.equal
                (ManagerUi.withdrawalOfForm "name=serial&session=")
                (Ok (named "serial", AnySession))
                "the page's any-session option posts the field empty"

        testCase "a withdrawal with a session withdraws that session's declaration" <| fun () ->
            Expect.equal
                (ManagerUi.withdrawalOfForm "name=serial&session=alpha")
                (Ok (named "serial", oneSession "alpha"))
                "the audience is the session named"

        testCase "a withdrawal that names no server is refused" <| fun () ->
            Expect.isError (ManagerUi.withdrawalOfForm "session=") "a withdrawal needs a name even when the session is blank"

        testCase "a declaration with no session field is refused" <| fun () ->
            Expect.isError
                (ManagerUi.declarationOfForm "name=serial&url=http%3A%2F%2F127.0.0.1%3A7333")
                "absent is not every session"

        testCase "a declaration with a blank session reaches every session" <| fun () ->
            let declared = ManagerUi.declarationOfForm "name=serial&url=http%3A%2F%2F127.0.0.1%3A7333&session="
            Expect.equal (declared |> Result.map (fun d -> d.Audience)) (Ok AnySession) "blank is the page's any-session"

        testCase "a declaration with a session reaches only that session" <| fun () ->
            let declared = ManagerUi.declarationOfForm "name=serial&url=http%3A%2F%2F127.0.0.1%3A7333&session=alpha"
            Expect.equal (declared |> Result.map (fun d -> d.Audience)) (Ok (oneSession "alpha")) "the audience is the session named"

        testCase "a declaration with no address field is refused" <| fun () ->
            Expect.isError (ManagerUi.declarationOfForm "name=serial&session=") "absent is not an address"

        testCase "a declaration with a blank address is refused" <| fun () ->
            Expect.isError (ManagerUi.declarationOfForm "name=serial&url=&session=") "blank is not an address"

        testCase "a declaration with no name field is refused" <| fun () ->
            Expect.isError (ManagerUi.declarationOfForm "url=http%3A%2F%2F127.0.0.1%3A7333&session=") "absent is not a name"

        testCase "a declaration's description is optional, and absent and blank agree" <| fun () ->
            let described (extra: string) =
                ManagerUi.declarationOfForm ("name=serial&url=http%3A%2F%2F127.0.0.1%3A7333&session=" + extra)
                |> Result.map (fun d -> d.Server.Description)
            Expect.equal (described "") (Ok None) "no field is no description"
            Expect.equal (described "&description=") (Ok None) "a blank field is no description"
            Expect.equal (described "&description=a+rig") (Ok (Some "a rig")) "a given one is kept"
    ]
