namespace TVRandomizer.Core

[<RequireQualifiedAccess>]
module TransportAuthority =
    [<Literal>]
    let PluginGuid = "Lesnomievi.TVRandomizer"

    [<Literal>]
    let PlaylistIdentifier = "Lesnomievi.TVRandomizer.Playlist.v1"

    [<Literal>]
    let InventoryIdentifier = "Lesnomievi.TVRandomizer.Inventory.v1"

    [<Literal>]
    let PositionIdentifier = "Lesnomievi.TVRandomizer.Position.v1"

    let private matches identifier value =
        value = identifier || value = PluginGuid + "." + identifier

    // The pinned API does not authenticate DataUpdate senders itself.
    // Restrict only our channels, before the API dispatches callbacks or changes variables.
    let allowMessage isServer sender identifier messageType =
        if matches PlaylistIdentifier identifier then
            sender = 0UL && (messageType = 20 || messageType = 36 || messageType = 68)
        elif matches PositionIdentifier identifier then
            sender = 0UL && messageType = 18
        elif matches InventoryIdentifier identifier then
            isServer && sender <> 0UL && messageType = 34
        else
            true
