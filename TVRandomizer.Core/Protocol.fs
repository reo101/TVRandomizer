namespace TVRandomizer.Core

open System
open System.Collections.Generic
open System.Globalization

[<CLIMutable>]
type InventoryMessage =
    { Session: string
      Videos: string array }

[<CLIMutable>]
type PlaylistSnapshot =
    { Session: string
      Generation: int64
      Cycle: int64
      Videos: string array
      Status: string }

[<CLIMutable>]
type PlaybackPosition =
    { Session: string
      Generation: int64
      Index: int
      On: bool
      HasPlayed: bool }

[<RequireQualifiedAccess>]
module Protocol =
    // Keep primitive-array messages comfortably below the transport's 64 KiB recommendation.
    [<Literal>]
    let MaxVideos = 256

    let private validSession (session: string) =
        let mutable parsed = Guid.Empty

        not (String.IsNullOrWhiteSpace session)
        && Guid.TryParseExact(session, "D", &parsed)

    let private validIdentifier (identifier: string) =
        if
            isNull identifier
            || identifier.Length < 66
            || identifier.Length > 69
            || identifier.[64] <> ':'
        then
            false
        else
            let digestIsValid =
                identifier
                |> Seq.take 64
                |> Seq.forall (fun character ->
                    (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))

            let occurrence = identifier.Substring(65)
            let mutable value = 0

            digestIsValid
            && occurrence.Length > 0
            && occurrence |> Seq.forall Char.IsDigit
            && Int32.TryParse(occurrence, &value)
            && value <= 4095
            && occurrence = value.ToString(CultureInfo.InvariantCulture)

    let private validVideos (videos: string array) =
        not (isNull videos)
        && videos.Length <= MaxVideos
        && videos |> Array.forall validIdentifier
        && (videos |> Array.distinct).Length = videos.Length

    let validateInventory (message: InventoryMessage) =
        not (isNull (box message))
        && validSession message.Session
        && validVideos message.Videos

    let validateSnapshot (snapshot: PlaylistSnapshot) =
        not (isNull (box snapshot))
        && validSession snapshot.Session
        && snapshot.Generation > 0L
        && snapshot.Cycle >= 0L
        && validVideos snapshot.Videos
        && match snapshot.Status with
           | "waiting" -> snapshot.Videos.Length = 0
           | "ready" -> snapshot.Videos.Length > 0
           | "empty" -> snapshot.Videos.Length = 0
           | _ -> false

    let emptySnapshot (session: string) : PlaylistSnapshot =
        { Session = session
          Generation = 0L
          Cycle = 0L
          Videos = Array.empty
          Status = "waiting" }

    let acceptSnapshot (session: string) (generation: int64) (snapshot: PlaylistSnapshot) =
        not (isNull (box snapshot))
        && validateSnapshot snapshot
        && snapshot.Session = session
        && snapshot.Generation > generation

    let encodeInventory (message: InventoryMessage) : string array =
        if not (validateInventory message) then
            invalidArg "message" "Inventory message is invalid."

        Array.append [| message.Session |] message.Videos

    let decodeInventory (wire: string array) : InventoryMessage option =
        if isNull wire || wire.Length < 1 || wire.Length > MaxVideos + 1 then
            None
        else
            let message =
                { Session = wire.[0]
                  Videos = wire.[1..] }

            if validateInventory message then Some message else None

    let encodeSnapshot (snapshot: PlaylistSnapshot) : string array =
        if not (validateSnapshot snapshot) then
            invalidArg "snapshot" "Playlist snapshot is invalid."

        Array.concat
            [| [| snapshot.Session
                  snapshot.Generation.ToString(CultureInfo.InvariantCulture)
                  snapshot.Cycle.ToString(CultureInfo.InvariantCulture)
                  snapshot.Status |]
               snapshot.Videos |]

    let decodeSnapshot (wire: string array) : PlaylistSnapshot option =
        if isNull wire || wire.Length < 4 || wire.Length > MaxVideos + 4 then
            None
        else
            let mutable generation = 0L
            let mutable cycle = 0L

            let generationParsed =
                Int64.TryParse(wire.[1], NumberStyles.None, CultureInfo.InvariantCulture, &generation)

            let cycleParsed =
                Int64.TryParse(wire.[2], NumberStyles.None, CultureInfo.InvariantCulture, &cycle)

            if not generationParsed || not cycleParsed then
                None
            else
                let snapshot =
                    { Session = wire.[0]
                      Generation = generation
                      Cycle = cycle
                      Status = wire.[3]
                      Videos = wire.[4..] }

                if validateSnapshot snapshot then Some snapshot else None

    let encodePosition (position: PlaybackPosition) =
        if
            not (validSession position.Session)
            || position.Generation <= 0L
            || position.Index < 0
            || position.Index >= MaxVideos
        then
            invalidArg "position" "Playback position is invalid."

        [| position.Session
           position.Generation.ToString(CultureInfo.InvariantCulture)
           position.Index.ToString(CultureInfo.InvariantCulture)
           if position.On then "1" else "0"
           if position.HasPlayed then "1" else "0" |]

    let decodePosition (wire: string array) =
        if isNull wire || wire.Length <> 5 then
            None
        else
            let mutable generation = 0L
            let mutable index = 0

            if
                validSession wire.[0]
                && Int64.TryParse(wire.[1], NumberStyles.None, CultureInfo.InvariantCulture, &generation)
                && Int32.TryParse(wire.[2], NumberStyles.None, CultureInfo.InvariantCulture, &index)
                && generation > 0L
                && index >= 0
                && index < MaxVideos
                && (wire.[3] = "0" || wire.[3] = "1")
                && (wire.[4] = "0" || wire.[4] = "1")
            then
                Some
                    { Session = wire.[0]
                      Generation = generation
                      Index = index
                      On = wire.[3] = "1"
                      HasPlayed = wire.[4] = "1" }
            else
                None
