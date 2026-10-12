namespace TVRandomizer

open System
open System.Collections.Generic
open System.Reflection
open UnityEngine
open UnityEngine.Video
open TVRandomizer.Core

/// Narrow bridge to the pinned TVLoader implementation. Playback stays in TVLoader.
type internal PlaybackBridge(loaderAssembly: Assembly, televisionType: Type) =
    let flags =
        BindingFlags.Public
        ||| BindingFlags.NonPublic
        ||| BindingFlags.Static
        ||| BindingFlags.Instance

    let patches = loaderAssembly.GetType("TVLoader.Patches.TVScriptPatches", true)
    let manager = loaderAssembly.GetType("TVLoader.Utils.VideoManager", true)

    let field (owner: Type) name =
        match owner.GetField(name, flags) with
        | null -> invalidOp (sprintf "Unsupported TVLoader/game contract: %s.%s" owner.FullName name)
        | value -> value

    let methodInfo (owner: Type) name =
        match owner.GetMethod(name, flags) with
        | null -> invalidOp (sprintf "Unsupported TVLoader/game contract: %s.%s" owner.FullName name)
        | value -> value

    let videos = field manager "Videos"
    let clip = field televisionType "currentClip"
    let clipTime = field televisionType "currentClipTime"
    let power = field televisionType "tvOn"
    let player = field televisionType "video"
    let nextPlayer = field patches "nextVideoPlayer"
    let currentPlayer = field patches "currentVideoPlayer"
    let played = field patches "tvHasPlayedBefore"
    let prepare = methodInfo patches "PrepareVideo"
    let play = methodInfo patches "PlayVideo"
    let initialize = methodInfo patches "Update"
    let setMaterial = methodInfo televisionType "SetTVScreenMaterial"
    let mutable television: obj = null
    let mutable desiredPower = false
    let mutable paused = false
    let mutable waitingForCycle = false
    let mutable appliedSession = ""
    let mutable appliedCycle = -1L
    let alive (value: UnityEngine.Object) = UnityEngine.Object.op_Implicit value

    let active () =
        not (isNull television) && alive (television :?> UnityEngine.Object)

    let video () =
        player.GetValue(television) :?> VideoPlayer

    let invoke (methodValue: MethodInfo) arguments =
        methodValue.Invoke(null, arguments) |> ignore

    let material on =
        setMaterial.Invoke(television, [| box on |]) |> ignore

    let invalidatePreload () =
        let prepared = nextPlayer.GetValue(null) :?> VideoPlayer

        if alive prepared then
            prepared.Stop()
            UnityEngine.Object.Destroy prepared

        nextPlayer.SetValue(null, null)

    let prepareAt index =
        invoke prepare [| television; box index |]

    let playPrepared () = invoke play [| television |]

    member _.OriginalPaths = (videos.GetValue(null) :?> List<string>).ToArray()
    member _.TelevisionType = televisionType
    member _.HasTelevision = active ()

    member _.Index =
        if active () then
            unbox<int> (clip.GetValue television)
        else
            0

    member _.Power =
        if paused || waitingForCycle then desiredPower
        elif active () then unbox<bool> (power.GetValue television)
        else false

    member _.HasPlayed = unbox<bool> (played.GetValue null)

    member _.Attach(instance: obj) =
        if not (Object.ReferenceEquals(television, instance)) then
            television <- instance
            desiredPower <- unbox<bool> (power.GetValue television)
            paused <- false
            waitingForCycle <- false
            appliedSession <- ""
            appliedCycle <- -1L
            // TVLoader's normal initialization also resets its player after a scene replacement.
            invoke initialize [| instance |]
            true
        else
            false

    member _.Pause(stop: bool) =
        if active () then
            if not paused && not waitingForCycle then
                desiredPower <- unbox<bool> (power.GetValue television)

            paused <- true
            let current = video ()

            if alive current then
                if stop then current.Stop() else current.Pause()

            if stop then
                desiredPower <- false

            power.SetValue(television, box false)
            material false

    member _.RecordPower(on: bool) =
        desiredPower <- on

        if active () then
            power.SetValue(television, box false)
            material false

    member _.CanToggle = not paused && not waitingForCycle

    member _.Apply(snapshot: PlaylistSnapshot, paths: Map<string, string>) =
        if snapshot.Status = "waiting" then
            if active () then
                if not paused && not waitingForCycle then
                    desiredPower <- unbox<bool> (power.GetValue television)

                paused <- true
                let current = video ()

                if alive current then
                    current.Pause()

                power.SetValue(television, box false)
                material false
        elif snapshot.Status = "empty" then
            let library = videos.GetValue(null) :?> List<string>
            library.Clear()
            invalidatePreload ()

            if active () then
                let current = video ()

                if alive current then
                    current.Stop()

                power.SetValue(television, box false)
                material false

            paused <- true
            desiredPower <- false
        else
            let localPaths = snapshot.Videos |> Array.map (fun identifier -> paths.[identifier])
            let library = videos.GetValue(null) :?> List<string>
            library.Clear()
            library.AddRange localPaths

            if active () then
                let restart = snapshot.Session <> appliedSession || snapshot.Cycle <> appliedCycle

                let on =
                    if paused || waitingForCycle then
                        desiredPower
                    else
                        unbox<bool> (power.GetValue television)

                paused <- false

                if restart then
                    waitingForCycle <- false
                    let current = video ()

                    if alive current then
                        current.Stop()

                    invalidatePreload ()
                    clip.SetValue(television, box 0)
                    clipTime.SetValue(television, box 0f)
                    played.SetValue(null, box false)
                    // Explicit index zero replaces the cached first clip from the previous permutation.
                    prepareAt 0

                    if on then
                        playPrepared ()
                elif on && not waitingForCycle then
                    let current = video ()

                    if alive current then
                        current.Play()

                power.SetValue(television, box on)
                material on
                appliedSession <- snapshot.Session
                appliedCycle <- snapshot.Cycle

    member _.IsCurrentSource(source: VideoPlayer) =
        active ()
        && alive source
        && UnityEngine.Object.op_Equality (source, currentPlayer.GetValue(null) :?> VideoPlayer)

    member _.AdvanceClip() =
        if active () then
            clip.SetValue(television, box (unbox<int> (clip.GetValue television) + 1))
            clipTime.SetValue(television, box 0f)
            playPrepared ()

    member _.WaitForHostCycle() =
        desiredPower <- unbox<bool> (power.GetValue television)
        waitingForCycle <- true

    member _.AlignPosition(index: int, on: bool, hasPlayed: bool) =
        if active () && not paused then
            let current = video ()

            if alive current then
                current.Stop()

            invalidatePreload ()
            clip.SetValue(television, box index)
            clipTime.SetValue(television, box 0f)
            played.SetValue(null, box hasPlayed)
            desiredPower <- on
            power.SetValue(television, box on)
            prepareAt (if on || not hasPlayed then index else index + 1)

            if on then
                playPrepared ()

            material on

    member _.Restore(paths: string array) =
        let library = videos.GetValue(null) :?> List<string>
        library.Clear()
        library.AddRange paths
        invalidatePreload ()
        appliedSession <- ""
        appliedCycle <- -1L
        waitingForCycle <- false
        paused <- false

        if active () then
            let current = video ()

            if alive current then
                current.Stop()

            power.SetValue(television, box false)
            clip.SetValue(television, box 0)
            clipTime.SetValue(television, box 0f)
            played.SetValue(null, box false)

            if paths.Length > 0 then
                prepareAt 0

            material false
