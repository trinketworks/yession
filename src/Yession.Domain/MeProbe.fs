namespace Yession.Domain

open Thoth.Json

/// The `/me` probe's wire payload — one codec, encoded here by the Session Process's
/// answer and decoded here by the browser's ask, so the two sides fold the SAME shape
/// rather than a hand-written JSON string on one side and a hand-picked field list on
/// the other. `Yession.Domain` is compiled both by .NET (the server) and by Fable (the
/// browser), the same way `Attribution` is — one module, not a server copy and a client
/// guess that can drift apart.
///
/// `toJson`/`ofJson` are the only surface callers need: `Thoth.Json`'s own `JsonValue` and
/// `Decoder<'a>` are distinct concrete types from `Thoth.Json.Net`'s (a plain .NET build
/// of a file that also has `Thoth.Json.Net` open — as the server's does, for other
/// reasons — resolves `Encode`/`Decode` to THAT backend, not this one), so a caller
/// exchanging strings rather than `encoder`/`decoder` directly never has to care which
/// backend is ambient where it stands.
module MeProbe =

    type Response =
        { PeerToken: string
          Sub: string
          Attributed: bool
          /// The attributed user's real name (`claims.name`, minted into the login
          /// cookie — see `SessionAuth.create`), when this access is attributed to one.
          /// `None` for unattributed access, and for an attributed user whose identity
          /// provider never gave one.
          DisplayName: string option }

    let private encoder (response: Response) =
        Encode.object
            [ "peerToken", Encode.string response.PeerToken
              "sub", Encode.string response.Sub
              "attributed", Encode.bool response.Attributed
              "displayName",
              response.DisplayName |> Option.map Encode.string |> Option.defaultValue Encode.nil ]

    let private decoder: Decoder<Response> =
        Decode.object (fun get ->
            { PeerToken = get.Required.Field "peerToken" Decode.string
              Sub = get.Required.Field "sub" Decode.string
              Attributed = get.Required.Field "attributed" Decode.bool
              DisplayName = get.Optional.Field "displayName" Decode.string })

    let toJson (response: Response) : string = response |> encoder |> Encode.toString 0

    let ofJson (json: string) : Result<Response, string> = Decode.fromString decoder json
