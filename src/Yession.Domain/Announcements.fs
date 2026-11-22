namespace Yession.Domain

/// One thing that a session tells the hosts of a deployment, through the Manager's registry
/// stream.
///
/// The Manager stores an announcement and relays it. The Manager does not read `Data`. A
/// session that runs a newer build can thus announce a new kind through an older Manager.
/// See docs/decisions/2026-11-22-a-session-announces-and-the-manager-relays.md.
type Announcement =
    { /// What this announces, for example `sandbox`. A reader that does not know a kind
      /// ignores the announcement.
      Kind : string
      /// Which one, among the announcements of this kind from one session.
      Name : string
      /// A JSON object, as text. The session that announces is the only writer of its
      /// shape, and a reader of that kind is the only reader. The codec makes sure that the
      /// text is an object (`ControlWire.announcement`).
      Data : string }

module Announcement =

    /// The most announcements that one session can hold.
    let maxCount = 64

    /// The most characters in a `Kind` or in a `Name`.
    let maxLabel = 128

    /// The most characters of `Data` that one session can hold, over all its announcements.
    let maxData = 65536

    let private kindCharacter (c: char) : bool =
        (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-'

    let private refuseLabel (what: string) (value: string) : string option =
        if value = "" then Some (sprintf "an announcement has an empty %s" what)
        elif value.Length > maxLabel then
            Some (sprintf "the %s of an announcement has %d characters, and the limit is %d" what value.Length maxLabel)
        elif value |> Seq.exists System.Char.IsControl then
            Some (sprintf "the %s of an announcement contains a control character" what)
        else None

    /// A whole set as a session sent it, or the reason that the Manager refuses it. The
    /// Manager replaces the set of the session only when this accepts.
    let check (set: Announcement list) : Result<Announcement list, string> =
        let labels =
            set
            |> List.tryPick (fun a ->
                match refuseLabel "kind" a.Kind with
                | Some reason -> Some reason
                | None when not (Seq.forall kindCharacter a.Kind) ->
                    Some (sprintf "the kind '%s' is not made of the characters a to z, 0 to 9 and '-'" a.Kind)
                | None -> refuseLabel "name" a.Name)
        let repeated =
            set
            |> List.countBy (fun a -> a.Kind, a.Name)
            |> List.tryFind (fun (_, count) -> count > 1)
        let data = set |> List.sumBy (fun a -> a.Data.Length)
        match labels, repeated with
        | Some reason, _ -> Error reason
        | None, Some ((kind, name), _) ->
            Error (sprintf "the set names the %s '%s' more than one time" kind name)
        | None, None when List.length set > maxCount ->
            Error (sprintf "the set has %d announcements, and the limit is %d" (List.length set) maxCount)
        | None, None when data > maxData ->
            Error (sprintf "the data of the set has %d characters, and the limit is %d" data maxData)
        | None, None -> Ok set
