// CLR test doubles, not Unity or an executable TVLoader replacement.
namespace UnityEngine

[<AllowNullLiteral>]
type Object() =
    let mutable destroyed = false

    member _.Destroyed
        with get () = destroyed
        and set value = destroyed <- value

    static member op_Implicit(value: Object) =
        not (isNull value) && not value.Destroyed

    static member op_Equality(left: Object, right: Object) =
        let leftNull = isNull left || left.Destroyed
        let rightNull = isNull right || right.Destroyed

        if leftNull || rightNull then
            leftNull = rightNull
        else
            obj.ReferenceEquals(left, right)

    static member op_Inequality(left: Object, right: Object) = not (Object.op_Equality (left, right))

    static member Destroy(value: Object) =
        if not (isNull value) then
            value.Destroyed <- true

namespace UnityEngine.Video

[<AllowNullLiteral>]
type VideoPlayer() =
    inherit UnityEngine.Object()
    let mutable stopCount = 0
    let mutable pauseCount = 0
    let mutable playCount = 0
    member _.targetTexture = obj ()
    member _.Stop() = stopCount <- stopCount + 1
    member _.Pause() = pauseCount <- pauseCount + 1
    member _.Play() = playCount <- playCount + 1
    member _.StopCount = stopCount
    member _.PauseCount = pauseCount
    member _.PlayCount = playCount

namespace TVLoader.Utils

open System.Collections.Generic

type VideoManager private () =
    static let Videos = List<string>()
    static member VideoList = Videos

namespace TVLoader.Patches

open UnityEngine.Video

type TVScriptPatches private () =
    static let mutable nextVideoPlayer: VideoPlayer = null
    static let mutable currentVideoPlayer: VideoPlayer = null
    static let mutable tvHasPlayedBefore = false
    static let preparedIndices = ResizeArray<int>()
    static let preparedUrls = ResizeArray<string>()
    static let mutable prepareCalls = 0
    static let mutable playCalls = 0

    static member NextPlayer
        with get () = nextVideoPlayer
        and set value = nextVideoPlayer <- value

    static member CurrentPlayer = currentVideoPlayer
    static member PreparedIndices = preparedIndices
    static member PreparedUrls = preparedUrls
    static member PrepareCalls = prepareCalls
    static member PlayCalls = playCalls

    static member Reset() =
        preparedIndices.Clear()
        preparedUrls.Clear()
        nextVideoPlayer <- null
        currentVideoPlayer <- null
        tvHasPlayedBefore <- false
        prepareCalls <- 0
        playCalls <- 0

    static member Update(television: obj) =
        if not (UnityEngine.Object.op_Implicit currentVideoPlayer) then
            currentVideoPlayer <- VideoPlayer()
            television.GetType().GetField("video").SetValue(television, currentVideoPlayer)

    static member PrepareVideo(television: obj, index: int) =
        let videos = TVLoader.Utils.VideoManager.VideoList
        preparedIndices.Add(index)
        preparedUrls.Add(videos.[index % videos.Count])
        prepareCalls <- prepareCalls + 1
        nextVideoPlayer <- VideoPlayer()

    static member PlayVideo(television: obj) =
        playCalls <- playCalls + 1
        currentVideoPlayer <- nextVideoPlayer
        television.GetType().GetField("video").SetValue(television, currentVideoPlayer)
        tvHasPlayedBefore <- true
        // Model the existence of a subsequent cached player, without native preparation or playback.
        nextVideoPlayer <- VideoPlayer()
