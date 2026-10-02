/// The wire shapes the browser client OWNS. The session process references this project
/// to answer them, so one declaration serves both ends of each exchange.
module Yession.Browser.Contracts

open Yession.Domain

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// One side's session description, as the wire carries it: `{ type, sdp }`.
type SdpMessage = { Type : string; Sdp : string }

/// Both halves of the signalling wire, in one value.
///
/// The decode half was already here; the encode half was `JSON.stringify {| ``type`` = ty;
/// sdp = sdp |}`, an anonymous record whose backtick-escaped label was the only place the
/// wire's field name was written on the way OUT. Nothing checked the two against each
/// other — rename the field on one side and this side still compiles, still stringifies,
/// and answers an offer with a body the other side reads as no session description at
/// all. A codec cannot drift that way: one declaration, read in both directions.
///
/// Both fields are required, because neither has a meaning this side can supply. A message
/// is DECODED rather than asserted: it arrives over somebody else's POST, and the two
/// readers that used to unbox it handed a missing `sdp` to libdatachannel as `undefined` —
/// a native call with no answer for it, inside a handler with nowhere to report one.
///
/// The browser owns this shape: it is the client's half of the handshake, and the
/// session process references this project to answer it.
let sdpMessage : Codec<SdpMessage> =
    { Encode =
        fun (message: SdpMessage) ->
            Encode.object
                [ "type", Encode.string message.Type
                  "sdp", Encode.string message.Sdp ]
      Decode =
        Decode.object (fun get ->
            { Type = get.Required.Field "type" Decode.string
              Sdp = get.Required.Field "sdp" Decode.string }) }

/// What a signalling body says, or nothing — a body that is not JSON, and one that is JSON
/// carrying no session description, are the same nothing to both callers.
let parseSdp (json: string) : SdpMessage option =
    Codec.fromString sdpMessage json |> Result.toOption

