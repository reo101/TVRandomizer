namespace TVRandomizer.Core

open System
open System.Collections.Generic

[<Sealed>]
type HostPlaylist(session: string, hostId: uint64, inventory: string array, random: Random) =
    let inventoryMessage =
        { Session = session
          Videos = inventory }

    do
        if not (Protocol.validateInventory inventoryMessage) then
            invalidArg "inventory" "Host inventory is invalid."

        if String.IsNullOrWhiteSpace session || isNull random then
            invalidArg "session" "Session and random source are required."

    let hostInventory = Array.copy inventory
    let members = HashSet<uint64>([| hostId |])
    let inventories = Dictionary<uint64, string array>()
    let mutable retainedVideos = Playlist.shuffle random inventory
    let mutable cycle = 1L
    let mutable generation = 1L
    let mutable status = if inventory.Length = 0 then "empty" else "ready"

    let mutable current =
        { Session = session
          Generation = generation
          Cycle = cycle
          Videos = Array.copy retainedVideos
          Status = status }

    do inventories.[hostId] <- hostInventory

    let copySnapshot (snapshot: PlaylistSnapshot) =
        { snapshot with
            Videos = Array.copy snapshot.Videos }

    let setSnapshot (newStatus: string) (videos: string array) (newCycle: int64) =
        generation <- generation + 1L
        cycle <- newCycle
        status <- newStatus

        current <-
            { Session = session
              Generation = generation
              Cycle = cycle
              Videos = Array.copy videos
              Status = status }

    let inventoriesComplete () =
        members |> Seq.forall inventories.ContainsKey

    let intersection () =
        let common = HashSet<string>(hostInventory, StringComparer.Ordinal)

        for clientId in members do
            if clientId <> hostId then
                let clientVideos = inventories.[clientId] |> HashSet<string>
                common.IntersectWith(clientVideos)

        hostInventory |> Array.filter common.Contains

    let sameMembers (left: string array) (right: string array) =
        left.Length = right.Length
        && (HashSet<string>(left, StringComparer.Ordinal)).SetEquals(right)

    let publishReady (allowExpansion: bool) =
        if not (inventoriesComplete ()) then
            None
        else
            let common = intersection ()

            let isReduction =
                not (HashSet<string>(common, StringComparer.Ordinal).IsSupersetOf(retainedVideos))

            if not allowExpansion && not isReduction then
                None
            elif sameMembers common retainedVideos then
                if status = "waiting" then
                    let readyStatus = if retainedVideos.Length = 0 then "empty" else "ready"
                    setSnapshot readyStatus retainedVideos cycle
                    Some(copySnapshot current)
                else
                    None
            else
                let shuffled = Playlist.shuffle random common
                retainedVideos <- Array.copy shuffled
                cycle <- cycle + 1L
                let readyStatus = if shuffled.Length = 0 then "empty" else "ready"
                setSnapshot readyStatus shuffled cycle
                Some(copySnapshot current)

    member _.Snapshot = copySnapshot current

    member _.UpdateMembership(connected: uint64 array) : PlaylistSnapshot option =
        if
            isNull connected
            || connected |> Array.distinct |> Array.length <> connected.Length
        then
            None
        else
            let nextMembers = HashSet<uint64>(connected)
            nextMembers.Add(hostId) |> ignore

            if members.SetEquals(nextMembers) then
                None
            else
                let removed =
                    members
                    |> Seq.filter (fun clientId -> not (nextMembers.Contains clientId))
                    |> Seq.toArray

                for clientId in removed do
                    inventories.Remove(clientId) |> ignore

                members.Clear()
                members.UnionWith(nextMembers)

                if status = "ready" || status = "empty" then
                    let missing =
                        members |> Seq.exists (fun clientId -> not (inventories.ContainsKey clientId))

                    if missing then
                        setSnapshot "waiting" Array.empty cycle
                        Some(copySnapshot current)
                    elif status = "empty" then
                        publishReady true
                    else
                        None
                else
                    publishReady true

    member _.SubmitInventory(sender: uint64, message: InventoryMessage) : PlaylistSnapshot option =
        if
            sender = hostId
            || not (members.Contains sender)
            || not (Protocol.validateInventory message)
            || message.Session <> session
        then
            None
        else
            let videos = Array.copy message.Videos

            match inventories.TryGetValue sender with
            | true, prior when prior = videos -> None
            | _ ->
                inventories.[sender] <- videos

                if status = "waiting" then
                    publishReady true
                else
                    publishReady false

    member _.CompleteCycle() : PlaylistSnapshot option =
        if status <> "ready" then
            None
        elif not (inventoriesComplete ()) then
            None
        else
            let shuffled = Playlist.shuffle random (intersection ())
            retainedVideos <- Array.copy shuffled
            let nextCycle = cycle + 1L
            let nextStatus = if shuffled.Length = 0 then "empty" else "ready"
            setSnapshot nextStatus shuffled nextCycle
            Some(copySnapshot current)
