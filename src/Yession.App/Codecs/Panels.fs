namespace Yession.App.Codecs

open Yession.Codecs

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

// What the connection panels' writes ANSWER, declared once for both ends. `ClaudeRequest` and
// `GitHubRequest` (Routes.fs) say what the browser posts; these say what the session replies.
// The session used to `sprintf` each reply and the browser used to read it back with a decoder
// of its own, so a field renamed on one side was a reply the other read as nothing — and its
// reader answered nothing with a plausible value (a poll it could not read was "still waiting").
// Every decoder here refuses what it cannot read, and the caller says so on the panel.

/// The Claude sign-in's begin reply: where the human approves, and the state the flow is
/// keyed by. The browser opens the url in a tab and never reads the state, which is the
/// session's own to match against the code a human pastes back.
[<RequireQualifiedAccess>]
type AuthorizeBegun =
    { AuthorizeUrl : string
      State : string }

module AuthorizeBegun =

    let codec : Codec<AuthorizeBegun> =
        { Encode =
            fun (begun: AuthorizeBegun) ->
                Encode.object
                    [ "authorizeUrl", Encode.string begun.AuthorizeUrl
                      "state", Encode.string begun.State ]
          Decode =
            Decode.object (fun get ->
                { AuthorizeBegun.AuthorizeUrl = get.Required.Field "authorizeUrl" Decode.string
                  AuthorizeBegun.State = get.Required.Field "state" Decode.string })
            // A blank url is no url: there is nothing for the human to approve, and a sign-in
            // that opened a blank tab would read as accepted.
            |> Decode.andThen (fun begun ->
                if begun.AuthorizeUrl = "" then Decode.fail "the authorize url is blank"
                else Decode.succeed begun) }

/// The GitHub sign-in's begin reply: the code to type, where to type it, and the seconds
/// GitHub asks the tab to leave between polls. The device code that redeems the grant is not
/// here and never is — it stays in the session (`GitHubConnection.routes`).
///
/// All three are required, and the two strings non-blank: a begin that says no code or nowhere
/// to approve is no more a flow than a reply that is not JSON. An interval of nothing is
/// refused too, because the poll waits that long and nothing is what a flat-out poll waits.
[<RequireQualifiedAccess>]
type DeviceBegun =
    { UserCode : string
      VerificationUri : string
      Interval : int }

module DeviceBegun =

    let private nonBlank (what: string) : Decoder<string> =
        Decode.string
        |> Decode.andThen (fun text ->
            if text = "" then Decode.fail (sprintf "the %s is blank" what) else Decode.succeed text)

    let codec : Codec<DeviceBegun> =
        { Encode =
            fun (begun: DeviceBegun) ->
                Encode.object
                    [ "userCode", Encode.string begun.UserCode
                      "verificationUri", Encode.string begun.VerificationUri
                      "interval", Encode.int begun.Interval ]
          Decode =
            Decode.object (fun get ->
                { DeviceBegun.UserCode = get.Required.Field "userCode" (nonBlank "user code")
                  DeviceBegun.VerificationUri = get.Required.Field "verificationUri" (nonBlank "verification uri")
                  DeviceBegun.Interval = get.Required.Field "interval" Decode.int })
            |> Decode.andThen (fun begun ->
                if begun.Interval < 1 then Decode.fail "the poll interval is not a positive number of seconds"
                else Decode.succeed begun) }

/// Where a device-flow poll that the session could ASK has got to. A poll that could not ask,
/// and one that ended the flow, are not replies: the first is a 200 saying `Pending` again
/// (the code is still good), the second is the session's own 4xx.
[<RequireQualifiedAccess>]
type DevicePoll =
    /// Still waiting, at the interval GitHub now asks for — the one the browser is already
    /// leaving, or a wider one after `slow_down`.
    | Pending of interval: int
    /// The grant landed and the Manager holds it.
    | Connected

module DevicePoll =

    let codec : Codec<DevicePoll> =
        { Encode =
            fun (poll: DevicePoll) ->
                match poll with
                | DevicePoll.Pending interval ->
                    Encode.object [ "status", Encode.string "pending"; "interval", Encode.int interval ]
                | DevicePoll.Connected -> Encode.object [ "status", Encode.string "connected" ]
          // A status it does not know is refused, not read as pending: a flow that never ends
          // is the answer a reader invents when it cannot tell, and this is the place that can.
          Decode =
            Decode.field "status" Decode.string
            |> Decode.andThen (fun status ->
                match status with
                | "pending" -> Decode.field "interval" Decode.int |> Decode.map DevicePoll.Pending
                | "connected" -> Decode.succeed DevicePoll.Connected
                | other -> Decode.fail (sprintf "an unknown device-poll status '%s'" other)) }

/// A write the session carried out and has nothing to add to: a pasted code or token taken.
/// Its only content is that it was accepted, and the browser waits on the pushed status for
/// what that did — so it carries no data, and the type says so by being the one value.
type WriteAccepted = WriteAccepted

module WriteAccepted =

    let codec : Codec<WriteAccepted> =
        { Encode = fun WriteAccepted -> Encode.object [ "ok", Encode.bool true ]
          Decode =
            Decode.field "ok" Decode.bool
            |> Decode.andThen (fun accepted ->
                if accepted then Decode.succeed WriteAccepted else Decode.fail "the write was not accepted") }

/// Whether a disconnect found a credential to remove. `false` is not a failure: there was
/// nothing there, which is what the person asked for.
[<RequireQualifiedAccess>]
type DisconnectAnswered =
    { Existed : bool }

module DisconnectAnswered =

    let codec : Codec<DisconnectAnswered> =
        { Encode = fun (answered: DisconnectAnswered) -> Encode.object [ "disconnected", Encode.bool answered.Existed ]
          Decode =
            Decode.object (fun get ->
                { DisconnectAnswered.Existed = get.Required.Field "disconnected" Decode.bool }) }
