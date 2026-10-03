namespace Yession.App.Codecs

// The wire shapes the App owns. The App is the client of every surface the Session serves it,
// so the App says what each exchange carries and the Session references this project to answer
// it: one declaration, read and written by both ends, where two used to agree by hand.

open Yession.Domain
open Yession.Codecs

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// Which half of the handshake a description is. The only two this exchange has: the App
/// offers and the Session answers, so anything else on the wire is not a description either
/// side could use, and the decode refuses it rather than handing it on.
[<RequireQualifiedAccess>]
type SdpKind =
    | Offer
    | Answer

/// One side's session description, as the wire carries it: `{ type, sdp }`.
type SdpMessage = { Kind : SdpKind; Sdp : string }

[<RequireQualifiedAccess>]
module SdpKind =
    /// The word WebRTC itself uses, which is what both ends hand their peer connection.
    let wire (kind: SdpKind) : string =
        match kind with
        | SdpKind.Offer -> "offer"
        | SdpKind.Answer -> "answer"

    let ofWire (word: string) : SdpKind option =
        match word with
        | "offer" -> Some SdpKind.Offer
        | "answer" -> Some SdpKind.Answer
        | _ -> None

/// The signalling exchange: the App POSTs its offer to the Session's signal url and reads the
/// answer out of the reply.
///
/// The browser used to write its offer with `JSON.stringify` and read the answer with an
/// unchecked `json<RTCSessionDescriptionInit>`, while the Session decoded the same shape with
/// a codec of its own — two declarations nothing compared, and on the browser's side no decode
/// at all, so a reply missing its `sdp` reached `setRemoteDescription` as `undefined`. Both
/// fields are required, because neither has a meaning either side can supply.
[<RequireQualifiedAccess>]
module Sdp =
    let message : Codec<SdpMessage> =
        { Encode =
            fun (message: SdpMessage) ->
                Encode.object
                    [ "type", Encode.string (SdpKind.wire message.Kind)
                      "sdp", Encode.string message.Sdp ]
          Decode =
            Decode.object (fun get ->
                { Kind =
                    get.Required.Field
                        "type"
                        (Decode.string
                         |> Decode.andThen (fun word ->
                             match SdpKind.ofWire word with
                             | Some kind -> Decode.succeed kind
                             | None -> Decode.fail (sprintf "%s is not an offer or an answer" word)))
                  Sdp = get.Required.Field "sdp" Decode.string }) }

    /// What a signalling body says, or nothing — a body that is not JSON, and one that is
    /// JSON carrying no session description, are the same nothing to every caller.
    let parse (json: string) : SdpMessage option =
        Codec.fromString message json |> Result.toOption
