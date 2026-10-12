namespace TVRandomizer.Playback.Tests

open System
open System.Collections.Generic
open System.Reflection
open TVRandomizer
open TVRandomizer.Core
open TVLoader.Patches
open TVLoader.Utils
open UnityEngine
open UnityEngine.Video

[<AllowNullLiteral>]
type FakeTelevision() =
    inherit Object()
    let mutable materialOn = false
    let mutable materialCalls = 0

    [<DefaultValue>]
    val mutable tvOn: bool

    [<DefaultValue>]
    val mutable video: VideoPlayer

    [<DefaultValue>]
    val mutable currentClip: int

    [<DefaultValue>]
    val mutable currentClipTime: float32

    member _.MaterialOn = materialOn
    member _.MaterialCalls = materialCalls

    member _.SetTVScreenMaterial(on: bool) =
        materialOn <- on
        materialCalls <- materialCalls + 1

module Program =
    let private assertTrue message condition =
        if not condition then
            failwith message

    let private assertEqual message expected actual =
        if expected <> actual then
            failwithf "%s. Expected %A, got %A" message expected actual

    let private snapshot session cycle status videos =
        { Session = session
          Generation = cycle
          Cycle = cycle
          Status = status
          Videos = videos }

    let private paths =
        Map.ofList [ "a", "/library/a.mp4"; "b", "/library/b.mp4"; "c", "/library/c.mp4" ]

    let private createContext () =
        TVScriptPatches.Reset()
        VideoManager.VideoList.Clear()
        VideoManager.VideoList.AddRange [ "/original/one.mp4"; "/original/two.mp4" ]
        let television = FakeTelevision()
        television.tvOn <- true
        let bridge = PlaybackBridge(Assembly.GetExecutingAssembly(), typeof<FakeTelevision>)
        bridge.Attach(television) |> ignore
        assertEqual "TVLoader initialization must not prepare a clip" 0 TVScriptPatches.PrepareCalls
        bridge, television

    let private testAttachedInitializesWithoutPreparing () =
        let bridge, television = createContext ()
        assertTrue "TV must be attached" bridge.HasTelevision
        assertEqual "Update supplies the actual player" television.video TVScriptPatches.CurrentPlayer
        assertEqual "Initial attach must not prepare" 0 TVScriptPatches.PrepareCalls

    let private testNewCycleInvalidatesAndStartsAtShuffledZero () =
        let bridge, television = createContext ()
        VideoManager.VideoList.Clear()
        VideoManager.VideoList.AddRange [ "/old/0"; "/old/1" ]
        TVScriptPatches.NextPlayer <- VideoPlayer()
        let stale = TVScriptPatches.NextPlayer
        bridge.Apply(snapshot "session" 1L "ready" [| "a"; "b"; "c" |], paths)
        assertTrue "old cached player must be destroyed" stale.Destroyed
        assertTrue "old cached player must be stopped first" (stale.StopCount = 1)

        assertEqual
            "the first item of the current shuffled list must be prepared"
            0
            (TVScriptPatches.PreparedIndices.[0])

        assertEqual
            "prepared URL must use the current shuffled permutation"
            "/library/a.mp4"
            TVScriptPatches.PreparedUrls.[0]

        assertEqual "new ready cycle plays the prepared clip" 1 TVScriptPatches.PlayCalls
        assertEqual "clip starts at index zero" 0 television.currentClip
        assertTrue "TV power is restored" television.tvOn

    let private testSameCycleResumeAndWaitingCycleBoundary () =
        let bridge, television = createContext ()
        bridge.Apply(snapshot "session" 1L "ready" [| "a"; "b" |], paths)
        let current = television.video
        television.currentClip <- 1
        television.currentClipTime <- 42.5f
        bridge.Apply(snapshot "session" 1L "waiting" [||], paths)
        assertEqual "waiting pauses current player" 1 current.PauseCount
        bridge.Apply(snapshot "session" 1L "ready" [| "b"; "a" |], paths)
        assertEqual "same-cycle resume must retain current index" 1 television.currentClip
        assertEqual "same-cycle resume must retain playback time" 42.5f television.currentClipTime
        assertEqual "same-cycle resume uses current player Play" 1 current.PlayCount
        assertEqual "same-cycle resume does not prepare again" 1 TVScriptPatches.PrepareCalls
        bridge.WaitForHostCycle()
        television.currentClip <- 1
        bridge.Apply(snapshot "session" 2L "ready" [| "c"; "a" |], paths)
        assertEqual "cycle boundary restarts at zero" 0 television.currentClip
        assertEqual "new cycle prepares its own shuffled first URL" "/library/c.mp4" TVScriptPatches.PreparedUrls.[1]
        assertEqual "cycle boundary starts the new prepared player" 2 TVScriptPatches.PlayCalls

    let private testWaitingClientDoesNotReplayOldCycleOnResume () =
        let bridge, _ = createContext ()
        bridge.Apply(snapshot "session" 1L "ready" [| "a"; "b" |], paths)
        bridge.WaitForHostCycle()
        bridge.Apply(snapshot "session" 1L "ready" [| "b"; "a" |], paths)
        assertEqual "same-cycle status resume while awaiting host cycle must not play" 1 TVScriptPatches.PlayCalls
        assertTrue "awaiting cycle retains desired power" bridge.Power
        bridge.Apply(snapshot "session" 2L "ready" [| "c"; "b" |], paths)
        assertEqual "new cycle may play after boundary" 2 TVScriptPatches.PlayCalls

    let private testQueuedPowerAndEmptyState () =
        let bridge, television = createContext ()
        bridge.Apply(snapshot "session" 1L "ready" [| "a"; "b" |], paths)
        bridge.Pause(false)
        bridge.RecordPower(true)
        assertTrue "paused controls are queued" (not bridge.CanToggle)
        bridge.Apply(snapshot "session" 2L "ready" [| "b"; "a" |], paths)
        assertTrue "queued power-on applies at cycle boundary" television.tvOn
        bridge.Pause(false)
        bridge.RecordPower(false)
        bridge.Apply(snapshot "session" 3L "ready" [| "a"; "b" |], paths)
        assertTrue "queued power-off applies at cycle boundary" (not television.tvOn)
        bridge.Apply(snapshot "session" 3L "empty" [||], paths)
        assertEqual "empty playlist clears videos" 0 VideoManager.VideoList.Count
        assertTrue "empty playlist powers TV off" (not television.tvOn)
        assertTrue "empty playlist stops current player" (television.video.StopCount > 0)

    let private testMissingLocalMappingThrows () =
        let bridge, _ = createContext ()
        let missing = snapshot "session" 1L "ready" [| "not-present" |]
        let mutable threw = false

        try
            bridge.Apply(missing, paths)
        with :? KeyNotFoundException ->
            threw <- true

        assertTrue "missing local path must throw instead of silently changing playlist" threw

    let private testInstanceReplacementAndDestroyedPlayerReset () =
        let bridge, first = createContext ()
        bridge.Apply(snapshot "session" 1L "ready" [| "a" |], paths)
        let replacement = FakeTelevision()
        replacement.tvOn <- true
        Object.Destroy first.video
        Object.Destroy first
        assertTrue "new scene instance attaches" (bridge.Attach(replacement))
        assertEqual "replacement runs TVLoader initialization" replacement.video TVScriptPatches.CurrentPlayer
        assertEqual "replacement initialization does not prepare" 1 TVScriptPatches.PrepareCalls
        assertEqual "new instance resets session tracking and prepares on apply" 0 replacement.currentClip
        bridge.Apply(snapshot "session" 1L "ready" [| "b" |], paths)
        assertEqual "new television begins at clip zero" 0 replacement.currentClip
        let staleCurrent = replacement.video
        Object.Destroy staleCurrent
        bridge.Apply(snapshot "session" 2L "ready" [| "c" |], paths)
        assertEqual "destroyed current player does not prevent cycle preparation" 3 TVScriptPatches.PrepareCalls
        assertTrue "original instance remains a distinct scene object" (not (obj.ReferenceEquals(first, replacement)))

    let private testLateJoinPositionAndCachedSkip () =
        let bridge, television = createContext ()
        bridge.AlignPosition(1, true, true)
        assertEqual "late join aligns clip index" 1 television.currentClip
        assertEqual "late join prepares current item when powered" 1 (TVScriptPatches.PreparedIndices.[0])
        assertEqual "late join starts aligned clip" 1 TVScriptPatches.PlayCalls
        bridge.AlignPosition(1, false, true)
        assertEqual "powered-off late join prepares next item" 2 (TVScriptPatches.PreparedIndices.[1])
        assertEqual "off alignment does not play" 1 TVScriptPatches.PlayCalls
        assertTrue "off alignment keeps TV off" (not television.tvOn)

    let private testRestoreOriginalPathsAndResetCache () =
        let bridge, television = createContext ()
        let originals = bridge.OriginalPaths
        bridge.Apply(snapshot "session" 1L "ready" [| "a"; "b" |], paths)
        let cached = TVScriptPatches.NextPlayer
        bridge.Restore originals

        assertEqual
            "restore reinstates original video paths"
            (Array.toList originals)
            (VideoManager.VideoList |> Seq.toList)

        assertTrue "restore destroys cached preload" cached.Destroyed
        assertEqual "restore resets clip index" 0 television.currentClip
        assertTrue "restore powers television off" (not television.tvOn)
        assertEqual "restore prepares original first item" originals.[0] TVScriptPatches.PreparedUrls.[1]

    [<EntryPoint>]
    let main _ =
        let tests =
            [ "attached initialization", testAttachedInitializesWithoutPreparing
              "new cycle preload", testNewCycleInvalidatesAndStartsAtShuffledZero
              "resume and cycle boundary", testSameCycleResumeAndWaitingCycleBoundary
              "waiting client resume", testWaitingClientDoesNotReplayOldCycleOnResume
              "queued power and empty", testQueuedPowerAndEmptyState
              "missing local mapping", testMissingLocalMappingThrows
              "scene replacement and destroyed player", testInstanceReplacementAndDestroyedPlayerReset
              "late join alignment", testLateJoinPositionAndCachedSkip
              "restore", testRestoreOriginalPathsAndResetCache ]

        try
            for name, test in tests do
                test ()
                printfn "PASS %s" name

            0
        with error ->
            eprintfn "FAIL %s" error.Message
            1
