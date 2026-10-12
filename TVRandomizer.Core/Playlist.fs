namespace TVRandomizer.Core

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography

[<RequireQualifiedAccess>]
module Playlist =
    let shuffle (random: Random) (videos: string array) : string array =
        let result = Array.copy videos

        for index = result.Length - 1 downto 1 do
            let swapIndex = random.Next(index + 1)
            let value = result.[index]
            result.[index] <- result.[swapIndex]
            result.[swapIndex] <- value

        result

    let identifyFiles (files: seq<string>) : Map<string, string> =
        use sha256 = SHA256.Create()
        let occurrences = Dictionary<string, int>(StringComparer.Ordinal)

        files
        |> Seq.map (fun path ->
            use stream = File.OpenRead(path)
            let digest = sha256.ComputeHash(stream)

            let hash =
                digest |> Array.map (fun value -> value.ToString("x2")) |> String.concat ""

            let occurrence =
                match occurrences.TryGetValue hash with
                | true, count -> count
                | false, _ -> 0

            if occurrence > 4095 then
                invalidArg "files" "A content hash occurs more than 4096 times."

            occurrences.[hash] <- occurrence + 1
            let identifier = sprintf "%s:%d" hash occurrence
            identifier, path)
        |> Map.ofSeq
