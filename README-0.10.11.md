# Still Greenhouses 0.10.11 — reviewed grass + indoor water merge

This package merges the supplied grass, water, diagnostics, ConfigLib, and
language edits into the existing 0.10.11 asset-origin / viable-room-cache
architecture.

No network protocol changes are included.

## Grass

Ground-cover grass is now recognized through a narrow code-family fallback:

- `tallgrass*`
- `grass-*`
- `*-grass-*`

Grass has its own `ApplyToGrass` client toggle.

The client still requires an active, nonzero Vanilla `WindMode` on the actual
rendered mesh before applying room wind.

Because the shared structural vegetation identity now recognizes grass, a
grass-only room can satisfy the existing server viable-room cache gate. Empty
underground RoomRegistry pockets still do not remain tracked.

Debug logging adds a de-duplicated:

```text
UNCLASSIFIED WIND BLOCK
```

line for wind-bearing blocks inside a tracked room that still fail the
vegetation policy.

## Water

`ApplyToWater` is client-side and default-on.

Water does not keep a room tracked. It only rides authoritative rooms already
tracked because viable vegetation exists.

Only exposed full freshwater source blocks are registered:

```text
block.IsLiquid()
LiquidLevel == 7
LiquidCode == "water"
no liquid directly above in the fluid layer
```

The fluid layer is queried explicitly. This excludes lava, flowing/partial
liquid, and submerged source volume that has no visible surface.

Water registration is performed when authoritative room snapshots are
committed. Block changes also refresh the changed position and the position
below it so newly exposed water surfaces can enter the mask.

## Liquid shader bridge

The generated high-priority `game:shaderincludes/vertexwarp.vsh` override now
also patches `applyLiquidWarping()`.

Both of the official liquid terrain programs are included in uniform binding:

```text
Chunkliquid
Chunkliquiddepth
```

For room-masked water:

- the room type's current wind speed comes from the existing shared room state
- `WaterWaveIntensity` is reconstructed from the room wind using Vanilla's
  `0.75 + windSpeed * 0.9` formula
- the wind ripple uses the room-local `WindWaveCounter`
- a room wind speed of exactly 0 is an explicit StillGreenhouses still-water
  policy and zeroes both liquid-wave terms

Outside an eligible room-water mask, Vanilla liquid inputs are used.

If the `applyLiquidWarping()` source landmarks drift in a later Vintage Story
version, the liquid surgery is non-fatal:

```text
ROOM WIND LIQUID PATCH SKIPPED
```

Vegetation wind remains available.

## Separate vegetation and water target flags

Vegetation and water share the 128-position GPU transport, but each position
now carries target flags as well as its room-type state index.

Targets:

```text
Vegetation = 1
Water      = 2
Both       = 3
```

The packed `.w` value contains:

```text
stateIndex + targetFlags * RoomTypeStateCount
```

The vegetation shader path only accepts Vegetation/Both entries.

The liquid shader path only accepts Water/Both entries.

This prevents a vegetation registration from enabling room-water behavior when
`ApplyToWater=false`, and prevents a water registration from enabling plant
wind for a disabled vegetation category. A position can legitimately carry
Both when solid and fluid layers overlap.

## Existing 0.10.11 architecture retained

- server viable-room cache gate
- water does not participate in server viability
- generated high-priority `game` shader asset origin
- original Vanilla vegetation `WindMode` preserved
- original `WindData` preserved
- Greenhouse / Cellar / normal Room shared wind ranges
- fixed state when lower == upper
- triangular stepped variable ranges
- 64 / 32 / 32 room-type position reservations
- nearest-position redistribution of unused reserved capacity
- fail-open to Vanilla global wind until the compiled `Chunkopaque` bridge is
  verified

## ConfigLib additions

Vegetation Types:

```text
Grass
```

Water:

```text
Water
```

The existing room-type lower/upper wind ranges drive both vegetation and water
for that room type.

## Useful diagnostics

```text
UNCLASSIFIED WIND BLOCK
ROOM WIND LIQUID PATCH APPLIED
ROOM WIND LIQUID PATCH SKIPPED
ROOM WIND COMPILED BRIDGE VERIFY target=Chunkliquid
ROOM WIND COMPILED BRIDGE VERIFY target=Chunkliquiddepth
ROOM WIND UNIFORM PROGRAMS
```

## Build note

No `dotnet` build or compiler invocation was run while preparing this package.
