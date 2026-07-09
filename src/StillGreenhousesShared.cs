/*
version 0.10.16a
*/

using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace StillGreenhouses;

internal static partial class StillGreenhousesShared
{
    internal const string ConfigFileName = "stillgreenhouses.json";
    internal const int ChunkSize = GlobalConstants.ChunkSize;

    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    internal static StillGreenhousesConfig LoadConfig(
        ICoreAPI api,
        bool storeNormalizedConfig
    )
    {
        StillGreenhousesConfig config;

        try
        {
            config =
                api.LoadModConfig<StillGreenhousesConfig>(ConfigFileName)
                ?? new StillGreenhousesConfig();
        }
        catch (Exception e)
        {
            config = new StillGreenhousesConfig();

            api.Logger.Warning(
                $"[StillGreenhouses] Failed to read config; using defaults. " +
                $"{e.GetType().Name}: {e.Message}"
            );
        }

        NormalizeConfig(config);

        if (storeNormalizedConfig)
        {
            api.StoreModConfig(config, ConfigFileName);
        }

        return config;
    }

    // Broad structural identity used by wind eligibility and room interior
    // pass-through logic. A block only becomes a room-wind target after its
    // rendered mesh is also proven to contain a nonzero Vanilla WindMode.
    internal static bool IsVegetationIdentityBlock(
        Block? block
    )
    {
        if (block == null)
        {
            return false;
        }

        return block is BlockCrop
               || block is BlockPlant
               || block.BlockMaterial == EnumBlockMaterial.Plant
               || block.BlockMaterial == EnumBlockMaterial.Leaves
               || IsGrassCode(block.Code);
    }

    internal static string DescribeVegetationIdentity(
        Block? block
    )
    {
        if (block == null)
        {
            return "<none>";
        }

        List<string> identities = new();

        if (block is BlockCrop)
        {
            identities.Add("BlockCrop");
        }

        if (block is BlockPlant)
        {
            identities.Add("BlockPlant");
        }

        if (
            block.BlockMaterial
                == EnumBlockMaterial.Plant
        )
        {
            identities.Add("MaterialPlant");
        }

        if (
            block.BlockMaterial
                == EnumBlockMaterial.Leaves
        )
        {
            identities.Add("MaterialLeaves");
        }

        if (IsGrassCode(block.Code))
        {
            identities.Add("GrassCode");
        }

        return identities.Count == 0
            ? "<none>"
            : string.Join("|", identities);
    }

    // Ground-cover grasses do not reliably share BlockPlant or Plant/Leaves
    // material identity, so use a narrow code-family fallback. The client still
    // requires an active Vanilla WindMode on the rendered mesh before applying
    // room wind.
    internal static bool IsGrassCode(
        AssetLocation? code
    )
    {
        string? path =
            code?.Path;

        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.StartsWith(
                   "tallgrass",
                   StringComparison.Ordinal
               )
               || path.StartsWith(
                   "grass-",
                   StringComparison.Ordinal
               )
               || path.Contains(
                   "-grass-",
                   StringComparison.Ordinal
               );
    }

    // Server discovery anchors are intentionally narrower than the broad visual
    // vegetation identity. Generic Leaves-material blocks may receive room wind
    // after another plant establishes the room, but ordinary tree canopy leaves
    // cannot establish or keep a managed room by themselves.
    internal static bool IsRoomDiscoveryAnchorVegetation(
        Block? block
    )
    {
        if (block?.Code == null)
        {
            return false;
        }

        string path =
            block.Code.Path
            ?? string.Empty;

        string runtimeType =
            block.GetType().FullName
            ?? block.GetType().Name;

        if (
            block is BlockCrop
            || block is BlockPlant
            || block.BlockMaterial
                == EnumBlockMaterial.Plant
            || IsGrassCode(block.Code)
        )
        {
            return true;
        }

        if (
            path.StartsWith(
                "smallberrybush-",
                StringComparison.Ordinal
            )
            || path.StartsWith(
                "fruitingbush-",
                StringComparison.Ordinal
            )
            || runtimeType.EndsWith(
                ".BlockBerryBush",
                StringComparison.Ordinal
            )
        )
        {
            return true;
        }

        if (
            path.StartsWith(
                "wildvine-",
                StringComparison.Ordinal
            )
            || runtimeType.EndsWith(
                "BlockVines",
                StringComparison.Ordinal
            )
        )
        {
            return true;
        }

        bool fruitTreeBlock =
            path.StartsWith(
                "fruittreebranch",
                StringComparison.Ordinal
            )
            || runtimeType.Contains(
                "FruitTree",
                StringComparison.OrdinalIgnoreCase
            );

        return fruitTreeBlock
               && (
                   path.Contains(
                       "branch",
                       StringComparison.OrdinalIgnoreCase
                   )
                   || path.Contains(
                       "leaves",
                       StringComparison.OrdinalIgnoreCase
                   )
                   || path.Contains(
                       "foliage",
                       StringComparison.OrdinalIgnoreCase
                   )
                   || runtimeType.Contains(
                       "Branch",
                       StringComparison.OrdinalIgnoreCase
                   )
               );
    }

    // Exact full freshwater source identity. Vanilla's Block.LiquidLevel is
    // 0..7 and full freshwater checks use level 7 with LiquidCode == "water".
    internal static bool IsWaterSourceBlock(
        Block? block
    )
    {
        if (block == null)
        {
            return false;
        }

        return block.IsLiquid()
               && block.LiquidLevel == 7
               && string.Equals(
                   block.LiquidCode,
                   "water",
                   StringComparison.Ordinal
               );
    }

    // Only exposed source blocks need a liquid-wave mask. Submerged full source
    // blocks have no visible top surface and would waste uniform positions.
    internal static bool IsWaterSurfaceSourceBlock(
        IBlockAccessor blockAccessor,
        BlockPos pos
    )
    {
        Block water =
            blockAccessor.GetBlock(
                pos,
                BlockLayersAccess.Fluid
            );

        if (!IsWaterSourceBlock(water))
        {
            return false;
        }

        BlockPos abovePos =
            pos.UpCopy();

        Block fluidAbove =
            blockAccessor.GetBlock(
                abovePos,
                BlockLayersAccess.Fluid
            );

        return !fluidAbove.IsLiquid();
    }

    internal static bool IsVegetationCandidate(
        Block? block,
        StillGreenhousesConfig? config = null
    )
    {
        if (
            block?.Code == null
            || !IsVegetationIdentityBlock(block)
        )
        {
            return false;
        }

        string path =
            block.Code.Path ?? string.Empty;

        string runtimeType =
            block.GetType().FullName
            ?? block.GetType().Name;

        // Known families retain their existing per-category opt-outs. These
        // checks are policy labels only; they are no longer the discovery
        // mechanism for vegetation wind eligibility.
        if (
            block is BlockCrop
            || path.StartsWith(
                "crop-",
                StringComparison.Ordinal
            )
        )
        {
            return config?.ApplyToCrops ?? true;
        }

        if (
            path.StartsWith(
                "smallberrybush-",
                StringComparison.Ordinal
            )
            || path.StartsWith(
                "fruitingbush-",
                StringComparison.Ordinal
            )
            || runtimeType.EndsWith(
                ".BlockBerryBush",
                StringComparison.Ordinal
            )
        )
        {
            return config?.ApplyToBerryBushes ?? true;
        }

        if (
            path.StartsWith(
                "tallplant-",
                StringComparison.Ordinal
            )
            || runtimeType.EndsWith(
                ".BlockReeds",
                StringComparison.Ordinal
            )
        )
        {
            return config?.ApplyToTallPlants ?? true;
        }

        if (
            path.StartsWith(
                "herb-",
                StringComparison.Ordinal
            )
        )
        {
            return config?.ApplyToHerbs ?? true;
        }

        if (
            path.StartsWith(
                "flower-",
                StringComparison.Ordinal
            )
        )
        {
            return config?.ApplyToFlowers ?? true;
        }

        if (
            path.StartsWith(
                "wildvine-",
                StringComparison.Ordinal
            )
            || runtimeType.EndsWith(
                "BlockVines",
                StringComparison.Ordinal
            )
        )
        {
            return config?.ApplyToVines ?? true;
        }

        bool fruitTreeBlock =
            path.StartsWith(
                "fruittreebranch",
                StringComparison.Ordinal
            )
            || runtimeType.Contains(
                "FruitTree",
                StringComparison.OrdinalIgnoreCase
            );

        if (
            fruitTreeBlock
            && (
                path.Contains(
                    "branch",
                    StringComparison.OrdinalIgnoreCase
                )
                || path.Contains(
                    "leaves",
                    StringComparison.OrdinalIgnoreCase
                )
                || path.Contains(
                    "foliage",
                    StringComparison.OrdinalIgnoreCase
                )
                || runtimeType.Contains(
                    "Branch",
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
        {
            return config?.ApplyToFruitTreeLeaves ?? true;
        }

        // Ferns, grasses, generic plant blocks, generic leaf blocks, and
        // compatible modded vegetation arrive here automatically through the
        // four structural identity cases above. The mesh WindMode check remains
        // the final proof that the rendered vertices actually participate in
        // Vanilla wind animation.
        if (IsGrassCode(block.Code))
        {
            return config?.ApplyToGrass ?? true;
        }

        return config?.ApplyToOtherVegetation ?? true;
    }

    // Room scanning only needs to know whether a non-air block can occupy room
    // interior without being treated as room structure. This is deliberately
    // separate from the wind-modification policy above.
    internal static bool IsRoomInteriorPassThroughBlock(
        Block? block
    )
    {
        if (block == null)
        {
            return false;
        }

        return IsVegetationIdentityBlock(block);
    }

    internal static bool TryClassifyRoom(
        Room room,
        out ManagedRoomType roomType
    )
    {
        roomType = default;

        if (
            room.AnyChunkUnloaded != 0
            || room.ExitCount != 0
        )
        {
            return false;
        }


        if (
            room.SkylightCount
            > room.NonSkylightCount
        )
        {
            roomType =
                ManagedRoomType.Greenhouse;

            return true;
        }

        if (room.IsSmallRoom)
        {

            roomType =
                ManagedRoomType.Cellar;

            return true;
        }

        roomType =
            ManagedRoomType.Room;

        return true;
    }

    internal static bool IsRoomTypeEnabled(
        StillGreenhousesConfig config,
        ManagedRoomType roomType
    ) =>
        roomType switch
        {
            ManagedRoomType.Greenhouse =>
                config.ApplyToGreenhouses,

            ManagedRoomType.Cellar =>
                config.ApplyToCellars,

            ManagedRoomType.Room =>
                config.ApplyToRooms,

            _ => false
        };

    internal static ManagedRoomType ResolveManagedRoomType(
        int rawValue
    ) =>
        Enum.IsDefined(
            typeof(ManagedRoomType),
            rawValue
        )
            ? (ManagedRoomType)rawValue
            : ManagedRoomType.Room;

    internal static ulong ComputeRegionSetHash(
        IEnumerable<GreenhouseRegion> regions
    )
    {
        ulong hash = FnvOffsetBasis;

        foreach (
            GreenhouseRegion region
            in regions
                .OrderBy(region => region.Key.Dimension)
                .ThenBy(region => region.Key.X1)
                .ThenBy(region => region.Key.Y1)
                .ThenBy(region => region.Key.Z1)
                .ThenBy(region => region.Key.X2)
                .ThenBy(region => region.Key.Y2)
                .ThenBy(region => region.Key.Z2)
                .ThenBy(region => region.Key.RoomType)
                .ThenBy(region => region.Key.ShapeHash)
        )
        {
            AddInt(ref hash, region.Key.Dimension);
            AddInt(ref hash, region.Key.X1);
            AddInt(ref hash, region.Key.Y1);
            AddInt(ref hash, region.Key.Z1);
            AddInt(ref hash, region.Key.X2);
            AddInt(ref hash, region.Key.Y2);
            AddInt(ref hash, region.Key.Z2);
            AddInt(
                ref hash,
                (int)region.Key.RoomType
            );
            AddUlong(ref hash, region.Key.ShapeHash);
        }

        return hash;
    }

    internal static HashSet<ChunkKey> GetBoundaryAffectedChunks(
        BlockPos pos
    )
    {
        ChunkKey center = ChunkKey.From(pos);

        int localX = PositiveMod(pos.X, ChunkSize);
        int localY = PositiveMod(pos.Y, ChunkSize);
        int localZ = PositiveMod(pos.Z, ChunkSize);

        int[] xOffsets = localX switch
        {
            0 => [-1, 0],
            ChunkSize - 1 => [0, 1],
            _ => [0]
        };

        int[] yOffsets = localY switch
        {
            0 => [-1, 0],
            ChunkSize - 1 => [0, 1],
            _ => [0]
        };

        int[] zOffsets = localZ switch
        {
            0 => [-1, 0],
            ChunkSize - 1 => [0, 1],
            _ => [0]
        };

        HashSet<ChunkKey> chunks = new();

        foreach (int dx in xOffsets)
        {
            foreach (int dy in yOffsets)
            {
                foreach (int dz in zOffsets)
                {
                    chunks.Add(
                        new ChunkKey(
                            center.X + dx,
                            center.Y + dy,
                            center.Z + dz,
                            center.Dimension
                        )
                    );
                }
            }
        }

        return chunks;
    }

    internal static IEnumerable<ChunkKey> GetNeighborChunks(
        ChunkKey center,
        int radius
    )
    {
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    yield return new ChunkKey(
                        center.X + dx,
                        center.Y + dy,
                        center.Z + dz,
                        center.Dimension
                    );
                }
            }
        }
    }

    internal static int CountRoomOccupiedPositions(
        Room room
    ) =>
        CountOccupiedPositions(
            room.Location.X1,
            room.Location.Y1,
            room.Location.Z1,
            room.Location.X2,
            room.Location.Y2,
            room.Location.Z2,
            room.PosInRoom
            ?? Array.Empty<byte>()
        );

    internal static int CountOccupiedPositions(
        int x1,
        int y1,
        int z1,
        int x2,
        int y2,
        int z2,
        byte[] occupancy
    )
    {
        int sizeX = x2 - x1 + 1;
        int sizeY = y2 - y1 + 1;
        int sizeZ = z2 - z1 + 1;

        int positionCount =
            Math.Max(
                0,
                sizeX * sizeY * sizeZ
            );

        int fullByteCount = Math.Min(
            positionCount / 8,
            occupancy.Length
        );

        int occupied = 0;

        for (int i = 0; i < fullByteCount; i++)
        {
            occupied +=
                System.Numerics.BitOperations.PopCount(
                    (uint)occupancy[i]
                );
        }

        int remainingBits =
            positionCount % 8;

        if (
            remainingBits > 0
            && fullByteCount < occupancy.Length
        )
        {
            uint mask =
                (1u << remainingBits) - 1u;

            occupied +=
                System.Numerics.BitOperations.PopCount(
                    (uint)occupancy[fullByteCount]
                    & mask
                );
        }

        return occupied;
    }

    internal static int FloorDiv(
        int value,
        int divisor
    )
    {
        int quotient = value / divisor;
        int remainder = value % divisor;

        if (remainder < 0)
        {
            quotient--;
        }

        return quotient;
    }

    private static int PositiveMod(
        int value,
        int divisor
    )
    {
        int result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    internal static RoomPlantMovementMode ResolveRoomPlantMovementMode(
        StillGreenhousesConfig config
    ) =>
        ResolveRoomPlantMovementMode(
            config.GreenhouseWindMode
        );

    internal static RoomPlantMovementMode ResolveRoomPlantMovementMode(
        string? configuredMode
    )
    {
        string normalizedMode = new(
            (configuredMode ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .ToArray()
        );

        // Any legacy nonzero movement-mode name migrates to VanillaLowWind. Only an
        // explicit NoWind selection produces fully stationary vegetation.
        return string.Equals(
                normalizedMode,
                nameof(RoomPlantMovementMode.NoWind),
                StringComparison.OrdinalIgnoreCase
            )
            ? RoomPlantMovementMode.NoWind
            : RoomPlantMovementMode.VanillaLowWind;
    }

    private static void NormalizeWindRange(
        float configuredLower,
        float configuredUpper,
        out float lower,
        out float upper
    )
    {
        lower =
            NormalizeWindPercent(
                configuredLower
            );

        upper =
            NormalizeWindPercent(
                configuredUpper
            );

        if (lower <= upper)
        {
            return;
        }

        (lower, upper) =
            (upper, lower);
    }

    private static float NormalizeWindPercent(
        float configuredValue
    )
    {
        if (
            float.IsNaN(configuredValue)
            || float.IsInfinity(configuredValue)
        )
        {
            return 5f;
        }

        return Math.Clamp(
            configuredValue,
            0f,
            200f
        );
    }

    private static void NormalizeConfig(
        StillGreenhousesConfig config
    )
    {
        config.GreenhouseWindMode =
            ResolveRoomPlantMovementMode(config).ToString();

        NormalizeWindRange(
            config.GreenhouseWindLowerPercent,
            config.GreenhouseWindUpperPercent,
            out float greenhouseLower,
            out float greenhouseUpper
        );

        config.GreenhouseWindLowerPercent =
            greenhouseLower;

        config.GreenhouseWindUpperPercent =
            greenhouseUpper;

        NormalizeWindRange(
            config.CellarWindLowerPercent,
            config.CellarWindUpperPercent,
            out float cellarLower,
            out float cellarUpper
        );

        config.CellarWindLowerPercent =
            cellarLower;

        config.CellarWindUpperPercent =
            cellarUpper;

        NormalizeWindRange(
            config.RoomWindLowerPercent,
            config.RoomWindUpperPercent,
            out float roomLower,
            out float roomUpper
        );

        config.RoomWindLowerPercent =
            roomLower;

        config.RoomWindUpperPercent =
            roomUpper;

        config.MinimumManagedRoomInteriorPositions = Math.Clamp(
            config.MinimumManagedRoomInteriorPositions,
            1,
            32768
        );

        config.MaxServerChunkScansPerTick = Math.Clamp(
            config.MaxServerChunkScansPerTick,
            1,
            8
        );

        config.ServerRescanDelayMs = Math.Clamp(
            config.ServerRescanDelayMs,
            0,
            10000
        );

        config.ServerRoomDisappearanceGraceMs = Math.Clamp(
            config.ServerRoomDisappearanceGraceMs,
            1000,
            30000
        );

        config.ServerIncompleteRoomDisappearanceRetentionMs = Math.Clamp(
            config.ServerIncompleteRoomDisappearanceRetentionMs,
            config.ServerRoomDisappearanceGraceMs,
            300000
        );

        config.ClientIncompleteRetryMs = Math.Clamp(
            config.ClientIncompleteRetryMs,
            250,
            10000
        );

        config.MaxClientDiscoveryRequestsPerSecond = Math.Clamp(
            config.MaxClientDiscoveryRequestsPerSecond,
            1,
            16
        );

        config.MaxChunkRequestsPerBatch = Math.Clamp(
            config.MaxChunkRequestsPerBatch,
            1,
            8
        );

        config.ClientDiscoveryRadiusChunks = Math.Clamp(
            config.ClientDiscoveryRadiusChunks,
            2,
            32
        );

        config.MaxQueuedDiscoveryChunks = Math.Clamp(
            config.MaxQueuedDiscoveryChunks,
            32,
            2048
        );

        config.ClientCacheRadiusChunks = Math.Clamp(
            config.ClientCacheRadiusChunks,
            8,
            64
        );

        config.ServerSubscriptionRadiusChunks = Math.Clamp(
            config.ServerSubscriptionRadiusChunks,
            8,
            64
        );

        config.ClientCachePruneIntervalMs = Math.Clamp(
            config.ClientCachePruneIntervalMs,
            5000,
            120000
        );
    }

    private static void AddInt(
        ref ulong hash,
        int value
    )
    {
        unchecked
        {
            AddByte(ref hash, (byte)value);
            AddByte(ref hash, (byte)(value >> 8));
            AddByte(ref hash, (byte)(value >> 16));
            AddByte(ref hash, (byte)(value >> 24));
        }
    }

    private static void AddUlong(
        ref ulong hash,
        ulong value
    )
    {
        unchecked
        {
            for (int shift = 0; shift < 64; shift += 8)
            {
                AddByte(
                    ref hash,
                    (byte)(value >> shift)
                );
            }
        }
    }

    private static void AddByte(
        ref ulong hash,
        byte value
    )
    {
        unchecked
        {
            hash ^= value;
            hash *= FnvPrime;
        }
    }

    internal static ulong ComputeShapeHash(
        byte[] occupancy
    )
    {
        ulong hash = FnvOffsetBasis;

        foreach (byte value in occupancy)
        {
            AddByte(ref hash, value);
        }

        return hash;
    }
}

internal readonly record struct ChunkKey(
    int X,
    int Y,
    int Z,
    int Dimension
)
{
    internal static ChunkKey From(BlockPos pos) =>
        new(
            StillGreenhousesShared.FloorDiv(
                pos.X,
                StillGreenhousesShared.ChunkSize
            ),
            StillGreenhousesShared.FloorDiv(
                pos.Y,
                StillGreenhousesShared.ChunkSize
            ),
            StillGreenhousesShared.FloorDiv(
                pos.Z,
                StillGreenhousesShared.ChunkSize
            ),
            pos.dimension
        );

    internal static ChunkKey From(
        GreenhouseChunkRequest packet
    ) =>
        new(
            packet.ChunkX,
            packet.ChunkY,
            packet.ChunkZ,
            packet.Dimension
        );

    internal static ChunkKey From(
        GreenhouseChunkSnapshot packet
    ) =>
        new(
            packet.ChunkX,
            packet.ChunkY,
            packet.ChunkZ,
            packet.Dimension
        );

    internal static ChunkKey FromInternalChunkCoord(
        Vec3i chunkCoord
    )
    {
        int dimensionSize =
            GlobalConstants.DimensionSizeInChunks;

        int dimension =
            StillGreenhousesShared.FloorDiv(
                chunkCoord.Y,
                dimensionSize
            );

        int logicalChunkY =
            chunkCoord.Y - dimension * dimensionSize;

        return new ChunkKey(
            chunkCoord.X,
            logicalChunkY,
            chunkCoord.Z,
            dimension
        );
    }

    internal BlockPos ToRepresentativeBlockPos() =>
        new(
            X * StillGreenhousesShared.ChunkSize
                + StillGreenhousesShared.ChunkSize / 2,
            Y * StillGreenhousesShared.ChunkSize
                + StillGreenhousesShared.ChunkSize / 2,
            Z * StillGreenhousesShared.ChunkSize
                + StillGreenhousesShared.ChunkSize / 2,
            Dimension
        );

    internal GreenhouseChunkRequest ToRequest() =>
        new()
        {
            ChunkX = X,
            ChunkY = Y,
            ChunkZ = Z,
            Dimension = Dimension
        };
}

internal enum ManagedRoomType
{
    Greenhouse = 1,
    Cellar = 2,
    Room = 3
}

internal enum RoomPlantMovementMode
{
    NoWind = 0,
    VanillaLowWind = 1
}

internal readonly record struct GreenhouseKey(
    int Dimension,
    int X1,
    int Y1,
    int Z1,
    int X2,
    int Y2,
    int Z2,
    ManagedRoomType RoomType,
    ulong ShapeHash
);

internal sealed class GreenhouseRegion
{
    internal GreenhouseKey Key { get; }

    internal int Dimension => Key.Dimension;
    internal int X1 => Key.X1;
    internal int Y1 => Key.Y1;
    internal int Z1 => Key.Z1;
    internal int X2 => Key.X2;
    internal int Y2 => Key.Y2;
    internal int Z2 => Key.Z2;
    internal ManagedRoomType RoomType =>
        Key.RoomType;

    internal int OccupiedPositionCount { get; }

    private readonly byte[] posInRoom;

    private GreenhouseRegion(
        GreenhouseKey key,
        byte[] posInRoom
    )
    {
        Key = key;
        this.posInRoom = posInRoom;
        OccupiedPositionCount =
            StillGreenhousesShared.CountOccupiedPositions(
                key.X1,
                key.Y1,
                key.Z1,
                key.X2,
                key.Y2,
                key.Z2,
                posInRoom
            );
    }

    internal bool HasAtLeastOccupiedPositions(
        int minimumPositions
    ) =>
        OccupiedPositionCount >= minimumPositions;

    internal static GreenhouseRegion FromRoom(
        Room room,
        int dimension,
        ManagedRoomType roomType
    )
    {
        byte[] occupancy =
            room.PosInRoom == null
                ? Array.Empty<byte>()
                : (byte[])room.PosInRoom.Clone();

        GreenhouseKey key = new(
            dimension,
            room.Location.X1,
            room.Location.Y1,
            room.Location.Z1,
            room.Location.X2,
            room.Location.Y2,
            room.Location.Z2,
            roomType,
            StillGreenhousesShared.ComputeShapeHash(occupancy)
        );

        return new GreenhouseRegion(
            key,
            occupancy
        );
    }

    internal static GreenhouseRegion FromPacket(
        GreenhouseRegionPacket packet
    )
    {
        byte[] occupancy =
            packet.PosInRoom == null
                ? Array.Empty<byte>()
                : (byte[])packet.PosInRoom.Clone();

        GreenhouseKey key = new(
            packet.Dimension,
            packet.X1,
            packet.Y1,
            packet.Z1,
            packet.X2,
            packet.Y2,
            packet.Z2,
            StillGreenhousesShared
                .ResolveManagedRoomType(
                    packet.RoomType
                ),
            packet.ShapeHash
        );

        return new GreenhouseRegion(
            key,
            occupancy
        );
    }

    internal GreenhouseRegionPacket ToPacket() =>
        new()
        {
            Dimension = Dimension,
            X1 = X1,
            Y1 = Y1,
            Z1 = Z1,
            X2 = X2,
            Y2 = Y2,
            Z2 = Z2,
            ShapeHash = Key.ShapeHash,
            PosInRoom = (byte[])posInRoom.Clone(),
            RoomType = (int)RoomType
        };

    internal bool Contains(BlockPos pos) =>
        Contains(
            pos.X,
            pos.Y,
            pos.Z,
            pos.dimension
        );

    internal bool IsWithinStructuralMargin(
        BlockPos pos,
        int margin
    )
    {
        return pos.dimension == Dimension
               && pos.X >= X1 - margin
               && pos.X <= X2 + margin
               && pos.Y >= Y1 - margin
               && pos.Y <= Y2 + margin
               && pos.Z >= Z1 - margin
               && pos.Z <= Z2 + margin;
    }

    internal IEnumerable<BlockPos> GetOccupiedPositions()
    {
        int sizeZ = Z2 - Z1 + 1;
        int sizeX = X2 - X1 + 1;
        int sizeY = Y2 - Y1 + 1;
        int positionCount =
            sizeX * sizeY * sizeZ;

        for (int index = 0; index < positionCount; index++)
        {
            int byteIndex = index / 8;

            if (
                byteIndex >= posInRoom.Length
                || (
                    posInRoom[byteIndex]
                    & (1 << (index % 8))
                ) == 0
            )
            {
                continue;
            }

            int dx = index % sizeX;
            int yz = index / sizeX;
            int dz = yz % sizeZ;
            int dy = yz / sizeZ;

            yield return new BlockPos(
                X1 + dx,
                Y1 + dy,
                Z1 + dz,
                Dimension
            );
        }
    }

    internal IEnumerable<ChunkKey> GetIntersectingChunks()
    {
        int minChunkX =
            StillGreenhousesShared.FloorDiv(
                X1,
                StillGreenhousesShared.ChunkSize
            );

        int maxChunkX =
            StillGreenhousesShared.FloorDiv(
                X2,
                StillGreenhousesShared.ChunkSize
            );

        int minChunkY =
            StillGreenhousesShared.FloorDiv(
                Y1,
                StillGreenhousesShared.ChunkSize
            );

        int maxChunkY =
            StillGreenhousesShared.FloorDiv(
                Y2,
                StillGreenhousesShared.ChunkSize
            );

        int minChunkZ =
            StillGreenhousesShared.FloorDiv(
                Z1,
                StillGreenhousesShared.ChunkSize
            );

        int maxChunkZ =
            StillGreenhousesShared.FloorDiv(
                Z2,
                StillGreenhousesShared.ChunkSize
            );

        for (int cy = minChunkY; cy <= maxChunkY; cy++)
        {
            for (int cz = minChunkZ; cz <= maxChunkZ; cz++)
            {
                for (int cx = minChunkX; cx <= maxChunkX; cx++)
                {
                    yield return new ChunkKey(
                        cx,
                        cy,
                        cz,
                        Dimension
                    );
                }
            }
        }
    }

    private bool Contains(
        int x,
        int y,
        int z,
        int dimension
    )
    {
        if (dimension != Dimension
            || x < X1
            || x > X2
            || y < Y1
            || y > Y2
            || z < Z1
            || z > Z2)
        {
            return false;
        }

        int sizeZ = Z2 - Z1 + 1;
        int sizeX = X2 - X1 + 1;

        int dx = x - X1;
        int dy = y - Y1;
        int dz = z - Z1;

        int index =
            (dy * sizeZ + dz) * sizeX + dx;

        int byteIndex = index / 8;

        if (byteIndex < 0
            || byteIndex >= posInRoom.Length)
        {
            return false;
        }

        return (
            posInRoom[byteIndex]
            & (1 << (index % 8))
        ) > 0;
    }
}
