namespace Yession.App.Codecs

open Yession.Domain
open Yession.Codecs

open Yession.Domain.Chat
open Yession.Domain.Sandboxes
open Yession.Domain.Files
open Yession.Domain.Artifacts
open Yession.Domain.Content
open Yession.Domain.Repos
open Yession.Domain.Prs
open System
open Yession.Domain.Agent
open Yession.Domain.Link
open Yession.Domain.Terminals
open Yession.Domain.Tools
open System.Globalization
#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// A terminal's transcript as the App reads it: the asciicast v2 lines a terminal's recording
/// is made of, and the keyframes beside them. The Session writes both to disk in exactly the
/// shape it serves, so the App owns the shape and the Session references this project to write
/// it.
[<RequireQualifiedAccess>]
module Transcripts =

    /// One asciicast line. Deliberately NOT this file's usual tagged-object shape: the
    /// format is asciinema's, and matching it exactly is the point — a transcript is only
    /// worth calling an audit artifact if something other than Yession can read it. So the
    /// header is a bare object with `version: 2` and a record is a bare three-element
    /// array, `[time, code, data]`.
    let line : Codec<TranscriptLine> =
        let encodeHeader (h: TranscriptHeader) =
            Encode.object
                [ "version", Encode.int 2
                  "width", Encode.int h.Width
                  "height", Encode.int h.Height
                  "timestamp", Encode.int64 h.Timestamp ]
        let encodeRecord (r: TranscriptRecord) =
            [ Encode.float r.At; Encode.string (TranscriptKind.code r.Kind); Encode.string r.Data ]
            |> Encode.list
        let decodeHeader : Decoder<TranscriptLine> =
            Decode.object (fun get ->
                { Width = get.Required.Field "width" Decode.int
                  Height = get.Required.Field "height" Decode.int
                  Timestamp = get.Required.Field "timestamp" Decode.int64 })
            |> Decode.map TranscriptHeaderLine
        let decodeRecord : Decoder<TranscriptLine> =
            Decode.map3
                (fun at code data -> at, code, data)
                (Decode.index 0 Decode.float)
                (Decode.index 1 Decode.string)
                (Decode.index 2 Decode.string)
            |> Decode.andThen (fun (at, code, data) ->
                match TranscriptKind.parse code with
                | Some kind -> Decode.succeed (TranscriptRecordLine { At = at; Kind = kind; Data = data })
                | None -> Decode.fail (sprintf "Unknown transcript record kind: %s" code))
        { Encode =
            (fun line ->
                match line with
                | TranscriptHeaderLine h -> encodeHeader h
                | TranscriptRecordLine r -> encodeRecord r)
          // The header is tried first because it is the one shape with a discriminator of
          // its own; a record is anything array-shaped.
          Decode = Decode.oneOf [ decodeHeader; decodeRecord ] }

    let record : Codec<TranscriptRecord> =
        { Encode = fun r -> line.Encode (TranscriptRecordLine r)
          Decode =
            line.Decode
            |> Decode.andThen (function
                | TranscriptRecordLine r -> Decode.succeed r
                | TranscriptHeaderLine _ -> Decode.fail "expected a transcript record, found the header") }

    /// One keyframe line in a terminal's `.keys.jsonl` sidecar (Plan 14, stage 3). This one
    /// IS the codecs' usual object shape rather than asciinema's, and deliberately so: it is
    /// ours, it is not in the `.cast`, and nothing outside Yession has to read it.
    let keyframe : Codec<TranscriptKeyframe> =
        { Encode =
            fun (k: TranscriptKeyframe) ->
                Encode.object
                    [ "seq", Encode.int k.Seq
                      "cols", Encode.int k.Cols
                      "rows", Encode.int k.Rows
                      "screen", Encode.string k.Screen ]
          Decode =
            Decode.object (fun get ->
                { Seq = get.Required.Field "seq" Decode.int
                  Cols = get.Required.Field "cols" Decode.int
                  Rows = get.Required.Field "rows" Decode.int
                  Screen = get.Required.Field "screen" Decode.string }) }

/// Rebuilding a `.cast` file from what a client has fetched (Plan 13, stage 3e).
///
/// The audit read. A closed terminal's blocks survive in the projection, but a list of
/// commands is not the same artefact as the RECORDING — a replay shows the terminal as it
/// behaved, at the speed it behaved, which is what someone auditing actually wants to watch.
///
/// It is cheap because the transcript already IS asciicast v2 on disk and the chunk route
/// already serves it: concatenating chunk 0, 1, 2 … reproduces the file byte for byte, so any
/// prefix of chunks is a valid `.cast`. This reassembles that from the DECODED records a
/// client holds, which is the same thing through a round trip the codec already pins — and it
/// means the replay rides the browser's HTTP cache rather than a second whole-file route.
module TranscriptReplay =

    /// The `.cast` text for a header, the records under it, and CHAPTER MARKERS spliced in
    /// as asciicast's own `"m"` events (Plan 25, stage 1).
    ///
    /// Markers belong in the file rather than in the player's `markers` option, and the
    /// difference is not cosmetic. The player idle-compresses EVENTS as it loads a recording
    /// (`idleTimeLimit`), then multiplexes an option-supplied marker list in afterwards, on
    /// the clock the file was written in — so option markers land in dead air the
    /// compression just removed. Everything downstream of that reads wrong at once: the
    /// duration shown is the uncompressed one, playback trudges through gaps to reach a
    /// marker, a `startAt` computed against the compressed clock lands short of the chapter
    /// it names, and the last marker is dropped by the chapter list's `time < duration`
    /// filter because it has become the final event. A marker written into the cast rides
    /// the same compression as every record around it and none of that happens.
    ///
    /// A marker sorts BEFORE a record at the same time — the order the player's own
    /// multiplex picks — so a chapter names the command whose first byte follows it.
    ///
    /// `"m"` is not a `TranscriptKind`, and it should not become one: no transcript on disk
    /// contains a marker. Chapters are a fact about the BLOCKS a terminal ran, folded from
    /// the event log at the moment a recording is assembled for a reader.
    ///
    /// Two residuals the player keeps, and neither is a reason to go back to the option. A
    /// marker at exactly the cast's LAST event time is still dropped from the chapter list —
    /// the UI filters on `time < duration`, strictly, and a marker that is the final event has
    /// become the duration. That is a block that printed nothing, and the `"m"` event is in the
    /// recording either way. And the control bar shows the raw poster time until the reader
    /// first interacts, so a poster nudged past the final record reads a hair long before play.
    let castWithMarkers
        (header: TranscriptHeader)
        (records: (int * TranscriptRecord) list)
        (markers: (float * string) list)
        : string =
        let markerLine (at: float) (label: string) =
            [ Encode.float at; Encode.string "m"; Encode.string label ]
            |> Encode.list
            |> Encode.toString 0
        let recordLine (record: TranscriptRecord) =
            Codec.toString Transcripts.line (TranscriptRecordLine record)
        let rec merge (records: (int * TranscriptRecord) list) (markers: (float * string) list) =
            match records, markers with
            | [], [] -> []
            | [], (at, label) :: restMarkers -> markerLine at label :: merge [] restMarkers
            | (_, record) :: restRecords, [] -> recordLine record :: merge restRecords []
            | (_, record) :: restRecords, (at, label) :: restMarkers ->
                if at <= record.At then markerLine at label :: merge records restMarkers
                else recordLine record :: merge restRecords markers
        let lines =
            (Codec.toString Transcripts.line (TranscriptHeaderLine header))
            :: merge (records |> List.sortBy fst) (markers |> List.sortBy fst)
        String.concat "\n" lines + "\n"

    /// The `.cast` text for a header and the records under it, in sequence order.
    ///
    /// Gaps are simply absent rather than filled: a record the client never fetched, or one
    /// retention deleted, is a line that is not there. asciicast has no notion of a hole, and
    /// inventing a placeholder would put something in the recording that the terminal never
    /// printed. Whether the recording is COMPLETE is a separate question, answered by the
    /// terminal's `DroppedBytes` and said in the surface rather than smuggled into the file.
    ///
    /// A recording with no chapters: the same text `castWithMarkers` writes when nothing
    /// marks it, so the two can never disagree about the shape of a cast.
    let cast (header: TranscriptHeader) (records: (int * TranscriptRecord) list) : string =
        castWithMarkers header records []

    /// The `.cast` text for the half-open transcript range `[fromSeq, toSeq)` — one block's
    /// output, or one stretch of live mode — as a standalone recording the stock player
    /// renders unmodified.
    ///
    /// Three things happen here, and the first two are the ones a naive slice gets wrong:
    ///
    ///   * **The keyframe is painted first**, as a synthesized output record at `t = 0`, so
    ///     the range starts from the screen it actually started from rather than from a
    ///     blank VT. Its size overrides the header's, because the header records the size
    ///     the terminal OPENED at and a resize before the range changed it.
    ///   * **Times are rebased** to the range's first record, because asciicast times are
    ///     relative to the start of the file — without this a block forty minutes in makes
    ///     the player idle for forty minutes before its first frame.
    ///   * Records outside the range are dropped, and INPUT records are kept: what someone
    ///     typed is part of a stretch of live mode, and a pty echoes it anyway.
    ///
    /// With no keyframe (a recording written before they existed) the range still rebases
    /// and still plays; it is the naive slice, approximately right for command output and
    /// wrong wherever the screen carried state in. That is the honest degradation — the
    /// alternative is refusing to play a recording we do have.
    let range
        (header: TranscriptHeader)
        (keyframe: TranscriptKeyframe option)
        (fromSeq: int)
        (toSeq: int)
        (records: (int * TranscriptRecord) list)
        : string =
        let inRange =
            records
            |> List.filter (fun (seq, _) -> seq >= fromSeq && seq < toSeq)
            |> List.sortBy fst
        let origin = inRange |> List.tryHead |> Option.map (fun (_, r) -> r.At) |> Option.defaultValue 0.0
        let header =
            match keyframe with
            | Some k -> { header with Width = k.Cols; Height = k.Rows }
            | None -> header
        let painted =
            match keyframe with
            | Some k when k.Screen <> "" -> [ 0, { At = 0.0; Kind = TranscriptOutput; Data = k.Screen } ]
            | _ -> []
        let rebased =
            inRange |> List.map (fun (seq, record) -> seq + 1, { record with At = record.At - origin })
        cast header (painted @ rebased)
