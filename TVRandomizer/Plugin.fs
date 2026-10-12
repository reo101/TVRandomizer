namespace TVRandomizer

open System
open System.Reflection
open BepInEx
open BepInEx.Bootstrap
open HarmonyLib
open LethalNetworkAPI
open LethalNetworkAPI.Utils
open UnityEngine.Video
open TVRandomizer.Core

module internal PatchCallbacks =
    let mutable capture: obj -> unit = ignore
    let mutable toggle: obj -> bool -> bool = fun _ _ -> true
    let mutable finish: obj -> VideoPlayer -> bool = fun _ _ -> true
    let mutable synchronize: unit -> bool = fun () -> true
    let mutable authorize: uint64 -> string -> int -> bool = fun _ _ _ -> true

type internal Patches =
    static member Capture(__instance: obj) = PatchCallbacks.capture __instance
    static member Toggle(__instance: obj, on: bool) = PatchCallbacks.toggle __instance on
    static member Finish(__instance: obj, source: VideoPlayer) = PatchCallbacks.finish __instance source
    static member Synchronize() = PatchCallbacks.synchronize ()

    static member Authorize(__0: uint64, __1: string, __2: obj) =
        PatchCallbacks.authorize __0 __1 (Convert.ToInt32 __2)

[<BepInPlugin(TransportAuthority.PluginGuid, "TVRandomizer", "1.1.0")>]
[<BepInDependency("rattenbonkers.TVLoader", "1.1.2")>]
[<BepInDependency("LethalNetworkAPI", "3.4.2")>]
type TVRandomizerPlugin() as this =
    inherit BaseUnityPlugin()
    let harmony = Harmony(TransportAuthority.PluginGuid)
    let mutable pluginActive = false
    let mutable pluginLogger: BepInEx.Logging.ManualLogSource = null
    let mutable bridge: PlaybackBridge option = None
    let mutable paths: Map<string, string> = Map.empty
    let mutable originalPaths: string array = [||]
    let mutable host: HostPlaylist option = None
    let mutable pendingCompletionCycle: int64 option = None
    let mutable snapshot = Protocol.emptySnapshot ""
    let mutable inventorySentForSession = ""
    let mutable pendingPosition: PlaybackPosition option = None
    let pendingClients = System.Collections.Generic.HashSet<uint64>()
    let mutable flushPositions: unit -> unit = ignore
    let mutable stateVariable: LNetworkVariable<string array> option = None
    let mutable inventoryMessage: LNetworkMessage<string array> option = None
    let mutable positionMessage: LNetworkMessage<string array> option = None
    let mutable networkStarted: Action<bool> = null
    let mutable wasConnected = false
    let mutable nextMembershipCheck = 0f

    let isReady () =
        snapshot.Status = "ready" && snapshot.Videos |> Array.forall paths.ContainsKey

    let connected () =
        pluginActive && LNetworkUtils.IsConnected

    let isHost () =
        connected () && LNetworkUtils.IsHostOrServer

    let applyPosition () =
        match pendingPosition, bridge with
        | Some position, Some playback when
            isReady ()
            && playback.HasTelevision
            && position.Session = snapshot.Session
            && position.Generation = snapshot.Generation
            && position.Index < snapshot.Videos.Length
            ->
            playback.AlignPosition(position.Index, position.On, position.HasPlayed)
            pendingPosition <- None
        | _ -> ()

    let applySnapshot () =
        match bridge with
        | Some playback ->
            if snapshot.Status = "ready" && not (isReady ()) then
                playback.Pause(false)
            else
                playback.Apply(snapshot, paths)

            applyPosition ()
        | None -> ()

    let rec publish (value: PlaylistSnapshot option) =
        match value, stateVariable with
        | Some state, Some variable ->
            variable.Value <- Protocol.encodeSnapshot state

            match pendingCompletionCycle, host with
            | Some cycle, _ when state.Cycle > cycle -> pendingCompletionCycle <- None
            | Some cycle, Some authority when state.Status = "ready" && state.Cycle = cycle ->
                pendingCompletionCycle <- None
                publish (authority.CompleteCycle())
            | _ -> ()

            if snapshot.Status = "ready" then
                flushPositions ()
        | _ -> ()

    let receiveSnapshot (wire: string array) =
        match Protocol.decodeSnapshot wire with
        | Some state when
            snapshot.Session = ""
            || Protocol.acceptSnapshot snapshot.Session snapshot.Generation state
            ->
            snapshot <- state
            applySnapshot ()

            if state.Status = "empty" then
                pluginLogger.LogWarning("No shared TV videos. The TV is stopped; the lobby remains available.")
            elif state.Status = "waiting" then
                pluginLogger.LogWarning(
                    "TV paused while waiting for client inventories. All clients need TVRandomizer and its dependencies."
                )
            else
                pluginLogger.LogInfo(
                    sprintf
                        "Playlist cycle %d: %d shared videos. First video: %s"
                        state.Cycle
                        state.Videos.Length
                        state.Videos.[0]
                )

            if not (isHost ()) && inventorySentForSession <> state.Session then
                match inventoryMessage with
                | Some message ->
                    message.SendServer(
                        Protocol.encodeInventory
                            { Session = state.Session
                              Videos = paths |> Map.toArray |> Array.map fst }
                    )

                    inventorySentForSession <- state.Session
                | None -> ()
        | _ -> ()

    let sendPosition (clientId: uint64) =
        match bridge, positionMessage with
        | Some playback, Some message when isReady () ->
            let index = playback.Index

            if index >= 0 && index < snapshot.Videos.Length then
                message.SendClient(
                    Protocol.encodePosition
                        { Session = snapshot.Session
                          Generation = snapshot.Generation
                          Index = index
                          On = playback.Power
                          HasPlayed = playback.HasPlayed },
                    clientId
                )
        | _ -> ()

    let receiveInventory wire sender =
        match host, Protocol.decodeInventory wire with
        | Some authority, Some inventory when isHost () ->
            if
                inventory.Session = snapshot.Session
                && Array.contains sender LNetworkUtils.AllConnectedClients
            then
                pendingClients.Add sender |> ignore
                publish (authority.UpdateMembership LNetworkUtils.AllConnectedClients)
                publish (authority.SubmitInventory(sender, inventory))
                flushPositions ()
        | _ -> ()

    let receivePosition wire =
        match Protocol.decodePosition wire with
        | Some position when
            snapshot.Session = ""
            || (position.Session = snapshot.Session
                && position.Generation >= snapshot.Generation)
            ->
            pendingPosition <- Some position
            applyPosition ()
        | _ -> ()

    let resetSession () =
        host <- None
        pendingCompletionCycle <- None
        snapshot <- Protocol.emptySnapshot ""
        inventorySentForSession <- ""
        pendingPosition <- None
        pendingClients.Clear()

        match bridge with
        | Some playback -> playback.Restore originalPaths
        | None -> ()

    let startSession isServer =
        resetSession ()
        wasConnected <- true

        if isServer then
            let authority =
                HostPlaylist(Guid.NewGuid().ToString("D"), 0UL, paths |> Map.toArray |> Array.map fst, Random())

            host <- Some authority
            publish (Some authority.Snapshot)
            publish (authority.UpdateMembership LNetworkUtils.AllConnectedClients)
        else
            match stateVariable with
            | Some variable -> receiveSnapshot variable.Value
            | None -> ()

    let failClosed (exceptionValue: exn) =
        pluginLogger.LogError(exceptionValue)

        match bridge with
        | Some playback -> playback.Pause(true)
        | None -> ()

        snapshot <-
            { snapshot with
                Status = "waiting"
                Videos = [||] }

    let guard action =
        try
            action ()
        with exceptionValue ->
            failClosed exceptionValue

    member private _.Awake() =
        pluginLogger <- this.Logger

        try
            let loader = Chainloader.PluginInfos.["rattenbonkers.TVLoader"]

            if loader.Metadata.Version <> Version(1, 1, 2) then
                invalidOp "Only TVLoader 1.1.2 is supported by the checked private playback contract."

            if
                Chainloader.PluginInfos.["LethalNetworkAPI"].Metadata.Version
                <> Version(3, 4, 2)
            then
                invalidOp "Only LethalNetworkAPI 3.4.2 is supported by the checked dispatcher contract."

            let gameType =
                AppDomain.CurrentDomain.GetAssemblies()
                |> Array.pick (fun assembly -> assembly.GetType("TVScript") |> Option.ofObj)

            let playback = PlaybackBridge(loader.Instance.GetType().Assembly, gameType)
            bridge <- Some playback

            flushPositions <-
                fun () ->
                    if isHost () && isReady () && playback.HasTelevision then
                        for clientId in pendingClients |> Seq.toArray do
                            if Array.contains clientId LNetworkUtils.AllConnectedClients then
                                sendPosition clientId

                            pendingClients.Remove clientId |> ignore

            originalPaths <- playback.OriginalPaths

            if originalPaths.Length > Protocol.MaxVideos then
                invalidOp (
                    sprintf
                        "TVRandomizer supports at most %d playlist entries; refusing to truncate the library."
                        Protocol.MaxVideos
                )

            paths <- Playlist.identifyFiles originalPaths

            let callbacks = typeof<Patches>

            let patch target name before =
                let prefix =
                    HarmonyMethod(callbacks.GetMethod(name, BindingFlags.Static ||| BindingFlags.Public))

                prefix.priority <- Priority.First
                prefix.before <- before
                harmony.Patch(target, prefix = prefix) |> ignore

            let tvMethod name = AccessTools.Method(gameType, name)

            PatchCallbacks.capture <-
                fun instance ->
                    guard (fun () ->
                        if playback.Attach instance then
                            if connected () then
                                applySnapshot ()
                                flushPositions ()
                            else
                                playback.Pause(false))

            PatchCallbacks.toggle <-
                fun instance on ->
                    guard (fun () ->
                        if playback.Attach instance && connected () then
                            applySnapshot ())

                    if not (connected ()) || not (isReady ()) || not playback.CanToggle then
                        playback.RecordPower on
                        false
                    else
                        true

            PatchCallbacks.finish <-
                fun _ source ->
                    if connected () && isReady () && playback.Power && playback.IsCurrentSource source then
                        guard (fun () ->
                            if playback.Index = snapshot.Videos.Length - 1 then
                                if isHost () then
                                    host
                                    |> Option.iter (fun authority ->
                                        pendingCompletionCycle <- Some snapshot.Cycle
                                        publish (authority.UpdateMembership LNetworkUtils.AllConnectedClients)

                                        if isReady () && pendingCompletionCycle.IsSome then
                                            pendingCompletionCycle <- None
                                            publish (authority.CompleteCycle()))
                                else
                                    playback.WaitForHostCycle()
                            else
                                // Use TVLoader's player, but progress even when the host is inside the factory.
                                playback.AdvanceClip())

                    false
            // Native clip-index synchronization has no playlist generation and can overwrite a newly shuffled index.
            PatchCallbacks.synchronize <- fun () -> not (connected ())

            PatchCallbacks.authorize <-
                fun sender identifier kind -> TransportAuthority.allowMessage (isHost ()) sender identifier kind

            patch (tvMethod "Update") "Capture" [| "rattenbonkers.TVLoader" |]
            patch (tvMethod "TurnTVOnOff") "Toggle" [| "rattenbonkers.TVLoader" |]
            patch (tvMethod "TVFinishedClip") "Finish" [| "rattenbonkers.TVLoader" |]
            patch (tvMethod "SyncTimeAndClipWithClients") "Synchronize" [||]

            let dispatcher =
                typeof<LNetworkUtils>.Assembly.GetType("LethalNetworkAPI.Internal.UnnamedMessageHandler", true)

            patch (AccessTools.Method(dispatcher, "HandleMessage")) "Authorize" [||]

            stateVariable <-
                Some(
                    LNetworkVariable<string array>
                        .Connect(
                            TransportAuthority.PlaylistIdentifier,
                            [||],
                            LNetworkVariableWritePerms.Server,
                            Action<string array, string array>(fun _ state -> guard (fun () -> receiveSnapshot state))
                        )
                )

            inventoryMessage <-
                Some(
                    LNetworkMessage<string array>
                        .Connect(
                            TransportAuthority.InventoryIdentifier,
                            Action<string array, uint64>(fun wire sender ->
                                guard (fun () -> receiveInventory wire sender)),
                            null,
                            null
                        )
                )

            positionMessage <-
                Some(
                    LNetworkMessage<string array>
                        .Connect(
                            TransportAuthority.PositionIdentifier,
                            null,
                            Action<string array>(fun wire -> guard (fun () -> receivePosition wire)),
                            null
                        )
                )

            networkStarted <- Action<bool>(fun isServer -> guard (fun () -> startSession isServer))
            LNetworkUtils.add_OnNetworkStart networkStarted
            pluginActive <- true

            if LNetworkUtils.IsConnected then
                startSession LNetworkUtils.IsHostOrServer

            this.Logger.LogInfo(sprintf "TVRandomizer 1.1.0 loaded: %d hashed video entries." paths.Count)
        with exceptionValue ->
            pluginActive <- false
            harmony.UnpatchSelf()

            this.Logger.LogError(
                sprintf "TVRandomizer could not initialize; TVLoader remains unchanged. %s" (exceptionValue.ToString())
            )

    member private _.Update() =
        if pluginActive then
            guard (fun () ->
                let online = LNetworkUtils.IsConnected

                if wasConnected && not online then
                    resetSession ()

                wasConnected <- online

                if online then
                    match stateVariable with
                    | Some variable when variable.IsInitialized -> receiveSnapshot variable.Value
                    | _ -> ()

                    if isHost () && UnityEngine.Time.unscaledTime >= nextMembershipCheck then
                        nextMembershipCheck <- UnityEngine.Time.unscaledTime + 0.25f

                        host
                        |> Option.iter (fun authority ->
                            publish (authority.UpdateMembership LNetworkUtils.AllConnectedClients)))

    member private _.OnDestroy() =
        pluginActive <- false

        if not (isNull networkStarted) then
            LNetworkUtils.remove_OnNetworkStart networkStarted

        stateVariable |> Option.iter (fun variable -> variable.Dispose())
        inventoryMessage |> Option.iter (fun message -> message.ClearSubscriptions())
        positionMessage |> Option.iter (fun message -> message.ClearSubscriptions())
        harmony.UnpatchSelf()
        bridge |> Option.iter (fun playback -> playback.Restore originalPaths)
        PatchCallbacks.capture <- ignore
        PatchCallbacks.toggle <- fun _ _ -> true
        PatchCallbacks.finish <- fun _ _ -> true
        PatchCallbacks.synchronize <- fun () -> true
        PatchCallbacks.authorize <- fun _ _ _ -> true
