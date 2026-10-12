module TVRandomizer.Contracts.Tests.Program

open System
open System.IO
open System.IO.Compression
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open System.Text
open System.Text.Json

let require condition message =
    if not condition then
        failwith message

let inspectAssembly path inspect =
    use stream = File.OpenRead path
    use executable = new PEReader(stream)
    require executable.HasMetadata ("Not a managed assembly: " + path)
    inspect (executable.GetMetadataReader())

let definition (reader: MetadataReader) fullName =
    reader.TypeDefinitions
    |> Seq.map reader.GetTypeDefinition
    |> Seq.find (fun value ->
        let namespaceName = reader.GetString value.Namespace
        let name = reader.GetString value.Name

        (if namespaceName = "" then
             name
         else
             namespaceName + "." + name) = fullName)

let fields (reader: MetadataReader) (value: TypeDefinition) =
    value.GetFields()
    |> Seq.map (fun handle -> reader.GetString((reader.GetFieldDefinition handle).Name))
    |> Set.ofSeq

let requireMethod (reader: MetadataReader) (value: TypeDefinition) name parameterCount =
    let exists =
        value.GetMethods()
        |> Seq.map reader.GetMethodDefinition
        |> Seq.exists (fun methodValue ->
            reader.GetString methodValue.Name = name
            && (methodValue.GetParameters()
                |> Seq.filter (fun handle -> (reader.GetParameter handle).SequenceNumber <> 0)
                |> Seq.length) = parameterCount)

    require exists (sprintf "Missing method contract: %s/%d" name parameterCount)

let loaderContract path =
    inspectAssembly path (fun reader ->
        let manager = definition reader "TVLoader.Utils.VideoManager"
        require ((fields reader manager).Contains "Videos") "TVLoader video library field changed"
        let patches = definition reader "TVLoader.Patches.TVScriptPatches"

        for fieldName in
            [ "currentVideoPlayer"
              "nextVideoPlayer"
              "renderTexture"
              "tvHasPlayedBefore" ] do
            require ((fields reader patches).Contains fieldName) ("TVLoader field changed: " + fieldName)

        for name, count in
            [ "PrepareVideo", 2
              "PlayVideo", 1
              "Update", 1
              "TurnTVOnOff", 2
              "TVFinishedClip", 2 ] do
            requireMethod reader patches name count)

let networkContract path =
    inspectAssembly path (fun reader ->
        let dispatcher = definition reader "LethalNetworkAPI.Internal.UnnamedMessageHandler"
        requireMethod reader dispatcher "HandleMessage" 5
        let enumDefinition = definition reader "LethalNetworkAPI.Internal.EMessageType"

        for name, expected in
            [ "Message", 2
              "Variable", 4
              "ServerMessage", 16
              "ClientMessage", 32
              "DataUpdate", 16
              "OwnershipUpdate", 32
              "Initialize", 64 ] do
            let fieldValue =
                enumDefinition.GetFields()
                |> Seq.map reader.GetFieldDefinition
                |> Seq.find (fun value -> reader.GetString value.Name = name)

            let constant = reader.GetConstant(fieldValue.GetDefaultValue())
            let mutable blob = reader.GetBlobReader constant.Value
            require (blob.ReadInt32() = expected) ("Network message flag changed: " + name))

let pluginContract path =
    inspectAssembly path (fun reader ->
        let plugin = definition reader "TVRandomizer.TVRandomizerPlugin"
        requireMethod reader plugin "Awake" 0
        requireMethod reader plugin "Update" 0
        requireMethod reader plugin "OnDestroy" 0
        let patches = definition reader "TVRandomizer.Patches"

        for name, count in [ "Capture", 1; "Toggle", 2; "Finish", 2; "Synchronize", 0; "Authorize", 3 ] do
            requireMethod reader patches name count

        let framework =
            reader.CustomAttributes
            |> Seq.map reader.GetCustomAttribute
            |> Seq.tryPick (fun attribute ->
                if attribute.Constructor.Kind = HandleKind.MemberReference then
                    let constructor =
                        reader.GetMemberReference(MemberReferenceHandle.op_Explicit attribute.Constructor)

                    if constructor.Parent.Kind = HandleKind.TypeReference then
                        let typeReference =
                            reader.GetTypeReference(TypeReferenceHandle.op_Explicit constructor.Parent)

                        if reader.GetString typeReference.Name = "TargetFrameworkAttribute" then
                            let mutable blob = reader.GetBlobReader attribute.Value
                            blob.ReadUInt16() |> ignore
                            Some(blob.ReadSerializedString())
                        else
                            None
                    else
                        None
                else
                    None)

        require (framework = Some ".NETStandard,Version=v2.1") "Plugin targets the wrong host runtime"

        let attributes =
            plugin.GetCustomAttributes()
            |> Seq.choose (fun handle ->
                let attribute = reader.GetCustomAttribute handle

                if attribute.Constructor.Kind <> HandleKind.MemberReference then
                    None
                else
                    let constructor =
                        reader.GetMemberReference(MemberReferenceHandle.op_Explicit attribute.Constructor)

                    if constructor.Parent.Kind <> HandleKind.TypeReference then
                        None
                    else
                        let reference =
                            reader.GetTypeReference(TypeReferenceHandle.op_Explicit constructor.Parent)

                        let name = reader.GetString reference.Name

                        if name = "BepInPlugin" || name = "BepInDependency" then
                            let mutable blob = reader.GetBlobReader attribute.Value
                            require (blob.ReadUInt16() = 1us) "Invalid plugin attribute encoding"
                            let identifier = blob.ReadSerializedString()
                            let second = blob.ReadSerializedString()

                            let third =
                                if name = "BepInPlugin" then
                                    blob.ReadSerializedString()
                                else
                                    ""

                            Some(name, identifier, second, third)
                        else
                            None)
            |> Set.ofSeq

        require
            (attributes.Contains("BepInPlugin", "Lesnomievi.TVRandomizer", "TVRandomizer", "1.1.0"))
            "Plugin discovery metadata differs from manifest"

        require
            (attributes.Contains("BepInDependency", "rattenbonkers.TVLoader", "1.1.2", ""))
            "TVLoader dependency missing"

        require
            (attributes.Contains("BepInDependency", "LethalNetworkAPI", "3.4.2", ""))
            "Networking dependency missing")

let gameContract path =
    inspectAssembly path (fun reader ->
        let television = definition reader "TVScript"

        for name in [ "currentClip"; "currentClipTime"; "tvOn"; "video" ] do
            require ((fields reader television).Contains name) ("Game TV field changed: " + name)

        for name, count in
            [ "Update", 0
              "TurnTVOnOff", 1
              "TVFinishedClip", 1
              "SetTVScreenMaterial", 1
              "SyncTimeAndClipWithClients", 3 ] do
            requireMethod reader television name count)

let archiveContract path =
    use archive = ZipFile.OpenRead path

    let files =
        archive.Entries
        |> Seq.filter (fun entry -> not (entry.FullName.EndsWith "/"))
        |> Seq.map (fun entry -> entry.FullName)
        |> Set.ofSeq

    let expected =
        Set.ofList
            [ "README.md"
              "LICENSE"
              "FSharp.Core.LICENSE"
              "manifest.json"
              "icon.png"
              "BepInEx/plugins/TVRandomizer/TVRandomizer.dll"
              "BepInEx/plugins/TVRandomizer/TVRandomizer.Core.dll"
              "BepInEx/plugins/TVRandomizer/FSharp.Core.dll" ]

    require (files = expected) (sprintf "Unexpected package contents: %A" files)
    use manifestStream = archive.GetEntry("manifest.json").Open()
    use manifest = JsonDocument.Parse manifestStream
    let root = manifest.RootElement
    require (root.GetProperty("name").GetString() = "TVRandomizer") "Invalid package name"
    require (root.GetProperty("version_number").GetString() = "1.1.0") "Inconsistent package version"
    require (root.GetProperty("description").GetString().Length <= 250) "Description exceeds Thunderstore limit"

    let dependencies =
        root.GetProperty("dependencies").EnumerateArray()
        |> Seq.map (fun value -> value.GetString())
        |> Set.ofSeq

    require
        (dependencies = Set.ofList
            [ "BepInEx-BepInExPack-5.4.2100"
              "Rattenbonkers-TVLoader-1.1.2"
              "xilophor-LethalNetworkAPI-3.4.2" ])
        "Incorrect package dependencies"

    use iconStream = archive.GetEntry("icon.png").Open()
    let header = Array.zeroCreate<byte> 24
    iconStream.ReadExactly header
    require (header.[0..7] = [| 137uy; 80uy; 78uy; 71uy; 13uy; 10uy; 26uy; 10uy |]) "Icon is not PNG"

    let dimension offset =
        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(ReadOnlySpan<byte>(header, offset, 4))

    require (dimension 16 = 256 && dimension 20 = 256) "Icon must be 256x256"
    use readmeStream = archive.GetEntry("README.md").Open()
    use reader = new StreamReader(readmeStream, UTF8Encoding(false, true))
    require (reader.ReadToEnd().Length > 0) "README is empty"

[<EntryPoint>]
let main arguments =
    try
        require
            (arguments.Length = 4 || arguments.Length = 5)
            "Expected TVLoader DLL, NetworkAPI DLL, mod ZIP, plugin DLL, and optional game DLL."

        loaderContract arguments.[0]
        networkContract arguments.[1]
        archiveContract arguments.[2]
        pluginContract arguments.[3]

        if arguments.Length = 5 then
            gameContract arguments.[4]

        printfn "Pinned loader contracts and Thunderstore archive checks passed. No game code executed."
        0
    with exceptionValue ->
        eprintfn "%s" (exceptionValue.ToString())
        1
