/*
version 0.10.16a
*/

namespace StillGreenhouses;

public sealed class StillGreenhousesConfig
{
    public bool Enabled { get; set; } = true;
    public bool DebugLogging { get; set; } = false;

    // Client-only presentation toggle. The authoritative server system does
    // not use this value.
    public bool ClientVisualEnabled { get; set; } = true;

    // Client-side room-class filters. The server authoritatively classifies and
    // sends all supported enclosed RoomRegistry room types; each client chooses
    // which types receive the configured wind mesh.
    public bool ApplyToGreenhouses { get; set; } = true;
    public bool ApplyToCellars { get; set; } = false;
    public bool ApplyToRooms { get; set; } = false;

    // Client-side vegetation category filters. IsVegetationCandidate() is the
    // single authoritative policy used by every wind-modification path.
    public bool ApplyToCrops { get; set; } = true;
    public bool ApplyToFlowers { get; set; } = true;
    public bool ApplyToHerbs { get; set; } = true;
    public bool ApplyToBerryBushes { get; set; } = true;
    public bool ApplyToTallPlants { get; set; } = true;
    public bool ApplyToVines { get; set; } = true;
    public bool ApplyToFruitTreeLeaves { get; set; } = true;

    // Any remaining BlockCrop, BlockPlant, Plant-material, or Leaves-material
    // vegetation is eligible when its rendered mesh actually contains an
    // active Vanilla WindMode. This covers ferns and compatible modded
    // vegetation without hardcoding block-code families.
    public bool ApplyToOtherVegetation { get; set; } = true;

    // Ground-cover grasses (tallgrass-*, *-grass-*) are matched by block code
    // because they do not reliably report a BlockPlant runtime type or a
    // Plant/Leaves material. They get their own toggle.
    public bool ApplyToGrass { get; set; } = true;

    // Calm the surface motion of water source blocks inside enabled, already-
    // tracked room types, using that room type's wind range. A 0% range yields
    // still water. Water alone never keeps a room tracked; it rides the room set
    // that vegetation already established.
    public bool ApplyToWater { get; set; } = true;

    // Client-side visual behavior for vegetation inside an enabled authoritative
    // room region. The legacy JSON property name is retained for upgrade
    // compatibility. NoWind clears Vanilla wind bits. VanillaLowWind preserves
    // the original WindMode/WindData and supplies a room-local clone of
    // Vanilla's sustained 5% surface-wind shader state. Older nonzero mode
    // names are migrated to VanillaLowWind during config normalization.
    public string GreenhouseWindMode { get; set; } = "NoWind";

    // Client-side room-type wind ranges, expressed as percentages of Vanilla's
    // surface-wind scale. 5 means 5%, or 0.05 in the shader.
    //
    // Every enabled room of the same ManagedRoomType shares one environment
    // state. Lower == Upper produces a fixed sustained wind value. Unequal
    // values are stepped upward through the range and then back downward.
    public float GreenhouseWindLowerPercent { get; set; } = 5f;
    public float GreenhouseWindUpperPercent { get; set; } = 5f;

    public float CellarWindLowerPercent { get; set; } = 5f;
    public float CellarWindUpperPercent { get; set; } = 5f;

    public float RoomWindLowerPercent { get; set; } = 5f;
    public float RoomWindUpperPercent { get; set; } = 5f;

    // Server-side minimum number of actual RoomRegistry interior positions
    // required before an enclosed room can become managed. This rejects tiny
    // enclosed voids inside vegetation canopies before the vegetation scan.
    public int MinimumManagedRoomInteriorPositions { get; set; } = 7;

    // Server-side authoritative room scans are rate limited on the main thread.
    public int MaxServerChunkScansPerTick { get; set; } = 1;

    // Structural changes are delayed briefly so Vanilla's RoomRegistry can
    // invalidate/rebuild its own room data before Still Greenhouses rescans.
    public int ServerRescanDelayMs { get; set; } = 1000;

    // A live managed room that temporarily produces zero replacements remains
    // authoritative for this grace period. Repeated zero observations do not
    // confirm deletion early; a final anchor-driven rediscovery owns removal.
    public int ServerRoomDisappearanceGraceMs { get; set; } = 5000;

    // When the final disappearance rediscovery is incomplete, retain the last
    // authoritative positive room for at least this long before performing a
    // diagnostic recheck. Incomplete evidence never removes a positive room;
    // the retention checkpoint repeats until discovery becomes authoritative.
    public int ServerIncompleteRoomDisappearanceRetentionMs { get; set; } = 30000;

    // Diagnostic shader proof. When enabled, vegetation vertices that actually
    // match an uploaded managed-room position visibly oscillate sideways. This
    // is intentionally obvious and should remain false during normal gameplay.
    public bool DebugRoomWindVisualProof { get; set; } = false;

    // Client transport retry delay used only when a chunk request cannot be
    // sent or no server snapshot has arrived. Incomplete authoritative
    // snapshots are server-owned and are no longer polled by the client.
    public int ClientIncompleteRetryMs { get; set; } = 1000;

    // Maximum number of new greenhouse discovery requests sent by one client
    // per second. Requests are queued and sent nearest-player-first.
    public int MaxClientDiscoveryRequestsPerSecond { get; set; } = 2;

    // Maximum number of discovery chunks serialized into one network packet.
    // This is an internal performance setting and is not shown in ConfigLib.
    public int MaxChunkRequestsPerBatch { get; set; } = 2;

    // Unknown chunks may only enter greenhouse discovery within this horizontal
    // radius. Already-known snapshots may remain cached farther away.
    public int ClientDiscoveryRadiusChunks { get; set; } = 8;

    // Maximum number of unsent discovery candidates retained by the client.
    // When full, nearer candidates replace farther queued chunks.
    public int MaxQueuedDiscoveryChunks { get; set; } = 256;

    // Client snapshots and observed vegetation anchors outside this horizontal
    // chunk radius are pruned. Returning to the area rediscovers it normally.
    public int ClientCacheRadiusChunks { get; set; } = 24;

    // Positive/incomplete server subscriptions outside this horizontal chunk
    // radius are pruned. Complete negative subscriptions are one-shot.
    public int ServerSubscriptionRadiusChunks { get; set; } = 24;

    // Client cache pruning cadence.
    public int ClientCachePruneIntervalMs { get; set; } = 30000;
}
