# Still Greenhouses 4.0.0

Still Greenhouses gives vegetation and exposed water inside Vintage Story rooms a configurable indoor wind environment instead of always using the global outdoor ground wind. The server identifies valid rooms through Vintage Story's authoritative `RoomRegistry`; each client decides which room types and visual categories it wants to render with room-local wind.

The mod also treats butterflies inside managed rooms as sheltered from rain and strong wind, allowing them to spend more time flying rather than immediately behaving as though they were outdoors in poor weather.

## Requirements

- Vintage Story 1.22.0 or later in the 1.22 series.
- The base `game` and `survival` modules.
- Still Greenhouses must be installed on both the server and every connecting client.
- ConfigLib is optional. Without ConfigLib, edit `stillgreenhouses.json` directly.

The generated configuration file is named:

```text
stillgreenhouses.json
```

It is stored in the Vintage Story `ModConfig` directory.

## What the mod can do

- Detect authoritative vanilla greenhouses, cellars, and normal rooms on the server.
- Give vegetation inside enabled room types a client-configurable wind range.
- Give exposed full freshwater source blocks an independent water-movement range.
- Use different movement ranges for greenhouses, cellars, and normal rooms.
- Use different movement ranges for plants and water in the same room.
- Hold movement at a fixed value or cycle it through a lower/upper range.
- Provide a true no-wind vegetation mode that suppresses residual movement from vanilla wind modes.
- Preserve each mesh's original vanilla `WindMode` and `WindData`, so supported plants retain their normal style of movement.
- Support crops, flowers, herbs, berry bushes, tall plants and reeds, vines, fruit-tree foliage, bamboo, ferns, potted/container plants, compatible leaves, and other wind-enabled vegetation.
- Support compatible modded vegetation when its rendered mesh contains vanilla wind flags.
- Keep room membership authoritative on the server while allowing each client to choose its own visual room/category filters.
- Revalidate rooms after blocks are placed, broken, or used.
- Retain a room briefly during structural edits so repeatedly building or merging walls does not immediately remove and recreate the effect.
- Treat butterflies inside managed rooms as having calm wind and no precipitation for their rest decisions.
- Extend ordinary butterfly wandering while the butterfly remains inside a managed room.

## What the mod does not do

- It does not create a new room system. It uses Vintage Story's existing `RoomRegistry` and vanilla room classifications.
- It does not change whether a room receives vanilla greenhouse growth or cellar food-preservation behavior.
- It does not change the server's actual weather, global wind, rainfall, or climate values.
- It does not change windmill power, sailing, projectiles, player movement, or other gameplay systems that use wind.
- Plant and water changes are visual shader effects. They do not create a separate physical wind simulation.
- It does not remove rain particles, weather sounds, or other outdoor effects for players standing inside a room.
- The butterfly shelter behavior applies only to the patched butterfly AI checks; it does not make every entity consider the room dry or calm.
- Water alone cannot establish or keep a managed room. A room must contain qualifying discovery-anchor vegetation.
- It only adjusts exposed, full freshwater source blocks. It does not manage flowing water, partial liquid levels, other liquids, or source blocks covered by another liquid block.
- A block whose rendered mesh has no active vanilla wind mode cannot be made to sway by this mod alone.
- Generic tree-canopy leaves may receive room wind after another qualifying plant establishes the room, but ordinary canopy leaves cannot establish or preserve a managed room by themselves.
- The 4.0.0 release does not include a room-debug overlay or a replacement for `/debug rooms hi`.
- The grass option is included for compatibility and experimentation, but the 4.0.0 ConfigLib text labels it work in progress and non-functional.
- The legacy 128/512 position settings no longer control the active 4.0.0 spatial-hash system.

# Architecture

Still Greenhouses is divided into an authoritative server system and a client rendering system.

- The **server** decides which enclosed regions are valid managed rooms and sends their geometry and type to subscribed clients.
- The **client** observes rendered vegetation, checks it against the server-provided room geometry, registers affected mesh envelopes, and supplies room-local states to the game's shaders.

The server never trusts a client to declare that a room is valid. The client never attempts to replace the server's room classification.

## Server room identification

### 1. Room discovery starts from vegetation anchors

The server does not call `RoomRegistry.GetRoomForPosition()` for every block in every chunk. Instead, it scans loaded chunk block IDs for a narrower set of **room discovery anchors**.

Discovery anchors include:

- crops;
- `BlockPlant` vegetation;
- plant-material blocks;
- recognized grass families;
- berry bushes;
- vines;
- fruit-tree branch/foliage families.

Generic leaves may be visually affected on the client, but normal tree canopy leaves are deliberately excluded as server discovery anchors. This prevents a naturally generated leaf canopy from establishing or preserving an accidental managed room by itself.

When the scanner finds an anchor, it asks vanilla's `RoomRegistry` for the room containing that position. Once a room is found, the scanner marks the room's positions within that chunk as covered so that additional plants in the same room do not cause redundant room queries.

Discovery work is sliced across server ticks according to the configured scan count and foreground time budget.

### 2. Vanilla room validation

A `RoomRegistry` result is rejected when:

- any part of the room depends on an unloaded chunk; or
- the room has one or more exits according to vanilla's room data.

A complete enclosed room is classified as follows:

1. **Greenhouse** — `SkylightCount` is greater than `NonSkylightCount`.
2. **Cellar** — the room is not a greenhouse and vanilla marks it as `IsSmallRoom`.
3. **Normal room** — the room is enclosed and complete but does not meet the greenhouse or cellar condition.

These are vanilla-derived classifications. Still Greenhouses does not redefine their measurements.

### 3. Additional Still Greenhouses viability checks

A vanilla-valid room must also satisfy two mod-specific conditions:

- Its exact `PosInRoom` occupancy count must be at least `MinimumManagedRoomInteriorPositions`.
- It must contain at least one qualifying discovery-anchor vegetation block.

The default minimum is seven occupied interior positions. The size requirement rejects tiny enclosed pockets, including small voids that can appear inside vegetation or unusual block arrangements.

### 4. Authoritative room representation

For every managed room, the server stores:

- dimension;
- minimum and maximum X/Y/Z bounds;
- vanilla room type: greenhouse, cellar, or normal room;
- a cloned `PosInRoom` occupancy bitset;
- occupied-position count;
- a shape hash calculated from the occupancy bitset.

The occupancy bitset is important because a room is not assumed to fill every coordinate in its rectangular bounds. The client receives the exact occupied cells rather than only a bounding box.

Each room is indexed under every chunk it intersects, allowing clients to request and subscribe by chunk.

### 5. Structural updates and room removal

The server listens for:

- block placement;
- block breaking;
- block use events that may alter room structure, such as doors;
- chunk-dirty notifications.

Structural checks are delayed by `ServerRescanDelayMs` so vanilla's `RoomRegistry` can rebuild its own data before Still Greenhouses queries it.

When an existing managed room temporarily disappears, Still Greenhouses does not immediately delete it. It starts a disappearance grace period. After the grace period, the server performs an anchor-driven authoritative rediscovery before confirming removal.

If the final rediscovery is incomplete because required chunks are unloaded, the last valid positive room is retained. An incomplete result never removes a previously confirmed room; the server repeats the check after the configured incomplete-retention interval.

Removed rooms are also retained temporarily as structural recovery anchors. This helps the server rediscover a rebuilt room after walls are altered even when the surrounding chunk contains no other currently managed room.

### 6. Butterfly room weather

Butterfly behavior is evaluated on the server against the same managed-room cache.

Inside a managed room:

- butterfly rest checks receive a calm wind value;
- butterfly rest checks receive zero precipitation;
- the ordinary wander task is lengthened within a bounded duration.

Chase and flee tasks are not modified by the wander-duration adjustment.

## Client management of wind-affected objects

### 1. The client observes rendered vegetation

Harmony patches cover several vanilla tessellation paths, including:

- general block JSON tessellation;
- tall plants and reeds;
- fruiting-bush cached meshes;
- plant containers and potted plants;
- other block entities that submit wind-enabled contained vegetation meshes.

A block must pass both of these checks:

1. Its category is enabled by the client's configuration, or it qualifies for the generic wind-mesh fallback.
2. Its rendered mesh contains at least one nonzero vanilla `WindMode` flag.

The client records representative vegetation positions by chunk. Encountering vegetation in a chunk whose room state is unknown causes the client to queue an authoritative chunk request.

### 2. The client checks server-provided room membership

A vegetation position is affected only when:

- the client has a server snapshot for the containing chunk;
- an enabled room region in that snapshot contains the exact position; and
- the relevant visual/category settings are enabled.

Container meshes may extend into the interior cell above the container block. For these objects, the client checks both the container position and the block immediately above it.

### 3. Mesh envelopes are measured, not hard-coded

For a qualifying mesh, the client examines the actual vertices whose vanilla wind flags are enabled. It calculates a local axis-aligned envelope around those wind-enabled vertices.

The envelope may be expanded to account for:

- vanilla randomized Y rotation;
- vanilla random draw offsets;
- transforms applied by a block-entity mesh pool;
- contained vegetation that extends above its block position.

The original vanilla wind mode and wind data are preserved. The mod does not replace each plant with a single generic sway animation.

### 4. Water is discovered separately

After a room snapshot is committed, the client scans the configured room positions in bounded batches.

A water target is registered only when:

- `ApplyToWater` is enabled;
- the fluid-layer block is full freshwater with liquid level 7;
- the block above does not contain liquid, meaning the source has an exposed surface; and
- the source position belongs to an enabled server-authoritative room.

Water uses a full-block target envelope and a separate room-state range from vegetation.

### 5. Dynamic quarter-block spatial hash

The original implementation uploaded a fixed uniform array of spatial envelopes and tested each wind-enabled vertex against as many as 128 entries. Version 4.0.0 replaces that system with CPU preprocessing and a dynamically sized GPU lookup texture.

The client:

1. Rasterizes registered vegetation and water envelopes into quarter-block cells using `floor(worldCoordinate * 4)`.
2. Stores the cells in an open-addressed hash table.
3. Encodes each table slot into two RGBA8 texels.
4. Stores whether the cell affects vegetation, water, or both, plus the applicable room-state index.
5. Uploads the resulting texture only when registration topology changes.

Coordinates are stored relative to a selected hash origin using signed 16-bit components. The GLSL resolver reconstructs the vertex's absolute quarter-block coordinate from Vintage Story's render-relative coordinate space, hashes it, and performs at most eight probes.

This changes the per-vertex lookup from a linear scan over a fixed envelope list to a bounded, approximately constant-time texture lookup. Vegetation and water no longer compete for a shared 128-position budget.

### 6. Room-local shader states

The client maintains six shared runtime states:

- greenhouse vegetation;
- cellar vegetation;
- normal-room vegetation;
- greenhouse water;
- cellar water;
- normal-room water.

All rooms of the same type share that type's current client-side state. The mod does not maintain a different random wind value for every individual room.

Each state contains:

- current wind percentage;
- normalized shader wind speed;
- regular wind phase;
- high-frequency wind phase;
- a marker used for exact no-wind behavior.

When lower and upper bounds are equal, the state is fixed. When they differ, the value steps up and down through the range. The implementation uses one-percentage-point triangular steps at a 12-second interval, with narrower ranges using their full span as the step.

The three room types use phase offsets so their range transitions do not all begin at the same moment.

### 7. Vanilla deformation remains responsible for plant movement

The client generates a shader override from the exact base-game `vertexwarp.vsh` available to that installation. It wraps the existing vanilla call sites rather than shipping a completely unrelated vegetation shader.

For a managed vegetation vertex, the shader substitutes the room-local environmental wind speed and phase counters before vanilla's native `WindMode` switch runs. Vanilla still determines the deformation style for the vertex's original mode.

`VanillaNoWind` supplies an exact zero state and suppresses residual movement that some vanilla modes can retain even when ordinary wind speed is zero.

Liquid deformation is patched separately. It retains vanilla's noise-based water shape while using the room-local water state for the wind-affected phase and intensity.

### 8. Snapshot reconciliation and redraws

When a newer server snapshot changes room membership, the client:

- updates its chunk cache;
- retains registrations that still belong to a valid enabled room;
- updates room identity or room type when needed;
- removes stale vegetation and water targets;
- queues a bounded exposed-water rescan;
- queues chunk redraws when newly managed content needs to be re-tessellated.

Client foreground work, water validation, redraws, discovery queues, and cache pruning are all bounded by configuration settings to avoid performing an entire large update in one frame.

# Client/server communication

Still Greenhouses 4.0.0 uses the network channel:

```text
stillgreenhouses-cache-v4
```

Packets are serialized with ProtoBuf.

## Client request

`GreenhouseChunkBatchRequest` contains a list of chunk coordinates:

- chunk X;
- chunk Y;
- chunk Z;
- dimension.

The client queues unknown chunks nearest-player-first, sends a limited number of new requests per second, and places a limited number of chunks in each packet.

The server rejects unreasonable remote requests. A requested chunk must be in the same dimension and within a fixed safety distance of the requesting player.

## Server snapshot

`GreenhouseChunkSnapshot` contains:

- chunk coordinates and dimension;
- `Revision` — changes when authoritative chunk state changes;
- `VisualRevision` — changes when room-wind classification changes in a way that may require visual reconciliation;
- `Complete` — whether the server currently has authoritative complete data;
- `ContentHash` — deterministic hash of the room-region set;
- a list of room-region packets.

Each `GreenhouseRegionPacket` contains:

- dimension;
- room bounds;
- shape hash;
- exact `PosInRoom` occupancy bytes;
- room type.

## Subscriptions and pushed updates

A valid request subscribes that player to the chunk. If the server already has cached state, it responds immediately. Otherwise, it sends a `Complete=false` acknowledgement and performs discovery work.

`Complete=false` means the server owns a future authoritative update. The client does not repeatedly poll a known incomplete snapshot from the tessellation path.

When a subscribed chunk changes, the server pushes the newer snapshot to all current subscribers. Subscriptions and cached states are pruned when players move outside the configured retention radius or disconnect.

## Typical interaction sequence

1. A client tessellates wind-enabled vegetation and observes its chunk.
2. The client has no authoritative snapshot and queues a chunk request.
3. The request worker rate-limits and batches the request.
4. The server validates the request and subscribes the player.
5. The server returns cached state or begins a sliced authoritative room scan.
6. The server sends room bounds, occupancy, room type, revision, and completeness.
7. The client commits the snapshot if it is newer than its cached revision.
8. The client redraws affected chunks, registers eligible vegetation envelopes, and scans exposed water.
9. The client builds or updates the quarter-block hash texture.
10. During rendering, the shader resolves each affected vertex to one of the six room-local states.
11. Later structural changes cause the server to revalidate and push a new revision.

# Configuration file reference

The server and client each load their own copy of `stillgreenhouses.json`.

- Server-only settings must be changed in the server's configuration.
- Client-only settings may differ between players.
- Shared lifecycle settings such as `Enabled` and `DebugLogging` apply independently to the side on which that file is loaded.
- Values outside the valid range are normalized when the config loads.
- Reversed lower/upper ranges are automatically reordered.
- `NaN` or infinite wind percentages are replaced with `5`.

## General and visual settings

| Property | Side | Default | Valid values | Description |
|---|---|---:|---|---|
| `Enabled` | Both | `true` | `true`, `false` | Master processing switch for that side. On the server it disables room scanning, networking responses, and managed-room butterfly checks. On the client it disables discovery and visual registration. |
| `DebugLogging` | Both | `false` | `true`, `false` | Enables detailed diagnostic and performance logging on the side using the setting. |
| `ClientVisualEnabled` | Client | `true` | `true`, `false` | Enables or disables plant/water room-wind presentation without changing server room detection. |
| `GreenhouseWindMode` | Client | `"VanillaLowWind"` | `"VanillaLowWind"`, `"VanillaNoWind"` | Selects low/configured plant wind or exact no-wind behavior. Legacy `NoWind` is migrated to `VanillaNoWind`; other legacy nonzero names normalize to `VanillaLowWind`. |

## Client room-type filters

| Property | Default | Valid values | Description |
|---|---:|---|---|
| `ApplyToGreenhouses` | `true` | `true`, `false` | Allows client visual wind adjustment in server-authoritative greenhouse rooms. |
| `ApplyToCellars` | `true` | `true`, `false` | Allows client visual wind adjustment in server-authoritative cellar rooms. |
| `ApplyToRooms` | `true` | `true`, `false` | Allows client visual wind adjustment in server-authoritative normal rooms. |

These filters do not change what the server identifies or sends. They only change which received room types the local client uses.

## Client vegetation-category filters

| Property | Default | Valid values | Description |
|---|---:|---|---|
| `ApplyToCrops` | `true` | `true`, `false` | Enables crop blocks. |
| `ApplyToFlowers` | `true` | `true`, `false` | Enables `flower-*` blocks. |
| `ApplyToHerbs` | `true` | `true`, `false` | Enables `herb-*` blocks. |
| `ApplyToBerryBushes` | `true` | `true`, `false` | Enables small and fruiting berry-bush families. |
| `ApplyToTallPlants` | `true` | `true`, `false` | Enables reeds, cattails, tule, papyrus, sedges, and `tallplant-*` families. |
| `ApplyToVines` | `true` | `true`, `false` | Enables wild vines and recognized vine block types. |
| `ApplyToFruitTreeLeaves` | `true` | `true`, `false` | Enables wind-enabled foliage vertices on recognized fruit-tree branches and foliage. |
| `ApplyToOtherVegetation` | `true` | `true`, `false` | Enables generic plant/leaf vegetation and the active-wind-mesh fallback, including compatible ferns, bamboo, potted plants, and modded vegetation. |
| `ApplyToGrass` | `true` | `true`, `false` | Enables recognized grass-code families. The 4.0.0 UI labels this option WIP/non-functional. |

A category toggle is not sufficient by itself: the rendered mesh must also contain an active vanilla wind mode.

## Client plant wind ranges

All plant wind percentages are normalized to `0.0–200.0`.

- `0` means no simulated surface wind.
- `5` means 5% (`0.05` in the shader).
- `100` means vanilla-scale 100%.
- `200` allows up to twice that scale.
- Equal lower and upper values produce fixed movement.
- Unequal values cycle between the two bounds.

| Property | Default | Valid range | Description |
|---|---:|---:|---|
| `GreenhouseWindLowerPercent` | `5.0` | `0.0–200.0` | Lower vegetation wind bound shared by enabled greenhouses. |
| `GreenhouseWindUpperPercent` | `5.0` | `0.0–200.0` | Upper vegetation wind bound shared by enabled greenhouses. |
| `CellarWindLowerPercent` | `5.0` | `0.0–200.0` | Lower vegetation wind bound shared by enabled cellars. |
| `CellarWindUpperPercent` | `5.0` | `0.0–200.0` | Upper vegetation wind bound shared by enabled cellars. |
| `RoomWindLowerPercent` | `5.0` | `0.0–200.0` | Lower vegetation wind bound shared by enabled normal rooms. |
| `RoomWindUpperPercent` | `5.0` | `0.0–200.0` | Upper vegetation wind bound shared by enabled normal rooms. |

When `GreenhouseWindMode` is `VanillaNoWind`, vegetation is forced to zero regardless of these range values. Water continues to use its independent water ranges.

## Client water settings

| Property | Default | Valid values/range | Description |
|---|---:|---|---|
| `ApplyToWater` | `true` | `true`, `false` | Enables independent room-local movement for exposed full freshwater source blocks in enabled room types. |
| `GreenhouseWaterWindLowerPercent` | `5.0` | `0.0–200.0` | Lower water movement bound shared by enabled greenhouses. |
| `GreenhouseWaterWindUpperPercent` | `5.0` | `0.0–200.0` | Upper water movement bound shared by enabled greenhouses. |
| `CellarWaterWindLowerPercent` | `5.0` | `0.0–200.0` | Lower water movement bound shared by enabled cellars. |
| `CellarWaterWindUpperPercent` | `5.0` | `0.0–200.0` | Upper water movement bound shared by enabled cellars. |
| `RoomWaterWindLowerPercent` | `5.0` | `0.0–200.0` | Lower water movement bound shared by enabled normal rooms. |
| `RoomWaterWindUpperPercent` | `5.0` | `0.0–200.0` | Upper water movement bound shared by enabled normal rooms. |

## Legacy position settings

| Property | Default | Valid values | Description |
|---|---:|---|---|
| `MaxAffectedPlants` | `128` | `1–128` when extended capacity is false; `1–512` when true | Retained for rollback compatibility with the older uniform-array implementation. Version 4.0.0's active spatial hash does not truncate targets to this value. |
| `ExperimentalExtendedPositionCapacity` | `false` | `true`, `false` | Legacy opt-in that raises the normalization ceiling for `MaxAffectedPlants` from 128 to 512. It does not expand the active 4.0.0 hash system. |

## Server room-management settings

| Property | Default | Valid range | Description |
|---|---:|---:|---|
| `MinimumManagedRoomInteriorPositions` | `7` | `1–32768` | Minimum exact `PosInRoom` occupancy count required for a vanilla-valid room to become managed. |
| `MaxServerChunkScansPerTick` | `1` | `1–8` | Maximum number of scan/revalidation operations admitted during one 250 ms scheduler callback. |
| `ServerForegroundWorkBudgetMs` | `4.0` | `0.5–20.0` | Soft main-thread time budget for one server scan scheduler callback. RoomRegistry calls themselves remain synchronous. |
| `ServerRescanDelayMs` | `1000` | `0–10000` | Delay after structural changes before rescanning, allowing vanilla RoomRegistry data to settle. |
| `ServerRoomDisappearanceGraceMs` | `5000` | `1000–30000` | Minimum time a formerly valid room is retained while repeated zero/replacement observations are awaiting authoritative confirmation. |
| `ServerIncompleteRoomDisappearanceRetentionMs` | `30000` | Current grace value through `300000` | Retention checkpoint when the final disappearance scan is incomplete. It is normalized to at least `ServerRoomDisappearanceGraceMs`. |
| `ServerSubscriptionRadiusChunks` | `24` | `8–64` | Horizontal chunk radius within which a player's existing server subscriptions are retained. Vertical retention is separately bounded by the implementation. |

## Client transport, cache, and foreground-work settings

| Property | Default | Valid range | Description |
|---|---:|---:|---|
| `ClientIncompleteRetryMs` | `1000` | `250–10000` | Retry/lease timeout when a request cannot be sent or no server packet arrives. Known `Complete=false` snapshots are server-owned and are not continuously polled. |
| `MaxClientDiscoveryRequestsPerSecond` | `2` | `1–16` | Maximum number of newly requested chunks sent by one client per second. |
| `MaxChunkRequestsPerBatch` | `2` | `1–8` | Maximum chunk requests placed in one client packet. Not exposed by ConfigLib. |
| `ClientDiscoveryRadiusChunks` | `8` | `2–32` | Horizontal radius in which an unknown observed chunk may enter discovery. |
| `MaxQueuedDiscoveryChunks` | `256` | `32–2048` | Maximum unsent discovery candidates. Nearer chunks replace farther entries when full. |
| `ClientCacheRadiusChunks` | `24` | `8–64` | Horizontal radius for retaining snapshots and observed vegetation. |
| `ClientForegroundWorkIntervalMs` | `50` | `20–1000` | Interval for bounded water validation and chunk-redraw work. |
| `MaxClientWaterChecksPerTick` | `256` | `16–4096` | Maximum water positions checked during one client foreground-work callback. |
| `MaxClientChunkRedrawsPerTick` | `1` | `1–16` | Maximum queued chunk redraws performed during one client foreground-work callback. |
| `ClientCachePruneIntervalMs` | `30000` | `5000–120000` | Interval between client cache-pruning passes. |

## Client shader diagnostic settings

These are debugging tools and should remain disabled during normal play.

| Property | Default | Valid values | Description |
|---|---:|---|---|
| `DebugRoomWindCallSiteProof` | `false` | `true`, `false` | Stage-one proof. Moves every wind-enabled vegetation vertex reaching the wrapped shader call site by +0.75 blocks on X. Does not require a successful room lookup. |
| `DebugRoomWindVisualProof` | `false` | `true`, `false` | Stage-two proof. Raises only vertices that successfully resolve a managed-room hash cell by +0.35 blocks on Y. |

If both proof settings are enabled, the call-site proof takes precedence.

## Complete programmatic default configuration

The following values are the defaults constructed by the 4.0.0 code when no config file exists:

```json
{
  "Enabled": true,
  "DebugLogging": false,
  "ClientVisualEnabled": true,
  "ApplyToGreenhouses": true,
  "ApplyToCellars": true,
  "ApplyToRooms": true,
  "ApplyToCrops": true,
  "ApplyToFlowers": true,
  "ApplyToHerbs": true,
  "ApplyToBerryBushes": true,
  "ApplyToTallPlants": true,
  "ApplyToVines": true,
  "ApplyToFruitTreeLeaves": true,
  "ApplyToOtherVegetation": true,
  "ApplyToGrass": true,
  "ApplyToWater": true,
  "MaxAffectedPlants": 128,
  "ExperimentalExtendedPositionCapacity": false,
  "GreenhouseWindMode": "VanillaLowWind",
  "GreenhouseWindLowerPercent": 5.0,
  "GreenhouseWindUpperPercent": 5.0,
  "CellarWindLowerPercent": 5.0,
  "CellarWindUpperPercent": 5.0,
  "RoomWindLowerPercent": 5.0,
  "RoomWindUpperPercent": 5.0,
  "GreenhouseWaterWindLowerPercent": 5.0,
  "GreenhouseWaterWindUpperPercent": 5.0,
  "CellarWaterWindLowerPercent": 5.0,
  "CellarWaterWindUpperPercent": 5.0,
  "RoomWaterWindLowerPercent": 5.0,
  "RoomWaterWindUpperPercent": 5.0,
  "MinimumManagedRoomInteriorPositions": 7,
  "MaxServerChunkScansPerTick": 1,
  "ServerForegroundWorkBudgetMs": 4.0,
  "ServerRescanDelayMs": 1000,
  "ServerRoomDisappearanceGraceMs": 5000,
  "ServerIncompleteRoomDisappearanceRetentionMs": 30000,
  "DebugRoomWindCallSiteProof": false,
  "DebugRoomWindVisualProof": false,
  "ClientIncompleteRetryMs": 1000,
  "MaxClientDiscoveryRequestsPerSecond": 2,
  "MaxChunkRequestsPerBatch": 2,
  "ClientDiscoveryRadiusChunks": 8,
  "MaxQueuedDiscoveryChunks": 256,
  "ClientCacheRadiusChunks": 24,
  "ServerSubscriptionRadiusChunks": 24,
  "ClientForegroundWorkIntervalMs": 50,
  "MaxClientWaterChecksPerTick": 256,
  "MaxClientChunkRedrawsPerTick": 1,
  "ClientCachePruneIntervalMs": 30000
}
```

# ConfigLib reference

ConfigLib exposes a client-focused subset of `stillgreenhouses.json`. Every exposed setting is marked `clientSide` in the shipped schema, and the 4.0.0 UI displays a restart-required notice. Saving the ConfigLib window does not live-reload Still Greenhouses; restart the client after changes.

The following table lists every interactive ConfigLib option in 4.0.0.

| ConfigLib label | Property | UI default | Valid UI options | Effect |
|---|---|---:|---|---|
| Enable Indoor Wind Adjustment | `ClientVisualEnabled` | `true` | On/Off | Client-side visual master switch for vegetation and water room wind. |
| Indoor Plant Movement | `GreenhouseWindMode` | `VanillaLowWind` | `VanillaLowWind` (Low Wind), `VanillaNoWind` (No Wind) | Selects configured plant ranges or exact zero vegetation movement. |
| Legacy 0.17 Extended Capacity | `ExperimentalExtendedPositionCapacity` | `false` | On/Off | Legacy compatibility option; no active 4.0.0 hash-capacity effect. |
| Legacy 0.17 Position Limit | `MaxAffectedPlants` | `128` | Integer `1–512`, step `1` | Legacy compatibility value. On load, code clamps it to 128 unless extended capacity is enabled. |
| Apply to Greenhouses | `ApplyToGreenhouses` | `true` | On/Off | Enables greenhouse visual effects. |
| Apply to Cellars | `ApplyToCellars` | `false` | On/Off | Enables cellar visual effects. |
| Apply to Normal Rooms | `ApplyToRooms` | `false` | On/Off | Enables normal-room visual effects. |
| Greenhouse Lower Wind (%) | `GreenhouseWindLowerPercent` | `5.0` | `0.0–200.0`, step `0.1` | Greenhouse vegetation lower bound. |
| Greenhouse Upper Wind (%) | `GreenhouseWindUpperPercent` | `5.0` | `0.0–200.0`, step `0.1` | Greenhouse vegetation upper bound. |
| Cellar Lower Wind (%) | `CellarWindLowerPercent` | `5.0` | `0.0–200.0`, step `0.1` | Cellar vegetation lower bound. |
| Cellar Upper Wind (%) | `CellarWindUpperPercent` | `5.0` | `0.0–200.0`, step `0.1` | Cellar vegetation upper bound. |
| Normal Room Lower Wind (%) | `RoomWindLowerPercent` | `5.0` | `0.0–200.0`, step `0.1` | Normal-room vegetation lower bound. |
| Normal Room Upper Wind (%) | `RoomWindUpperPercent` | `5.0` | `0.0–200.0`, step `0.1` | Normal-room vegetation upper bound. |
| Crops | `ApplyToCrops` | `true` | On/Off | Enables crop vegetation. |
| Flowers | `ApplyToFlowers` | `true` | On/Off | Enables flower vegetation. |
| Herbs | `ApplyToHerbs` | `true` | On/Off | Enables herb vegetation. |
| Berry Bushes | `ApplyToBerryBushes` | `true` | On/Off | Enables berry-bush vegetation. |
| Tall Plants | `ApplyToTallPlants` | `true` | On/Off | Enables reeds and tall-plant families. |
| Vines | `ApplyToVines` | `true` | On/Off | Enables vine vegetation. |
| Fruit Tree Leaves (WIP) | `ApplyToFruitTreeLeaves` | `true` | On/Off | Enables recognized fruit-tree wind vertices. |
| Other Plants and Leaves | `ApplyToOtherVegetation` | `true` | On/Off | Enables generic native-wind meshes, including many containers, ferns, bamboo, leaves, and compatible modded plants. |
| Grass (WIP, non-functional) | `ApplyToGrass` | `true` | On/Off | Enables the recognized grass category; UI marks it WIP/non-functional in 4.0.0. |
| Water | `ApplyToWater` | `true` | On/Off | Enables independent water source-surface adjustment. |
| Greenhouse Water Lower Movement (%) | `GreenhouseWaterWindLowerPercent` | `5.0` | `0.0–200.0`, step `0.1` | Greenhouse water lower bound. |
| Greenhouse Water Upper Movement (%) | `GreenhouseWaterWindUpperPercent` | `5.0` | `0.0–200.0`, step `0.1` | Greenhouse water upper bound. |
| Cellar Water Lower Movement (%) | `CellarWaterWindLowerPercent` | `5.0` | `0.0–200.0`, step `0.1` | Cellar water lower bound. |
| Cellar Water Upper Movement (%) | `CellarWaterWindUpperPercent` | `5.0` | `0.0–200.0`, step `0.1` | Cellar water upper bound. |
| Normal Room Water Lower Movement (%) | `RoomWaterWindLowerPercent` | `5.0` | `0.0–200.0`, step `0.1` | Normal-room water lower bound. |
| Normal Room Water Upper Movement (%) | `RoomWaterWindUpperPercent` | `5.0` | `0.0–200.0`, step `0.1` | Normal-room water upper bound. |

## ConfigLib default discrepancy

The 4.0.0 code defaults both of these properties to `true` when it creates a new config:

```json
{
  "ApplyToCellars": true,
  "ApplyToRooms": true
}
```

The shipped ConfigLib schema declares their UI defaults as `false`.

This means the value shown or written by ConfigLib can differ from the programmatic default of a newly generated configuration. The actual value stored in `stillgreenhouses.json` is the value the client loads. Server room identification is unaffected by these client filters.

## Settings not exposed by ConfigLib

The following settings must be edited directly in `stillgreenhouses.json`:

- `Enabled`
- `DebugLogging`
- `MinimumManagedRoomInteriorPositions`
- `MaxServerChunkScansPerTick`
- `ServerForegroundWorkBudgetMs`
- `ServerRescanDelayMs`
- `ServerRoomDisappearanceGraceMs`
- `ServerIncompleteRoomDisappearanceRetentionMs`
- `DebugRoomWindCallSiteProof`
- `DebugRoomWindVisualProof`
- `ClientIncompleteRetryMs`
- `MaxClientDiscoveryRequestsPerSecond`
- `MaxChunkRequestsPerBatch`
- `ClientDiscoveryRadiusChunks`
- `MaxQueuedDiscoveryChunks`
- `ClientCacheRadiusChunks`
- `ServerSubscriptionRadiusChunks`
- `ClientForegroundWorkIntervalMs`
- `MaxClientWaterChecksPerTick`
- `MaxClientChunkRedrawsPerTick`
- `ClientCachePruneIntervalMs`

Server performance and room-validity settings must be changed in the dedicated server's config file, not only in a player's local ConfigLib window.

# Troubleshooting notes

- Confirm the mod is installed on both sides and both sides are running compatible copies.
- Restart the client after changing ConfigLib settings.
- Restart the server after changing server-side settings.
- Enable `DebugLogging` only while diagnosing a problem; it produces extensive cache, room, shader, and performance output.
- If vegetation is unaffected, first verify that its mesh contains a native vanilla wind mode and that its category and room type are enabled.
- If water is unaffected, confirm it is a full freshwater source with an exposed top surface and that the room was already established by qualifying vegetation.
- Use the shader proof settings only for diagnosis; they intentionally displace geometry.

