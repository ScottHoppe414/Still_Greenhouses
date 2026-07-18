/*
version 0.18.0
*/

namespace StillGreenhouses;

public sealed class StillGreenhousesConfig
{
    internal const int MinimumAffectedPlantPositions = 1;
    internal const int StandardMaximumAffectedPlantPositions = 128;
    internal const int MaximumAffectedPlantPositions = 512;
    internal const int DefaultAffectedPlantPositions = 128;

    public bool Enabled { get; set; } = true;
    public bool DebugLogging { get; set; } = false;

    // Client-only presentation toggle. The authoritative server system does
    // not use this value.
    public bool ClientVisualEnabled { get; set; } = true;

    // Client-side room-class filters. The server authoritatively classifies and
    // sends all supported enclosed RoomRegistry room types; each client chooses
    // which types receive the configured wind mesh.
    public bool ApplyToGreenhouses { get; set; } = true;
    public bool ApplyToCellars { get; set; } = true;
    public bool ApplyToRooms { get; set; } = true;

    // Client-side vegetation category filters. IsVegetationCandidate() owns
    // normal world vegetation categories. ApplyToOtherVegetation separately
    // enables the generic native-wind mesh fallback and container adapter.
    public bool ApplyToCrops { get; set; } = true;
    public bool ApplyToFlowers { get; set; } = true;
    public bool ApplyToHerbs { get; set; } = true;
    public bool ApplyToBerryBushes { get; set; } = true;
    public bool ApplyToTallPlants { get; set; } = true;
    public bool ApplyToVines { get; set; } = true;
    public bool ApplyToFruitTreeLeaves { get; set; } = true;

    // Any remaining rendered mesh with a native Vanilla WindMode may opt into
    // room wind through the generic wind-mesh fallback. This covers ferns,
    // bamboo, potted/container vegetation, and compatible modded plants without
    // hardcoding their block classes or adding assembly dependencies.
    public bool ApplyToOtherVegetation { get; set; } = true;

    // Ground-cover grasses (tallgrass-*, *-grass-*) are matched by block code
    // because they do not reliably report a BlockPlant runtime type or a
    // Plant/Leaves material. They get their own toggle.
    public bool ApplyToGrass { get; set; } = true;

    // Calm the surface motion of water source blocks inside enabled, already-
    // tracked room types. Water uses its own ranges below and does not inherit
    // the vegetation mode or vegetation range. Water alone never keeps a room
    // tracked; it rides the room set that vegetation already established.
    public bool ApplyToWater { get; set; } = true;

    // Legacy 0.17 uniform-array budget retained so existing config files remain
    // rollback compatible. The 0.18 spatial hash does not truncate vegetation
    // or make water compete for this budget.
    public int MaxAffectedPlants { get; set; } =
        DefaultAffectedPlantPositions;

    // Legacy 0.17 opt-in retained for rollback-compatible config files. It has
    // no effect on the dynamically sized 0.18 spatial-hash transport.
    public bool ExperimentalExtendedPositionCapacity { get; set; } = false;

    // Client-side visual behavior for vegetation inside an enabled authoritative
    // room region. The legacy JSON property name is retained for upgrade
    // compatibility. Both modes preserve the original WindMode/WindData and
    // use room-local Vanilla shader states. VanillaNoWind is fixed at exactly
    // 0%; VanillaLowWind uses the configured ranges, which default to 5%.
    // Legacy NoWind and older nonzero mode names are migrated during config
    // normalization.
    public string GreenhouseWindMode { get; set; } = "VanillaLowWind";

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

    // Client-side water surface movement ranges, independently expressed as a
    // percentage of Vanilla's surface-wind scale. These use the same fixed or
    // stepped range behavior as vegetation. Equal bounds produce one value.
    public float GreenhouseWaterWindLowerPercent { get; set; } = 5f;
    public float GreenhouseWaterWindUpperPercent { get; set; } = 5f;

    public float CellarWaterWindLowerPercent { get; set; } = 5f;
    public float CellarWaterWindUpperPercent { get; set; } = 5f;

    public float RoomWaterWindLowerPercent { get; set; } = 5f;
    public float RoomWaterWindUpperPercent { get; set; } = 5f;

    // Server-side minimum number of actual RoomRegistry interior positions
    // required before an enclosed room can become managed. This rejects tiny
    // enclosed voids inside vegetation canopies before the vegetation scan.
    public int MinimumManagedRoomInteriorPositions { get; set; } = 7;

    // Server-side authoritative room scans are rate limited on the main thread.
    public int MaxServerChunkScansPerTick { get; set; } = 1;

    // Maximum foreground time budget for one server scan scheduler tick. This
    // does not move RoomRegistry/world access off-thread; it prevents multiple
    // expensive scan/revalidation operations from being chained into one game
    // tick when MaxServerChunkScansPerTick is raised.
    public float ServerForegroundWorkBudgetMs { get; set; } = 4f;

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

    // Stage-1 shader proof. When enabled, every wind-enabled vegetation vertex
    // that reaches a wrapped Vanilla applyVertexWarping() call site is shifted
    // +0.75 blocks on X. No room-position lookup is required. This proves the
    // runtime terrain call-site wrapper itself is executing.
    public bool DebugRoomWindCallSiteProof { get; set; } = false;

    // Stage-2 shader proof. When enabled, only vegetation vertices that resolve
    // an uploaded managed-room envelope are lifted +0.35 blocks on Y. The
    // fixed displacement cannot hide at a sine zero crossing and is
    // intentionally obvious. The Stage-1 call-site proof takes precedence when
    // both proof settings are accidentally enabled.
    public bool DebugRoomWindVisualProof { get; set; } = false;

    // Client transport retry delay used when a chunk request cannot be sent or
    // when no server snapshot arrives before the pending request lease expires.
    // Incomplete authoritative snapshots are server-owned and are not polled.
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

    // Server subscriptions outside this horizontal chunk radius are pruned.
    // Complete negative subscriptions remain active inside the radius so a
    // later structural room change can publish an authoritative update.
    public int ServerSubscriptionRadiusChunks { get; set; } = 24;

    // Client-side foreground work is intentionally sliced across fixed game
    // ticks. Water source validation and chunk redraw requests are queued and
    // processed in bounded batches instead of running whole-room scans during a
    // snapshot commit.
    public int ClientForegroundWorkIntervalMs { get; set; } = 50;
    public int MaxClientWaterChecksPerTick { get; set; } = 256;
    public int MaxClientChunkRedrawsPerTick { get; set; } = 1;

    // Client cache pruning cadence.
    public int ClientCachePruneIntervalMs { get; set; } = 30000;

    // Client-side request and display toggle. The server still decides whether
    // the feature is permitted and authoritatively classifies the current room.
    public bool ShowRoomInspectionOverlay { get; set; } = false;

    // Server-side permission for clients to request room inspection results.
    // The client-side value of this property is never trusted.
    public bool AllowClientRoomInspectionOverlay { get; set; } = true;

    // Server-authoritative rendering radius returned to clients when no valid
    // enclosed Vanilla room exists. Clients do not choose their own scan radius.
    public int RoomInspectionFailureRadius { get; set; } = 14;
}
