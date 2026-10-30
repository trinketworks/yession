module Yession.Tests.A2ui

// The A2UI binding against the spec's own fixtures, copied from a2ui-project/a2ui into
// `tests/fixtures/a2ui` (that directory's README says from which commit).
//
//   * every message of every basic-catalog example decodes, so what the spec shows an agent
//     is something this binding reads;
//   * decoding what an encode wrote gives back what was decoded, so a surface relayed through
//     this binding says what its author said;
//   * every schema test case is judged as the schema judges it: a `valid: true` message
//     decodes and a `valid: false` one is refused. That is the binding's strictness, pinned
//     case by case, with the spec's own words as the name.
//
// Pure: files in, verdicts out. The cheap tier.

open Fable.Pyxpecto
open A2ui

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

let private root = "tests/fixtures/a2ui/v0_9_1"

let private jsonFiles (dir: string) =
    TestFiles.entries dir |> List.filter (fun f -> f.EndsWith ".json") |> List.sort

let private exampleMessages (file: string) : Result<JsonValue list, string> =
    Decode.fromString (Decode.field "messages" (Decode.list Decode.value)) (TestFiles.read (root + "/examples/" + file))

type private SchemaCase =
    { Description : string
      Valid : bool
      Data : JsonValue }

let private schemaCases (file: string) : Result<string * SchemaCase list, string> =
    Decode.fromString
        (Decode.object (fun get ->
            get.Required.Field "schema" Decode.string,
            get.Required.Field
                "tests"
                (Decode.list (
                    Decode.object (fun get ->
                        { Description = get.Required.Field "description" Decode.string
                          Valid = get.Required.Field "valid" Decode.bool
                          Data = get.Required.Field "data" Decode.value })))))
        (TestFiles.read (root + "/cases/" + file))

let private decodeServer (value: JsonValue) = Decode.fromValue "$" Codec.serverToClientDecoder value
let private decodeClient (value: JsonValue) = Decode.fromValue "$" Codec.clientToServerDecoder value

let private roundTrip (message: ServerToClient) =
    Codec.toString Codec.encodeServerToClient message |> Codec.fromString Codec.serverToClientDecoder

let private exampleTests =
    testList "basic catalog examples" [
        for file in jsonFiles (root + "/examples") do
            testCase (sprintf "%s decodes" file) <| fun () ->
                match exampleMessages file with
                | Error e -> failwith e
                | Ok messages ->
                    let refused = messages |> List.choose (fun m -> match decodeServer m with Error e -> Some e | Ok _ -> None)
                    Expect.equal refused [] "every message of the example is one the binding reads"

            testCase (sprintf "%s survives an encode" file) <| fun () ->
                match exampleMessages file with
                | Error e -> failwith e
                | Ok messages ->
                    for raw in messages do
                        match decodeServer raw with
                        | Error e -> failwith e
                        | Ok message -> Expect.equal (roundTrip message) (Ok message) "decoding the encode gives back the message"
    ]

let private streamTests =
    testList "the contact form stream" [
        testCase "every line of the JSONL stream decodes" <| fun () ->
            let lines =
                TestFiles.read (root + "/cases/contact_form_example.jsonl")
                |> fun text -> text.Split '\n'
                |> Array.filter (fun l -> l.Trim () <> "")
                |> List.ofArray
            let refused = lines |> List.choose (fun l -> match Codec.fromString Codec.serverToClientDecoder l with Error e -> Some e | Ok _ -> None)
            Expect.equal refused [] "no line of the stream is refused"
    ]

let private caseTests =
    testList "schema cases" [
        for file in jsonFiles (root + "/cases") do
            match schemaCases file with
            | Error e -> testCase file <| fun () -> failwith e
            | Ok (schema, cases) ->
                let decode = if schema = "client_to_server.json" then decodeClient >> Result.map ignore else decodeServer >> Result.map ignore
                for case in cases do
                    testCase (sprintf "%s: %s" file case.Description) <| fun () ->
                        if case.Valid then Expect.isOk (decode case.Data) "the schema accepts it, so the binding reads it"
                        else Expect.isError (decode case.Data) "the schema refuses it, so the binding does"
    ]

let private surface = "\"surfaceId\":\"s\""

let private ownTests =
    testList "beyond the schema" [
        testCase "a call whose function returns the wrong type for its slot is refused" <| fun () ->
            // `formatString` returns a string; a check's condition takes a boolean. The schema
            // passes it when no `returnType` is stated; a renderer could only guess.
            let message =
                sprintf
                    """{"version":"v0.9.1","updateComponents":{%s,"components":[{"id":"root","component":"Button","child":"t","action":{"event":{"name":"go"}},"checks":[{"condition":{"call":"formatString","args":{"value":"x"}},"message":"m"}]}]}}"""
                    surface
            Expect.isError (Codec.fromString Codec.serverToClientDecoder message) "a string is not a condition"

        testCase "a null value is a value: the key is set to null" <| fun () ->
            let update = Codec.fromString Codec.serverToClientDecoder (sprintf """{"version":"v0.9.1","updateDataModel":{%s,"path":"/a","value":null}}""" surface)
            Expect.equal update (Ok (UpdateDataModel ("s", Some "/a", Some JNull))) "present and null sets"

        testCase "no value removes the key" <| fun () ->
            let update = Codec.fromString Codec.serverToClientDecoder (sprintf """{"version":"v0.9.1","updateDataModel":{%s,"path":"/a"}}""" surface)
            Expect.equal update (Ok (UpdateDataModel ("s", Some "/a", None))) "absent removes"

        testCase "a component outside the basic catalog is refused" <| fun () ->
            let message = sprintf """{"version":"v0.9.1","updateComponents":{%s,"components":[{"id":"root","component":"Chart"}]}}""" surface
            Expect.isError (Codec.fromString Codec.serverToClientDecoder message) "the binding renders the basic catalog only"
    ]

let tests = testList "A2ui" [ exampleTests; streamTests; caseTests; ownTests ]
