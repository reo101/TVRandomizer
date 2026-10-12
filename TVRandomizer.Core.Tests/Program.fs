namespace TVRandomizer.Core.Tests

open System
open System.IO
open TVRandomizer.Core

module Program =
    let private assertTrue condition message =
        if not condition then
            failwith message

    let private assertEqual expected actual message =
        if expected <> actual then
            failwithf "%s. Expected %A, got %A" message expected actual

    let private identifier digest occurrence =
        sprintf "%s:%d" (String.replicate 64 digest) occurrence

    let private session = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"

    let private snapshot status generation cycle videos =
        { Session = session
          Generation = generation
          Cycle = cycle
          Videos = videos
          Status = status }

    let private protocolTests () =
        let validId = identifier "a" 0

        assertTrue
            (Protocol.validateInventory
                { Session = session
                  Videos = [| validId |] })
            "Valid inventory rejected"

        assertTrue (not (Protocol.validateInventory Unchecked.defaultof<InventoryMessage>)) "Null inventory accepted"
        assertTrue (not (Protocol.validateInventory { Session = "invalid"; Videos = [||] })) "Invalid session accepted"

        assertTrue
            (not (Protocol.validateInventory { Session = session; Videos = null }))
            "Null inventory array accepted"

        assertTrue
            (not (
                Protocol.validateInventory
                    { Session = session
                      Videos = [| validId; validId |] }
            ))
            "Duplicate IDs accepted"

        assertTrue
            (not (
                Protocol.validateInventory
                    { Session = session
                      Videos = [| identifier "A" 0 |] }
            ))
            "Uppercase digest accepted"

        assertTrue
            (not (
                Protocol.validateInventory
                    { Session = session
                      Videos = [| identifier "a" 4096 |] }
            ))
            "Out-of-range occurrence accepted"

        assertTrue
            (not (
                Protocol.validateInventory
                    { Session = session
                      Videos = [| validId + "/path" |] }
            ))
            "Path-bearing ID accepted"

        assertTrue
            (not (
                Protocol.validateInventory
                    { Session = session
                      Videos = Array.create (Protocol.MaxVideos + 1) validId }
            ))
            "Oversized inventory accepted"

        assertTrue (Protocol.validateSnapshot (snapshot "ready" 1L 1L [| validId |])) "Valid snapshot rejected"
        assertTrue (not (Protocol.validateSnapshot Unchecked.defaultof<PlaylistSnapshot>)) "Null snapshot accepted"
        assertTrue (not (Protocol.validateSnapshot (snapshot "unknown" 1L 1L [||]))) "Unknown status accepted"
        assertTrue (not (Protocol.validateSnapshot (snapshot "ready" 1L 1L [||]))) "Empty ready snapshot accepted"

        assertTrue
            (not (Protocol.validateSnapshot (snapshot "waiting" 1L 1L [| validId |])))
            "Waiting snapshot with videos accepted"

        assertTrue
            (Protocol.emptySnapshot ""
             |> fun value ->
                 value.Session = ""
                 && value.Generation = 0L
                 && value.Cycle = 0L
                 && value.Status = "waiting")
            "Disconnected snapshot differs"

        assertTrue (Protocol.acceptSnapshot session 3L (snapshot "ready" 4L 1L [| validId |])) "New snapshot rejected"

        assertTrue
            (not (Protocol.acceptSnapshot session 4L (snapshot "ready" 4L 1L [| validId |])))
            "Same-generation snapshot accepted"

        assertTrue
            (not (
                Protocol.acceptSnapshot
                    session
                    0L
                    { snapshot "ready" 1L 1L [| validId |] with
                        Session = "bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee" }
            ))
            "Different-session snapshot accepted"

        let uniqueIds = Array.init Protocol.MaxVideos (identifier "a")

        assertTrue
            (Protocol.validateInventory
                { Session = session
                  Videos = uniqueIds })
            "Maximum-sized valid inventory rejected"

        assertTrue
            (not (
                Protocol.validateInventory
                    { Session = session
                      Videos = [| (String.replicate 64 "a") + ":00" |] }
            ))
            "Noncanonical occurrence accepted"

        let position =
            { Session = session
              Generation = 3L
              Index = 2
              On = true
              HasPlayed = true }

        assertEqual
            (Some position)
            (position |> Protocol.encodePosition |> Protocol.decodePosition)
            "Position roundtrip failed"

        assertTrue
            (Protocol.decodePosition [| session; "3"; "256"; "1"; "1" |] |> Option.isNone)
            "Oversized clip index accepted"

        assertTrue
            (Protocol.decodePosition [| session; "3"; "2"; "true"; "1" |] |> Option.isNone)
            "Malformed power accepted"

        assertTrue (Protocol.decodePosition null |> Option.isNone) "Null position accepted"

        let inventory =
            { Session = session
              Videos = [| validId |] }

        assertEqual
            (Some inventory)
            (inventory |> Protocol.encodeInventory |> Protocol.decodeInventory)
            "Inventory wire roundtrip failed"

        assertTrue (Protocol.decodeInventory null |> Option.isNone) "Null inventory wire accepted"
        assertTrue (Protocol.decodeInventory [||] |> Option.isNone) "Short inventory wire accepted"

        assertTrue
            (Protocol.decodeInventory [| session; validId; validId |] |> Option.isNone)
            "Malformed inventory wire accepted"

        assertTrue
            (Protocol.decodeInventory (Array.create (Protocol.MaxVideos + 2) validId)
             |> Option.isNone)
            "Oversized inventory wire accepted"

        let wireSnapshot = snapshot "ready" 2L 0L [| validId |]

        assertEqual
            (Some wireSnapshot)
            (wireSnapshot |> Protocol.encodeSnapshot |> Protocol.decodeSnapshot)
            "Snapshot wire roundtrip failed"

        assertTrue (Protocol.decodeSnapshot null |> Option.isNone) "Null snapshot wire accepted"
        assertTrue (Protocol.decodeSnapshot [| session; "1"; "0" |] |> Option.isNone) "Short snapshot wire accepted"

        assertTrue
            (Protocol.decodeSnapshot [| session; "+1"; "0"; "ready"; validId |]
             |> Option.isNone)
            "Noncanonical integer accepted"

        assertTrue
            (Protocol.decodeSnapshot [| session; "1"; "-1"; "ready"; validId |]
             |> Option.isNone)
            "Negative cycle accepted"

        assertTrue
            (Protocol.decodeSnapshot [| session; "0"; "0"; "empty" |] |> Option.isNone)
            "Zero generation accepted"

        assertTrue
            (Protocol.decodeSnapshot (Array.create (Protocol.MaxVideos + 5) validId)
             |> Option.isNone)
            "Oversized snapshot wire accepted"

    let private playlistTests () =
        let source = [| "a"; "b"; "c"; "d" |]
        let untouched = Array.copy source
        let shuffled = Playlist.shuffle (Random(17)) source
        assertEqual untouched source "Shuffle mutated input"
        assertEqual (untouched |> Array.sort) (shuffled |> Array.sort) "Shuffle changed permutation"
        let root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore

        try
            let first = Path.Combine(root, "first.bin")
            let second = Path.Combine(root, "renamed.bin")
            let third = Path.Combine(root, "different.bin")
            File.WriteAllBytes(first, [| 1uy; 2uy; 3uy |])
            File.WriteAllBytes(second, [| 1uy; 2uy; 3uy |])
            File.WriteAllBytes(third, [| 4uy; 5uy |])
            let identified = Playlist.identifyFiles [ first; second; third ]
            assertEqual 3 identified.Count "Duplicate raw contents were lost"

            assertEqual
                ((Playlist.identifyFiles [ first ] |> Map.toArray).[0] |> fst)
                ((Playlist.identifyFiles [ second ] |> Map.toArray).[0] |> fst)
                "Renaming identical content changed its identity"

            assertTrue
                (((Playlist.identifyFiles [ first ] |> Map.toArray).[0] |> fst)
                 <> ((Playlist.identifyFiles [ third ] |> Map.toArray).[0] |> fst))
                "Different bytes received the same identity"

            assertTrue (identified.ContainsKey(identified |> Map.toSeq |> Seq.head |> fst)) "Identified path missing"

            let duplicateIds =
                identified
                |> Map.toSeq
                |> Seq.map fst
                |> Seq.filter (fun value ->
                    value.StartsWith(
                        "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81:",
                        StringComparison.Ordinal
                    ))
                |> Seq.toArray

            assertEqual 2 duplicateIds.Length "Equal file contents did not receive occurrence identities"

            assertTrue
                (duplicateIds |> Array.exists (fun value -> value.EndsWith(":0"))
                 && duplicateIds |> Array.exists (fun value -> value.EndsWith(":1")))
                "Duplicate occurrence ordinals are wrong"
        finally
            Directory.Delete(root, true)

    let private hostTests () =
        let a, b, c = identifier "a" 0, identifier "b" 0, identifier "c" 0
        let message videos = { Session = session; Videos = videos }
        let host = HostPlaylist(session, 1UL, [| a; b; c |], Random(2))
        assertEqual "ready" host.Snapshot.Status "Host did not begin ready"
        let original = host.Snapshot
        assertEqual (Playlist.shuffle (Random(2)) [| a; b; c |]) original.Videos "Initial playlist was not shuffled"
        let clientsAdded = host.UpdateMembership [| 1UL; 2UL |] |> Option.get
        assertEqual "waiting" clientsAdded.Status "Joining client did not pause incomplete playlist"
        assertTrue (host.SubmitInventory(9UL, message [| a |]).IsNone) "Unconnected sender was accepted"

        assertTrue
            (host
                .SubmitInventory(
                    2UL,
                    { message [| a; b; c |] with
                        Session = "bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee" }
                )
                .IsNone)
            "Inventory from wrong session was accepted"

        assertTrue (host.SubmitInventory(2UL, message [| a; b; c |]).IsSome) "Valid inventory did not resume playlist"
        let resumed = host.Snapshot
        assertEqual "ready" resumed.Status "Playlist did not resume"
        assertEqual original.Cycle resumed.Cycle "Same-subset join restarted cycle"
        assertEqual original.Videos resumed.Videos "Same-subset join changed order"
        assertTrue (resumed.Generation > clientsAdded.Generation) "Resume did not advance generation"

        let shrink = HostPlaylist(session, 1UL, [| a; b; c |], Random(3))
        shrink.UpdateMembership [| 1UL; 2UL |] |> ignore
        let shrunk = shrink.SubmitInventory(2UL, message [| a; c |]) |> Option.get
        assertEqual "ready" shrunk.Status "Shrinking join did not resume"
        assertEqual 2 shrunk.Videos.Length "Intersection was not reduced"
        assertEqual 2L shrunk.Cycle "Shrinking join did not restart cycle"

        let empty = HostPlaylist(session, 1UL, [| a |], Random(4))
        empty.UpdateMembership [| 2UL |] |> ignore
        let emptySnapshot = empty.SubmitInventory(2UL, message [| b |]) |> Option.get
        assertEqual "empty" emptySnapshot.Status "Empty intersection was not reported"
        assertEqual 0 emptySnapshot.Videos.Length "Empty intersection contains videos"

        let membership = HostPlaylist(session, 1UL, [| a; b |], Random(5))
        let before = membership.Snapshot
        membership.UpdateMembership [| 1UL; 2UL |] |> ignore
        membership.SubmitInventory(2UL, message [| a |]) |> ignore
        let reduced = membership.Snapshot
        membership.UpdateMembership [| 1UL |] |> ignore
        assertEqual reduced.Videos membership.Snapshot.Videos "Disconnect expanded current playlist immediately"
        let nextCycle = membership.CompleteCycle() |> Option.get
        assertEqual 3L nextCycle.Cycle "Cycle completion failed to increment cycle"

        assertEqual
            (before.Generation + 3L)
            nextCycle.Generation
            "Snapshot generations did not advance across transitions"

        assertEqual 2 nextCycle.Videos.Length "Cycle completion did not apply deferred expansion"
        let reconnect = membership.UpdateMembership [| 1UL; 2UL |] |> Option.get
        assertEqual "waiting" reconnect.Status "Reconnect retained stale inventory"
        assertTrue (membership.SubmitInventory(2UL, message [| a; b |]).IsSome) "Reconnect inventory did not resume"
        assertTrue (membership.Snapshot.Generation > reconnect.Generation) "Reconnect did not publish a new generation"
        assertTrue (membership.CompleteCycle().IsSome) "Ready cycle could not complete"
        assertTrue (membership.CompleteCycle().IsSome) "Subsequent cycle could not complete"

        let simultaneous = HostPlaylist(session, 0UL, [| a; b; c |], Random(8))
        simultaneous.UpdateMembership [| 0UL; 2UL; 3UL |] |> ignore

        assertTrue
            (simultaneous.SubmitInventory(2UL, message [| a; b |]).IsNone)
            "Negotiation resumed before all inventories arrived"

        assertTrue (simultaneous.CompleteCycle().IsNone) "Incomplete inventory advanced a cycle"
        let submitted = [| a; c |]
        simultaneous.SubmitInventory(3UL, message submitted) |> ignore
        submitted.[0] <- b
        assertEqual [| a |] simultaneous.Snapshot.Videos "Client inventory was not intersected or defensively copied"
        let exposed = simultaneous.Snapshot
        exposed.Videos.[0] <- c
        assertEqual [| a |] simultaneous.Snapshot.Videos "Snapshot mutation changed host state"

        assertTrue
            (simultaneous.SubmitInventory(2UL, message [| a; b; c |]).IsNone)
            "Expanded inventory interrupted a ready cycle"

        assertEqual 2L simultaneous.Snapshot.Cycle "Expanded inventory reshuffled immediately"

        let pending = HostPlaylist(session, 0UL, [| a; b |], Random(9))
        let retained = pending.Snapshot.Videos
        pending.UpdateMembership [| 0UL; 2UL |] |> ignore
        let abandonedJoin = pending.UpdateMembership [| 0UL |] |> Option.get
        assertEqual "ready" abandonedJoin.Status "Departed unknown client left TV waiting"
        assertEqual retained abandonedJoin.Videos "Departed unknown client changed existing order"
        empty.UpdateMembership [| 1UL |] |> ignore
        assertEqual "ready" empty.Snapshot.Status "Empty intersection did not recover after restrictive client left"

    let private authorityTests () =
        let playlist = TransportAuthority.PlaylistIdentifier
        let inventory = TransportAuthority.InventoryIdentifier
        assertTrue (TransportAuthority.allowMessage false 0UL playlist 20) "Host snapshot rejected"
        assertTrue (not (TransportAuthority.allowMessage false 2UL playlist 20)) "Client forged host snapshot"
        assertTrue (not (TransportAuthority.allowMessage true 2UL playlist 20)) "Client modified host variable"
        assertTrue (not (TransportAuthority.allowMessage false 2UL playlist 68)) "Client forged initialization"
        assertTrue (TransportAuthority.allowMessage true 2UL inventory 34) "Inventory request rejected"
        assertTrue (not (TransportAuthority.allowMessage false 2UL inventory 34)) "Inventory delivered to non-host"
        assertTrue (not (TransportAuthority.allowMessage true 2UL inventory 18)) "Client forged server message"
        assertTrue (TransportAuthority.allowMessage false 2UL "Unrelated.Mod" 20) "Unrelated channel intercepted"

    [<EntryPoint>]
    let main _ =
        try
            protocolTests ()
            playlistTests ()
            hostTests ()
            authorityTests ()
            printfn "All TVRandomizer.Core regression checks passed."
            0
        with exceptionValue ->
            eprintfn "%s" (exceptionValue.ToString())
            1
