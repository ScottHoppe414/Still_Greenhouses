/*
version 0.18.0
*/

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace StillGreenhouses;

// 0.17.2 preserves every vegetation vertex's original Vanilla WindMode and
// WindData, measures the actual local bounds of each mesh's wind-enabled
// vertices, and supplies separate room-owned vegetation and water states per
// ManagedRoomType.
//
// Vanilla applyVertexWarping() still performs the native per-mode deformation.
// Managed vertices replace Vanilla's environmental wind speed and phase
// counters before the native WindMode switch runs. Low Wind and No Wind share
// this exact pipeline; the selected preset only chooses the vegetation range.
// A zero-valued state explicitly zeroes every vegetation mode after the switch
// because some Vanilla modes retain baseline motion at windSpeed zero.
//
// 0.18 rasterizes the measured envelopes into a dynamically sized quarter-
// block RGBA8 hash texture. The vertex shader reconstructs absolute cells from
// Vintage Story's render reference and resolves vegetation or water in at most
// eight probes, without a nearest-128 selection or a per-vertex linear scan.
//
// All greenhouses share the Greenhouse state, all cellars share the Cellar
// state, and all normal rooms share the Room state. Each state steps upward
// through its configured percent range, reverses at the upper bound, and steps
// downward to the lower bound. Lower == Upper is a fixed sustained wind value.
internal readonly record struct RoomWindRange(
    float LowerPercent,
    float UpperPercent
)
{
    internal bool IsFixed =>
        Math.Abs(
            UpperPercent - LowerPercent
        ) < 0.0001f;

}

internal readonly record struct RoomWindRuntimeSnapshot(
    ManagedRoomType RoomType,
    float CurrentPercent,
    float WindSpeed,
    float WindWaveCounter,
    float WindWaveCounterHighFreq
);

internal static class StillGreenhousesRoomWindEnvironment
{
    internal const int RoomTypeStateCount = 3;
    internal const float DefaultTestWindPercent = 5f;

    internal static bool ShaderOverrideReady =>
        StillGreenhousesRoomWindShaderPatch.Ready;

    internal static bool ShaderReloadAttempted =>
        StillGreenhousesRoomWindShaderPatch
            .ShaderReloadAttempted;

    internal static bool ShaderReloadSucceeded =>
        StillGreenhousesRoomWindShaderPatch
            .ShaderReloadSucceeded;

    internal static bool CompiledBridgeVerified =>
        StillGreenhousesRoomWindUniformRenderer
            .UniformBridgeReady;

    internal static bool UniformBridgeReady =>
        CompiledBridgeVerified;

    internal static bool EnvironmentActive =>
        ShaderOverrideReady
        && CompiledBridgeVerified
        && IsShaderDrivenMode(
            StillGreenhousesClientSystem
                .PlantMovementMode
        );

    internal static bool ShouldUseRoomWindShader =>
        EnvironmentActive;

    internal static bool IsShaderDrivenMode(
        RoomPlantMovementMode movementMode
    ) =>
        movementMode is
            RoomPlantMovementMode.VanillaNoWind
            or RoomPlantMovementMode.VanillaLowWind;

    internal static int GetStateIndex(
        ManagedRoomType roomType
    ) =>
        roomType switch
        {
            ManagedRoomType.Greenhouse => 0,
            ManagedRoomType.Cellar => 1,
            ManagedRoomType.Room => 2,
            _ => 2
        };

    internal static int GetStateIndex(
        ManagedRoomType roomType,
        RoomWindTargetKind target
    ) =>
        GetStateIndex(roomType)
        + (
            target == RoomWindTargetKind.Water
                ? RoomTypeStateCount
                : 0
        );

    internal static RoomWindRange GetRange(
        StillGreenhousesConfig config,
        ManagedRoomType roomType,
        RoomWindTargetKind target = RoomWindTargetKind.Vegetation
    )
    {
        if (
            target == RoomWindTargetKind.Vegetation
            && StillGreenhousesClientSystem.PlantMovementMode
                == RoomPlantMovementMode.VanillaNoWind
        )
        {
            return new RoomWindRange(0f, 0f);
        }

        float lower;
        float upper;

        switch (roomType)
        {
            case ManagedRoomType.Greenhouse:
                lower =
                    target == RoomWindTargetKind.Water
                        ? config.GreenhouseWaterWindLowerPercent
                        : config.GreenhouseWindLowerPercent;

                upper =
                    target == RoomWindTargetKind.Water
                        ? config.GreenhouseWaterWindUpperPercent
                        : config.GreenhouseWindUpperPercent;

                break;

            case ManagedRoomType.Cellar:
                lower =
                    target == RoomWindTargetKind.Water
                        ? config.CellarWaterWindLowerPercent
                        : config.CellarWindLowerPercent;

                upper =
                    target == RoomWindTargetKind.Water
                        ? config.CellarWaterWindUpperPercent
                        : config.CellarWindUpperPercent;

                break;

            default:
                lower =
                    target == RoomWindTargetKind.Water
                        ? config.RoomWaterWindLowerPercent
                        : config.RoomWindLowerPercent;

                upper =
                    target == RoomWindTargetKind.Water
                        ? config.RoomWaterWindUpperPercent
                        : config.RoomWindUpperPercent;

                break;
        }

        lower =
            Math.Clamp(
                lower,
                0f,
                200f
            );

        upper =
            Math.Clamp(
                upper,
                0f,
                200f
            );

        return lower <= upper
            ? new RoomWindRange(
                lower,
                upper
            )
            : new RoomWindRange(
                upper,
                lower
            );
    }

    internal static string DescribeRange(
        StillGreenhousesConfig config,
        ManagedRoomType roomType,
        RoomWindTargetKind target = RoomWindTargetKind.Vegetation
    )
    {
        RoomWindRange range =
            GetRange(
                config,
                roomType,
                target
            );

        return
            $"{range.LowerPercent:0.###}-{range.UpperPercent:0.###}%";
    }

    internal static string GetEffectiveWindTarget(
        ManagedRoomType roomType
    )
    {
        StillGreenhousesConfig config =
            StillGreenhousesClientSystem.Config
            ?? new StillGreenhousesConfig();

        if (!EnvironmentActive)
        {
            return
                "CompiledBridgeUnavailable/VanillaWindFallback";
        }

        return
            $"VanillaSurfaceWind@{DescribeRange(config, roomType)}/" +
            $"{roomType}SharedState";
    }
}

internal readonly record struct ManagedVegetationShaderPosition(
    int X,
    int Y,
    int Z,
    int Dimension
)
{
    internal static ManagedVegetationShaderPosition From(
        BlockPos pos
    ) =>
        new(
            pos.X,
            pos.Y,
            pos.Z,
            pos.dimension
        );

    internal BlockPos ToBlockPos() =>
        new(
            X,
            Y,
            Z,
            Dimension
        );
}

[Flags]
internal enum RoomWindTargetKind
{
    None = 0,
    Vegetation = 1,
    Water = 2
}

internal readonly record struct ManagedRoomWindEnvelope(
    float MinX,
    float MinY,
    float MinZ,
    float MaxX,
    float MaxY,
    float MaxZ
)
{
    internal static ManagedRoomWindEnvelope UnitBlock { get; } =
        new(
            0f,
            0f,
            0f,
            1f,
            1f,
            1f
        );

    internal float CenterX =>
        (MinX + MaxX) * 0.5f;

    internal float CenterY =>
        (MinY + MaxY) * 0.5f;

    internal float CenterZ =>
        (MinZ + MaxZ) * 0.5f;

    internal float HalfExtentX =>
        Math.Max(
            0f,
            (MaxX - MinX) * 0.5f
        );

    internal float HalfExtentY =>
        Math.Max(
            0f,
            (MaxY - MinY) * 0.5f
        );

    internal float HalfExtentZ =>
        Math.Max(
            0f,
            (MaxZ - MinZ) * 0.5f
        );

    internal ManagedRoomWindEnvelope Union(
        ManagedRoomWindEnvelope other
    ) =>
        new(
            Math.Min(MinX, other.MinX),
            Math.Min(MinY, other.MinY),
            Math.Min(MinZ, other.MinZ),
            Math.Max(MaxX, other.MaxX),
            Math.Max(MaxY, other.MaxY),
            Math.Max(MaxZ, other.MaxZ)
        );

    internal ManagedRoomWindEnvelope Translate(
        float x,
        float y,
        float z
    ) =>
        new(
            MinX + x,
            MinY + y,
            MinZ + z,
            MaxX + x,
            MaxY + y,
            MaxZ + z
        );

}

internal readonly record struct RoomWindRegistrationReconcileResult(
    int RegisteredBefore,
    int Retained,
    int RoomTypeUpdated,
    int RoomIdentityUpdated,
    int Removed,
    int WaterTargetsRemoved
);

internal readonly record struct ManagedRoomWindRegistration(
    GreenhouseKey RoomKey,
    ManagedRoomType RoomType,
    RoomWindTargetKind Targets,
    ManagedRoomWindEnvelope VegetationEnvelope,
    ManagedRoomWindEnvelope WaterEnvelope,
    bool AllowRoomAbove,
    bool AllowXRunCompaction
)
{
    internal ManagedRoomWindEnvelope GetEnvelope(
        RoomWindTargetKind targets
    )
    {
        bool vegetation =
            (
                targets
                & RoomWindTargetKind.Vegetation
            ) != 0;

        bool water =
            (
                targets
                & RoomWindTargetKind.Water
            ) != 0;

        if (vegetation && water)
        {
            return VegetationEnvelope.Union(
                WaterEnvelope
            );
        }

        return vegetation
            ? VegetationEnvelope
            : WaterEnvelope;
    }
}

internal readonly record struct CompactedPositionEnvelope(
    ManagedVegetationShaderPosition Position,
    GreenhouseKey RoomKey,
    ManagedRoomType RoomType,
    RoomWindTargetKind Targets,
    ManagedRoomWindEnvelope Envelope,
    int CoveredPositionCount
);

internal sealed class StillGreenhousesRoomWindUniformRenderer :
    IRenderer
{
    // Legacy 0.17 uniform-array limits retained for rollback diagnostics and
    // compaction checks. The active 0.18 renderer uses the dynamic cell hash
    // and does not consult this budget.
    internal static int ShaderPositionCapacity
    {
        get
        {
            StillGreenhousesConfig? config =
                StillGreenhousesClientSystem.Config;

            if (config == null)
            {
                return StillGreenhousesConfig
                    .DefaultAffectedPlantPositions;
            }

            int maximum =
                config.ExperimentalExtendedPositionCapacity
                    ? StillGreenhousesConfig.MaximumAffectedPlantPositions
                    : StillGreenhousesConfig.StandardMaximumAffectedPlantPositions;

            return Math.Clamp(
                config.MaxAffectedPlants,
                StillGreenhousesConfig.MinimumAffectedPlantPositions,
                maximum
            );
        }
    }

    internal static int ConfiguredPositionBudget =>
        Math.Clamp(
            StillGreenhousesClientSystem.Config?.MaxAffectedPlants
                ?? StillGreenhousesConfig.DefaultAffectedPlantPositions,
            StillGreenhousesConfig.MinimumAffectedPlantPositions,
            ShaderPositionCapacity
        );

    internal static int GreenhouseReservedPositionBudget =>
        CalculateReservedPositionBudgets(
            ConfiguredPositionBudget
        ).Greenhouse;

    internal static int CellarReservedPositionBudget =>
        CalculateReservedPositionBudgets(
            ConfiguredPositionBudget
        ).Cellar;

    internal static int RoomReservedPositionBudget =>
        CalculateReservedPositionBudgets(
            ConfiguredPositionBudget
        ).Room;

    // Two states per ManagedRoomType. Indices 0..2 are vegetation and 3..5 are
    // water, each ordered Greenhouse, Cellar, normal Room.
    internal const int UploadedRoomTypeStateCount =
        StillGreenhousesRoomWindEnvironment
            .RoomTypeStateCount * 2;

    // One vec4 per room type. Position transport now uses Vintage Story's own
    // playerReferencePos-relative render coordinate space, so no custom
    // reference metadata vector is required.
    internal const int UploadedShaderStateVectorCount =
        UploadedRoomTypeStateCount;

    // Wind-vertex envelopes are measured from the actual tessellated mesh.
    // Half extents are quantized into six bits per axis at 1/8-block steps and
    // packed with room/target metadata into the exactly representable integer
    // range of a GLSL float.
    internal const float EnvelopeExtentQuantization = 0.125f;
    internal const float EnvelopeMeasurementPadding = 0.0625f;
    internal const int EnvelopeExtentBits = 6;
    internal const int EnvelopeExtentMask =
        (1 << EnvelopeExtentBits) - 1;
    internal const int PackedTargetBits = 4;
    internal const int PackedTargetMask =
        (1 << PackedTargetBits) - 1;

    // A one-percentage-point triangular step every 12 scaled game seconds.
    // Ranges narrower than 1% use the full configured span as their step.
    internal const float AmbientWindStepIntervalSeconds =
        12f;

    private const int SnapshotRefreshMs = 250;
    private const int CellHashRegistrationDebounceMs = 250;
    private const int CellHashTextureUnit = 15;
    private const int ProgramRecoveryRediscoveryMs = 5000;
    private const double TopologyMovementThresholdBlocks = 1d;
    private const double TopologyMovementThresholdSquared =
        TopologyMovementThresholdBlocks * TopologyMovementThresholdBlocks;

    // Overlap safety is evaluated only when registration topology changes.
    // Four-block cells keep dense 32^3 rooms near linear-local work instead
    // of placing every descriptor in one chunk-sized comparison bucket.
    private const double CompactionOverlapBucketSizeBlocks = 4d;

    private static readonly EnumShaderProgram[] TargetPrograms =
    [
        // Active terrain vegetation consumers. The supplied Vanilla top-level
        // shaders call applyVertexWarping() in these passes.
        EnumShaderProgram.Chunkopaque,
        EnumShaderProgram.Chunktransparent,
        EnumShaderProgram.Chunkshadowmap,
        EnumShaderProgram.Chunkshadowmap_NoSSBOs,

        // Auxiliary Vanilla consumers also include vertexwarp.vsh and call
        // applyVertexWarping(). They are optional for core terrain readiness,
        // but receive the same room position/state uniforms when available.
        EnumShaderProgram.Standard,
        EnumShaderProgram.Entityanimated,
        EnumShaderProgram.Shadowmapentityanimated,
        EnumShaderProgram.Entityanimated_Oit,

        // Liquid consumers use the separately patched applyLiquidWarping().
        EnumShaderProgram.Chunkliquid,
        EnumShaderProgram.Chunkliquiddepth
    ];

    internal const int TerrainVegetationTargetProgramCount = 4;
    internal const int AuxiliaryVegetationTargetProgramCount = 4;
    internal const int VegetationTargetProgramCount =
        TerrainVegetationTargetProgramCount
        + AuxiliaryVegetationTargetProgramCount;
    internal const int LiquidTargetProgramCount = 2;

    private static readonly ConcurrentDictionary<
        ManagedVegetationShaderPosition,
        ManagedRoomWindRegistration
    > RegisteredPositions = new();

    private static readonly ConcurrentDictionary<
        ChunkKey,
        ConcurrentDictionary<
            ManagedVegetationShaderPosition,
            byte
        >
    > RegisteredPositionsByChunk = new();

    private static readonly object RegistrationSync = new();

    private static int uploadedPositionCount;
    private static int uploadedGreenhousePositionCount;
    private static int uploadedCellarPositionCount;
    private static int uploadedRoomPositionCount;
    private static int validPositionCount;
    private static int compactedEnvelopeCount;
    private static int uploadedCoveredPositionCount;
    private static int uploadedRoomStateCount;
    private static int activeProgramCount;
    private static int activeVegetationProgramCount;
    private static int activeTerrainVegetationProgramCount;
    private static int activeAuxiliaryVegetationProgramCount;
    private static int activeLiquidProgramCount;
    private static int requiredChunkOpaqueBound;
    private static int snapshotRevision;
    private static int uniformBridgeReady;
    private static int registrationRevision;

    private static long topologyRefreshRuns;
    private static long topologyRefreshSkips;
    private static long topologyCandidatesEvaluated;
    private static long programDiscoveryRuns;
    private static long uniformUploadRuns;
    private static long uniformProgramBindOperations;

    private static long lastTopologyRefreshMicroseconds;
    private static long maxTopologyRefreshMicroseconds;
    private static long lastProgramDiscoveryMicroseconds;
    private static long maxProgramDiscoveryMicroseconds;
    private static long lastUniformUploadMicroseconds;
    private static long maxUniformUploadMicroseconds;

    private static string lastUniformUploadFailure =
        "<none>";

    private static float greenhouseCurrentPercent;
    private static float cellarCurrentPercent;
    private static float roomCurrentPercent;
    private static float greenhouseWaterCurrentPercent;
    private static float cellarWaterCurrentPercent;
    private static float roomWaterCurrentPercent;

    private readonly ICoreClientAPI api;
    private readonly RoomWindRuntimeState[]
        roomTypeStates;

    private readonly List<ShaderProgramBinding>
        programBindings = new();

    private readonly float[] stateValueBuffer =
        new float[UploadedShaderStateVectorCount * 4];

    // 0.18.0 replaces the per-vertex linear envelope scan with a bounded-probe
    // texture hash. The texture is rebuilt only after registration topology has
    // been stable for one refresh interval; ordinary player movement never
    // changes it.
    private RoomWindCellHashSnapshot cellHashSnapshot =
        RoomWindCellHash.Build(
            Array.Empty<RoomWindCellContribution>()
        );

    private LoadedTexture? cellHashTexture;
    private int cellHashRevision;
    private int lastBuiltHashRegistrationRevision = int.MinValue;
    private int lastBuiltHashDimension = int.MinValue;
    private int pendingHashRegistrationRevision = int.MinValue;
    private int pendingHashDimension = int.MinValue;
    private long pendingHashSinceMs;
    private int stateRevision = 1;
    private int renderReferenceRevision = 1;
    private int renderReferenceQuarterX;
    private int renderReferenceQuarterY;
    private int renderReferenceQuarterZ;
    private float renderReferenceQuarterFractionX;
    private float renderReferenceQuarterFractionY;
    private float renderReferenceQuarterFractionZ;

    private RoomWindTopologySnapshot topology =
        RoomWindTopologySnapshot.Empty;

    private long nextSnapshotRefreshMs;
    private long nextProgramDiscoveryMs;
    private int lastProgramDiagnosticHash =
        int.MinValue;

    private int lastObservedRegistrationRevision =
        int.MinValue;

    private int lastObservedPositionBudget =
        -1;

    private int compactedRegistrationRevision =
        int.MinValue;

    private List<CompactedPositionEnvelope>
        compactedRegistrations = new();

    private double lastSelectionPlayerX =
        double.NaN;

    private double lastSelectionPlayerY =
        double.NaN;

    private double lastSelectionPlayerZ =
        double.NaN;

    private int lastSelectionPlayerDimension =
        int.MinValue;

    private double lastRenderReferenceX =
        double.NaN;

    private double lastRenderReferenceY =
        double.NaN;

    private double lastRenderReferenceZ =
        double.NaN;

    private bool lastRenderReferenceAvailable;
    private bool topologyRefreshInitialized;
    private int disposed;
    private int positionLimitWarningLogged;
    private int compactionFailureWarningLogged;

    internal static int RegisteredPositionCount =>
        RegisteredPositions.Count;

    internal static int UploadedPositionCount =>
        Volatile.Read(
            ref uploadedPositionCount
        );

    internal static int UploadedGreenhousePositionCount =>
        Volatile.Read(
            ref uploadedGreenhousePositionCount
        );

    internal static int UploadedCellarPositionCount =>
        Volatile.Read(
            ref uploadedCellarPositionCount
        );

    internal static int UploadedRoomPositionCount =>
        Volatile.Read(
            ref uploadedRoomPositionCount
        );

    internal static int ValidPositionCount =>
        Volatile.Read(
            ref validPositionCount
        );

    internal static int CompactedEnvelopeCount =>
        Volatile.Read(
            ref compactedEnvelopeCount
        );

    internal static int UploadedCoveredPositionCount =>
        Volatile.Read(
            ref uploadedCoveredPositionCount
        );

    internal static int UploadedRoomStateCount =>
        Volatile.Read(
            ref uploadedRoomStateCount
        );

    internal static int ActiveProgramCount =>
        Volatile.Read(
            ref activeProgramCount
        );

    internal static int ActiveVegetationProgramCount =>
        Volatile.Read(
            ref activeVegetationProgramCount
        );

    internal static int ActiveTerrainVegetationProgramCount =>
        Volatile.Read(
            ref activeTerrainVegetationProgramCount
        );

    internal static int ActiveAuxiliaryVegetationProgramCount =>
        Volatile.Read(
            ref activeAuxiliaryVegetationProgramCount
        );

    internal static int ActiveLiquidProgramCount =>
        Volatile.Read(
            ref activeLiquidProgramCount
        );

    internal static bool RequiredChunkOpaqueBound =>
        Volatile.Read(
            ref requiredChunkOpaqueBound
        ) != 0;

    internal static bool UniformBridgeReady =>
        Volatile.Read(
            ref uniformBridgeReady
        ) != 0;

    internal static string LastUniformUploadFailure =>
        Volatile.Read(
            ref lastUniformUploadFailure
        );

    internal static int SnapshotRevision =>
        Volatile.Read(
            ref snapshotRevision
        );

    internal static int RegistrationRevision =>
        Volatile.Read(
            ref registrationRevision
        );

    internal static long TopologyRefreshRuns =>
        Interlocked.Read(
            ref topologyRefreshRuns
        );

    internal static long TopologyRefreshSkips =>
        Interlocked.Read(
            ref topologyRefreshSkips
        );

    internal static long TopologyCandidatesEvaluated =>
        Interlocked.Read(
            ref topologyCandidatesEvaluated
        );

    internal static long ProgramDiscoveryRuns =>
        Interlocked.Read(
            ref programDiscoveryRuns
        );

    internal static long UniformUploadRuns =>
        Interlocked.Read(
            ref uniformUploadRuns
        );

    internal static long UniformProgramBindOperations =>
        Interlocked.Read(
            ref uniformProgramBindOperations
        );

    internal static double LastTopologyRefreshMs =>
        Interlocked.Read(
            ref lastTopologyRefreshMicroseconds
        ) / 1000d;

    internal static double MaxTopologyRefreshMs =>
        Interlocked.Read(
            ref maxTopologyRefreshMicroseconds
        ) / 1000d;

    internal static double LastProgramDiscoveryMs =>
        Interlocked.Read(
            ref lastProgramDiscoveryMicroseconds
        ) / 1000d;

    internal static double MaxProgramDiscoveryMs =>
        Interlocked.Read(
            ref maxProgramDiscoveryMicroseconds
        ) / 1000d;

    internal static double LastUniformUploadMs =>
        Interlocked.Read(
            ref lastUniformUploadMicroseconds
        ) / 1000d;

    internal static double MaxUniformUploadMs =>
        Interlocked.Read(
            ref maxUniformUploadMicroseconds
        ) / 1000d;

    internal static int TargetProgramCount =>
        TargetPrograms.Length;

    internal static float GreenhouseCurrentPercent =>
        Volatile.Read(
            ref greenhouseCurrentPercent
        );

    internal static float CellarCurrentPercent =>
        Volatile.Read(
            ref cellarCurrentPercent
        );

    internal static float RoomCurrentPercent =>
        Volatile.Read(
            ref roomCurrentPercent
        );

    internal static float GreenhouseWaterCurrentPercent =>
        Volatile.Read(
            ref greenhouseWaterCurrentPercent
        );

    internal static float CellarWaterCurrentPercent =>
        Volatile.Read(
            ref cellarWaterCurrentPercent
        );

    internal static float RoomWaterCurrentPercent =>
        Volatile.Read(
            ref roomWaterCurrentPercent
        );

    public double RenderOrder => 0.99d;

    public int RenderRange => int.MaxValue;

    internal StillGreenhousesRoomWindUniformRenderer(
        ICoreClientAPI api
    )
    {
        this.api = api;

        Volatile.Write(
            ref lastUniformUploadFailure,
            "<none>"
        );

        StillGreenhousesConfig config =
            StillGreenhousesClientSystem.Config
            ?? new StillGreenhousesConfig();

        DefaultShaderUniforms uniforms =
            api.Render.ShaderUniforms;

        roomTypeStates =
        [
            CreateRoomTypeState(
                config,
                ManagedRoomType.Greenhouse,
                RoomWindTargetKind.Vegetation,
                uniforms,
                phaseOffsetSeconds: 0f
            ),
            CreateRoomTypeState(
                config,
                ManagedRoomType.Cellar,
                RoomWindTargetKind.Vegetation,
                uniforms,
                phaseOffsetSeconds:
                    AmbientWindStepIntervalSeconds / 3f
            ),
            CreateRoomTypeState(
                config,
                ManagedRoomType.Room,
                RoomWindTargetKind.Vegetation,
                uniforms,
                phaseOffsetSeconds:
                    AmbientWindStepIntervalSeconds * 2f / 3f
            ),
            CreateRoomTypeState(
                config,
                ManagedRoomType.Greenhouse,
                RoomWindTargetKind.Water,
                uniforms,
                phaseOffsetSeconds: 0f
            ),
            CreateRoomTypeState(
                config,
                ManagedRoomType.Cellar,
                RoomWindTargetKind.Water,
                uniforms,
                phaseOffsetSeconds:
                    AmbientWindStepIntervalSeconds / 3f
            ),
            CreateRoomTypeState(
                config,
                ManagedRoomType.Room,
                RoomWindTargetKind.Water,
                uniforms,
                phaseOffsetSeconds:
                    AmbientWindStepIntervalSeconds * 2f / 3f
            )
        ];

        PublishStateMetrics();

        foreach (
            RoomWindRuntimeState state
            in roomTypeStates
        )
        {
            StillGreenhousesClientSystem.DebugLiteral(
                "[StillGreenhouses] ROOM TYPE WIND STATE CREATED " +
                $"roomType={state.RoomType}; " +
                $"target={state.Target}; " +
                $"range={state.LowerPercent:0.###}-{state.UpperPercent:0.###}%; " +
                $"fixed={state.IsFixed}; " +
                $"currentPercent={state.CurrentPercent:0.###}; " +
                $"windSpeed={state.WindSpeed:0.######}; " +
                $"stepIntervalSeconds={AmbientWindStepIntervalSeconds:0.###}; " +
                $"initialWindWaveCounter={uniforms.WindWaveCounter:0.######}; " +
                $"initialWindWaveCounterHighFreq={uniforms.WindWaveCounterHighFreq:0.######}"
            );
        }
    }

    private static RoomWindRuntimeState CreateRoomTypeState(
        StillGreenhousesConfig config,
        ManagedRoomType roomType,
        RoomWindTargetKind target,
        DefaultShaderUniforms uniforms,
        float phaseOffsetSeconds
    )
    {
        RoomWindRange range =
            StillGreenhousesRoomWindEnvironment
                .GetRange(
                    config,
                    roomType,
                    target
                );

        return new RoomWindRuntimeState(
            roomType,
            target,
            range,
            uniforms.WindWaveCounter,
            uniforms.WindWaveCounterHighFreq,
            phaseOffsetSeconds
        );
    }

    internal static bool TryMeasureWindVertexEnvelope(
        MeshData mesh,
        out ManagedRoomWindEnvelope allVertexEnvelope,
        out ManagedRoomWindEnvelope windVertexEnvelope,
        out int measuredVertexCount,
        out int windVertexCount
    )
    {
        allVertexEnvelope = default;
        windVertexEnvelope = default;
        measuredVertexCount = 0;
        windVertexCount = 0;

        if (
            mesh.xyz == null
            || mesh.Flags == null
        )
        {
            return false;
        }

        int vertexCount = Math.Min(
            mesh.VerticesCount,
            Math.Min(
                mesh.Flags.Length,
                mesh.xyz.Length / 3
            )
        );

        bool allInitialized = false;
        bool windInitialized = false;

        float allMinX = 0f;
        float allMinY = 0f;
        float allMinZ = 0f;
        float allMaxX = 0f;
        float allMaxY = 0f;
        float allMaxZ = 0f;

        float windMinX = 0f;
        float windMinY = 0f;
        float windMinZ = 0f;
        float windMaxX = 0f;
        float windMaxY = 0f;
        float windMaxZ = 0f;

        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            int xyzOffset = vertex * 3;

            float x = mesh.xyz[xyzOffset];
            float y = mesh.xyz[xyzOffset + 1];
            float z = mesh.xyz[xyzOffset + 2];

            if (
                !float.IsFinite(x)
                || !float.IsFinite(y)
                || !float.IsFinite(z)
            )
            {
                continue;
            }

            measuredVertexCount++;

            if (!allInitialized)
            {
                allMinX = allMaxX = x;
                allMinY = allMaxY = y;
                allMinZ = allMaxZ = z;
                allInitialized = true;
            }
            else
            {
                allMinX = Math.Min(allMinX, x);
                allMinY = Math.Min(allMinY, y);
                allMinZ = Math.Min(allMinZ, z);
                allMaxX = Math.Max(allMaxX, x);
                allMaxY = Math.Max(allMaxY, y);
                allMaxZ = Math.Max(allMaxZ, z);
            }

            if (
                (
                    mesh.Flags[vertex]
                    & VertexFlags.WindModeBitsMask
                ) == 0
            )
            {
                continue;
            }

            windVertexCount++;

            if (!windInitialized)
            {
                windMinX = windMaxX = x;
                windMinY = windMaxY = y;
                windMinZ = windMaxZ = z;
                windInitialized = true;
            }
            else
            {
                windMinX = Math.Min(windMinX, x);
                windMinY = Math.Min(windMinY, y);
                windMinZ = Math.Min(windMinZ, z);
                windMaxX = Math.Max(windMaxX, x);
                windMaxY = Math.Max(windMaxY, y);
                windMaxZ = Math.Max(windMaxZ, z);
            }
        }

        if (!allInitialized || !windInitialized)
        {
            return false;
        }

        allVertexEnvelope = new ManagedRoomWindEnvelope(
            allMinX,
            allMinY,
            allMinZ,
            allMaxX,
            allMaxY,
            allMaxZ
        );

        windVertexEnvelope = new ManagedRoomWindEnvelope(
            windMinX,
            windMinY,
            windMinZ,
            windMaxX,
            windMaxY,
            windMaxZ
        );

        return true;
    }

    internal static void RegisterPosition(
        BlockPos pos,
        GreenhouseRegion room,
        ManagedRoomWindEnvelope windVertexEnvelope,
        bool allowRoomAbove = false,
        bool allowXRunCompaction = false
    ) =>
        RegisterTargetPosition(
            pos,
            room,
            RoomWindTargetKind.Vegetation,
            windVertexEnvelope,
            allowRoomAbove,
            allowXRunCompaction
        );

    internal static void RegisterWaterPosition(
        BlockPos pos,
        GreenhouseRegion room
    ) =>
        RegisterTargetPosition(
            pos,
            room,
            RoomWindTargetKind.Water,
            ManagedRoomWindEnvelope.UnitBlock,
            allowRoomAbove: false,
            allowXRunCompaction: true
        );

    private static void RegisterTargetPosition(
        BlockPos pos,
        GreenhouseRegion room,
        RoomWindTargetKind target,
        ManagedRoomWindEnvelope envelope,
        bool allowRoomAbove,
        bool allowXRunCompaction
    )
    {
        RoomPlantMovementMode movementMode =
            StillGreenhousesClientSystem
                .PlantMovementMode;

        if (
            !StillGreenhousesRoomWindEnvironment
                .IsShaderDrivenMode(movementMode)
        )
        {
            RemoveTargetPosition(pos, target);

            return;
        }

        ManagedVegetationShaderPosition key =
            ManagedVegetationShaderPosition.From(pos);

        ChunkKey chunkKey =
            ChunkKey.From(pos);

        lock (RegistrationSync)
        {
            bool hadExisting =
                RegisteredPositions.TryGetValue(
                    key,
                    out ManagedRoomWindRegistration existingBefore
                );

            ManagedRoomWindRegistration updated;

            if (!hadExisting)
            {
                updated = new ManagedRoomWindRegistration(
                    room.Key,
                    room.RoomType,
                    target,
                    target == RoomWindTargetKind.Vegetation
                        ? envelope
                        : default,
                    target == RoomWindTargetKind.Water
                        ? envelope
                        : default,
                    target == RoomWindTargetKind.Vegetation
                        && allowRoomAbove,
                    allowXRunCompaction
                        && !allowRoomAbove
                );
            }
            else
            {
                bool hadVegetation =
                    (
                        existingBefore.Targets
                        & RoomWindTargetKind.Vegetation
                    ) != 0;

                ManagedRoomWindEnvelope vegetationEnvelope =
                    target == RoomWindTargetKind.Vegetation
                        ? hadVegetation
                            ? existingBefore.VegetationEnvelope.Union(
                                envelope
                            )
                            : envelope
                        : existingBefore.VegetationEnvelope;

                if (
                    target == RoomWindTargetKind.Vegetation
                    && hadVegetation
                    && vegetationEnvelope
                        != existingBefore.VegetationEnvelope
                    && StillGreenhousesClientSystem.Config
                        ?.DebugLogging == true
                )
                {
                    StillGreenhousesClientSystem.DebugLiteral(
                        "[StillGreenhouses] ROOM WIND REGISTRATION MERGE "
                        + $"pos={key.X},{key.Y},{key.Z}; "
                        + $"dim={key.Dimension}; "
                        + $"oldEnvelope={FormatEnvelope(existingBefore.VegetationEnvelope)}; "
                        + $"newEnvelope={FormatEnvelope(envelope)}; "
                        + $"mergedEnvelope={FormatEnvelope(vegetationEnvelope)}"
                    );
                }

                updated = existingBefore with
                {
                    RoomKey = room.Key,
                    RoomType = room.RoomType,
                    Targets = existingBefore.Targets | target,
                    VegetationEnvelope = vegetationEnvelope,
                    WaterEnvelope =
                        target == RoomWindTargetKind.Water
                            ? envelope
                            : existingBefore.WaterEnvelope,
                    AllowRoomAbove =
                        existingBefore.AllowRoomAbove
                        || (
                            target == RoomWindTargetKind.Vegetation
                            && allowRoomAbove
                        ),
                    // A second, different registration at the same block can
                    // represent a multipart mesh or a mixed water/vegetation
                    // target. Keep those one-for-one for the conservative
                    // compaction path.
                    AllowXRunCompaction =
                        existingBefore.AllowXRunCompaction
                        && allowXRunCompaction
                        && !allowRoomAbove
                        && existingBefore.RoomKey == room.Key
                        && existingBefore.RoomType == room.RoomType
                        && existingBefore.Targets == target
                        && (
                            target == RoomWindTargetKind.Water
                                ? existingBefore.WaterEnvelope == envelope
                                : existingBefore.VegetationEnvelope == envelope
                        )
                };
            }

            RegisteredPositions[key] = updated;

            RegisteredPositionsByChunk
                .GetOrAdd(
                    chunkKey,
                    _ => new ConcurrentDictionary<
                        ManagedVegetationShaderPosition,
                        byte
                    >()
                )[key] = 0;

            if (
                !hadExisting
                || updated != existingBefore
            )
            {
                Interlocked.Increment(
                    ref registrationRevision
                );
            }
        }
    }

    private static string FormatEnvelope(
        ManagedRoomWindEnvelope envelope
    ) =>
        FormattableString.Invariant(
            $"[{envelope.MinX:0.###},{envelope.MinY:0.###},{envelope.MinZ:0.###}..{envelope.MaxX:0.###},{envelope.MaxY:0.###},{envelope.MaxZ:0.###}]"
        );

    internal static bool HasRegisteredVegetationPositionAffectedByMembership(
        BlockPos membershipPos
    )
    {
        ManagedVegetationShaderPosition exactKey =
            ManagedVegetationShaderPosition.From(
                membershipPos
            );

        if (RegisteredPositions.TryGetValue(
                exactKey,
                out ManagedRoomWindRegistration exactRegistration
            )
            && (
                exactRegistration.Targets
                & RoomWindTargetKind.Vegetation
            ) != 0)
        {
            return true;
        }

        ManagedVegetationShaderPosition belowKey =
            ManagedVegetationShaderPosition.From(
                membershipPos.DownCopy()
            );

        return RegisteredPositions.TryGetValue(
                   belowKey,
                   out ManagedRoomWindRegistration belowRegistration
               )
               && belowRegistration.AllowRoomAbove
               && (
                   belowRegistration.Targets
                   & RoomWindTargetKind.Vegetation
               ) != 0;
    }

    internal static bool HasRegisteredVegetationPositionsForChunk(
        ChunkKey chunkKey
    )
    {
        if (!RegisteredPositionsByChunk.TryGetValue(
                chunkKey,
                out ConcurrentDictionary<
                    ManagedVegetationShaderPosition,
                    byte
                >? index
            ))
        {
            return false;
        }

        foreach (
            ManagedVegetationShaderPosition position
            in index.Keys
        )
        {
            if (
                RegisteredPositions.TryGetValue(
                    position,
                    out ManagedRoomWindRegistration registration
                )
                && (
                    registration.Targets
                    & RoomWindTargetKind.Vegetation
                ) != 0
            )
            {
                return true;
            }
        }

        return false;
    }

    internal static void RemovePosition(
        BlockPos pos
    ) =>
        RemoveRegisteredPosition(
            ManagedVegetationShaderPosition.From(pos)
        );

    internal static void RemoveWaterPosition(
        BlockPos pos
    ) =>
        RemoveTargetPosition(
            pos,
            RoomWindTargetKind.Water
        );

    private static bool RemoveTargetPosition(
        BlockPos pos,
        RoomWindTargetKind target
    )
    {
        ManagedVegetationShaderPosition position =
            ManagedVegetationShaderPosition.From(pos);

        lock (RegistrationSync)
        {
            if (!RegisteredPositions.TryGetValue(
                    position,
                    out ManagedRoomWindRegistration registration
                )
                || (
                    registration.Targets
                    & target
                ) == 0)
            {
                return false;
            }

            RoomWindTargetKind remainingTargets =
                registration.Targets
                & ~target;

            if (remainingTargets == RoomWindTargetKind.None)
            {
                return RemoveRegisteredPosition(
                    position
                );
            }

            bool vegetationRemaining =
                (
                    remainingTargets
                    & RoomWindTargetKind.Vegetation
                ) != 0;

            bool waterRemaining =
                (
                    remainingTargets
                    & RoomWindTargetKind.Water
                ) != 0;

            RegisteredPositions[position] =
                registration with
                {
                    Targets = remainingTargets,
                    VegetationEnvelope =
                        vegetationRemaining
                            ? registration.VegetationEnvelope
                            : default,
                    WaterEnvelope =
                        waterRemaining
                            ? registration.WaterEnvelope
                            : default,
                    AllowRoomAbove =
                        vegetationRemaining
                        && registration.AllowRoomAbove,
                    // Water registrations always use unit-block X-run
                    // compaction. If vegetation remains, retain its existing
                    // conservative eligibility instead of guessing after a
                    // mixed registration has been split.
                    AllowXRunCompaction =
                        waterRemaining
                        && !vegetationRemaining
                            ? true
                            : registration.AllowXRunCompaction
                };

            Interlocked.Increment(
                ref registrationRevision
            );

            return true;
        }
    }

    private static bool RemoveRegisteredPosition(
        ManagedVegetationShaderPosition position
    )
    {
        lock (RegistrationSync)
        {
            bool removed =
                RegisteredPositions.TryRemove(
                    position,
                    out _
                );

            if (!removed)
            {
                return false;
            }

            ChunkKey chunkKey =
                ChunkKey.From(
                    position.ToBlockPos()
                );

            if (
                RegisteredPositionsByChunk.TryGetValue(
                    chunkKey,
                    out ConcurrentDictionary<
                        ManagedVegetationShaderPosition,
                        byte
                    >? index
                )
            )
            {
                index.TryRemove(
                    position,
                    out _
                );

                if (index.IsEmpty)
                {
                    RegisteredPositionsByChunk.TryRemove(
                        chunkKey,
                        out _
                    );
                }
            }

            Interlocked.Increment(
                ref registrationRevision
            );

            return true;
        }
    }

    internal static RoomWindRegistrationReconcileResult
        ReconcilePositionsForChunk(
            ChunkKey chunkKey
        )
    {
        lock (RegistrationSync)
        {
            if (!RegisteredPositionsByChunk.TryGetValue(
                    chunkKey,
                    out ConcurrentDictionary<
                        ManagedVegetationShaderPosition,
                        byte
                    >? index
                ))
            {
                return default;
            }

            int registeredBefore = index.Count;
            int retained = 0;
            int roomTypeUpdated = 0;
            int roomIdentityUpdated = 0;
            int removed = 0;
            int waterTargetsRemoved = 0;

            ICoreClientAPI? clientApi =
                StillGreenhousesClientSystem.Capi;

            bool waterPipelineEnabled =
                StillGreenhousesClientSystem.Config
                    ?.ApplyToWater == true
                && StillGreenhousesRoomWindEnvironment
                    .IsShaderDrivenMode(
                        StillGreenhousesClientSystem
                            .PlantMovementMode
                    );

            foreach (
                ManagedVegetationShaderPosition position
                in index.Keys
            )
            {
                if (!RegisteredPositions.TryGetValue(
                        position,
                        out ManagedRoomWindRegistration registration
                    ))
                {
                    index.TryRemove(position, out _);
                    continue;
                }

                BlockPos blockPos = position.ToBlockPos();

                bool hasWaterTarget =
                    (
                        registration.Targets
                        & RoomWindTargetKind.Water
                    ) != 0;

                if (
                    hasWaterTarget
                    && clientApi != null
                    && (
                        !waterPipelineEnabled
                        || !StillGreenhousesShared
                            .IsWaterSurfaceSourceBlock(
                                clientApi.World.BlockAccessor,
                                blockPos
                            )
                    )
                )
                {
                    RoomWindTargetKind remainingTargets =
                        registration.Targets
                        & ~RoomWindTargetKind.Water;

                    waterTargetsRemoved++;

                    if (
                        remainingTargets
                        == RoomWindTargetKind.None
                    )
                    {
                        if (RegisteredPositions.TryRemove(
                                position,
                                out _
                            ))
                        {
                            removed++;
                        }

                        index.TryRemove(position, out _);
                        continue;
                    }

                    registration = registration with
                    {
                        Targets = remainingTargets,
                        WaterEnvelope = default
                    };

                    RegisteredPositions[position] =
                        registration;
                }

                bool hasVegetationTarget =
                    (
                        registration.Targets
                        & RoomWindTargetKind.Vegetation
                    ) != 0;

                GreenhouseRegion? room;
                bool roomResolved;

                if (
                    hasVegetationTarget
                    && registration.AllowRoomAbove
                )
                {
                    roomResolved =
                        StillGreenhousesClientSystem
                            .TryGetCachedWindMeshRoom(
                                blockPos,
                                requestIfUnknown: false,
                                out room
                            );
                }
                else
                {
                    roomResolved =
                        StillGreenhousesClientSystem
                            .TryGetCachedGreenhouse(
                                blockPos,
                                requestIfUnknown: false,
                                out room
                            );
                }

                if (!roomResolved || room == null)
                {
                    if (RegisteredPositions.TryRemove(position, out _))
                    {
                        removed++;
                    }

                    index.TryRemove(position, out _);
                    continue;
                }

                retained++;

                bool roomTypeChanged =
                    registration.RoomType != room.RoomType;

                bool roomIdentityChanged =
                    registration.RoomKey != room.Key;

                if (
                    !roomTypeChanged
                    && !roomIdentityChanged
                )
                {
                    continue;
                }

                RegisteredPositions[position] =
                    registration with
                    {
                        RoomKey = room.Key,
                        RoomType = room.RoomType
                    };

                if (roomTypeChanged)
                {
                    roomTypeUpdated++;
                }

                if (roomIdentityChanged)
                {
                    roomIdentityUpdated++;
                }
            }

            if (index.IsEmpty)
            {
                RegisteredPositionsByChunk.TryRemove(
                    chunkKey,
                    out _
                );
            }

            RoomWindRegistrationReconcileResult result = new(
                registeredBefore,
                retained,
                roomTypeUpdated,
                roomIdentityUpdated,
                removed,
                waterTargetsRemoved
            );

            if (
                roomTypeUpdated > 0
                || roomIdentityUpdated > 0
                || removed > 0
                || waterTargetsRemoved > 0
            )
            {
                Interlocked.Increment(
                    ref registrationRevision
                );
            }

            if (
                StillGreenhousesClientSystem.Config
                    ?.DebugLogging == true
                && registeredBefore > 0
            )
            {
                StillGreenhousesClientSystem.DebugLiteral(
                    "[StillGreenhouses] CLIENT ROOM WIND RECONCILE " +
                    $"chunk={chunkKey.X},{chunkKey.Y},{chunkKey.Z}; " +
                    $"dim={chunkKey.Dimension}; " +
                    $"registeredBefore={result.RegisteredBefore}; " +
                    $"retained={result.Retained}; " +
                    $"roomTypeUpdated={result.RoomTypeUpdated}; " +
                    $"roomIdentityUpdated={result.RoomIdentityUpdated}; " +
                    $"removed={result.Removed}; " +
                    $"waterTargetsRemoved={result.WaterTargetsRemoved}"
                );
            }

            return result;
        }
    }

    internal static int RemovePositionsForChunk(
        ChunkKey chunkKey
    )
    {
        lock (RegistrationSync)
        {
            if (!RegisteredPositionsByChunk.TryRemove(
                    chunkKey,
                    out ConcurrentDictionary<
                        ManagedVegetationShaderPosition,
                        byte
                    >? index
                ))
            {
                return 0;
            }

            int removed = 0;

            foreach (
                ManagedVegetationShaderPosition position
                in index.Keys
            )
            {
                if (RegisteredPositions.TryRemove(
                        position,
                        out _
                    ))
                {
                    removed++;
                }
            }

            if (removed > 0)
            {
                Interlocked.Increment(
                    ref registrationRevision
                );
            }

            return removed;
        }
    }

    internal static void ClearRegisteredPositions()
    {
        lock (RegistrationSync)
        {
            RegisteredPositions.Clear();
            RegisteredPositionsByChunk.Clear();

            Interlocked.Increment(
                ref registrationRevision
            );
        }

        Volatile.Write(
            ref uploadedPositionCount,
            0
        );

        Volatile.Write(
            ref uploadedGreenhousePositionCount,
            0
        );

        Volatile.Write(
            ref uploadedCellarPositionCount,
            0
        );

        Volatile.Write(
            ref uploadedRoomPositionCount,
            0
        );

        Volatile.Write(
            ref validPositionCount,
            0
        );

        Volatile.Write(
            ref compactedEnvelopeCount,
            0
        );

        Volatile.Write(
            ref uploadedCoveredPositionCount,
            0
        );

        Volatile.Write(
            ref uploadedRoomStateCount,
            0
        );
    }

    internal void PrepareForWorldTransition()
    {
        programBindings.Clear();
        topology = RoomWindTopologySnapshot.Empty;
        nextSnapshotRefreshMs = 0;
        nextProgramDiscoveryMs = 0;
        lastProgramDiagnosticHash = int.MinValue;
        lastObservedRegistrationRevision = int.MinValue;
        lastObservedPositionBudget = -1;
        compactedRegistrationRevision = int.MinValue;
        compactedRegistrations.Clear();
        lastSelectionPlayerX = double.NaN;
        lastSelectionPlayerY = double.NaN;
        lastSelectionPlayerZ = double.NaN;
        lastSelectionPlayerDimension = int.MinValue;
        lastRenderReferenceX = double.NaN;
        lastRenderReferenceY = double.NaN;
        lastRenderReferenceZ = double.NaN;
        lastRenderReferenceAvailable = false;
        topologyRefreshInitialized = false;
        positionLimitWarningLogged = 0;
        compactionFailureWarningLogged = 0;
        cellHashSnapshot = RoomWindCellHash.Build(
            Array.Empty<RoomWindCellContribution>()
        );
        cellHashRevision++;
        lastBuiltHashRegistrationRevision = int.MinValue;
        lastBuiltHashDimension = int.MinValue;
        pendingHashRegistrationRevision = int.MinValue;
        pendingHashDimension = int.MinValue;
        pendingHashSinceMs = 0;
        stateRevision++;
        renderReferenceRevision++;
        renderReferenceQuarterX = 0;
        renderReferenceQuarterY = 0;
        renderReferenceQuarterZ = 0;
        renderReferenceQuarterFractionX = 0f;
        renderReferenceQuarterFractionY = 0f;
        renderReferenceQuarterFractionZ = 0f;

        DisposeCellHashTexture();

        Volatile.Write(ref activeProgramCount, 0);
        Volatile.Write(ref activeVegetationProgramCount, 0);
        Volatile.Write(ref activeTerrainVegetationProgramCount, 0);
        Volatile.Write(ref activeAuxiliaryVegetationProgramCount, 0);
        Volatile.Write(ref activeLiquidProgramCount, 0);
        Volatile.Write(ref requiredChunkOpaqueBound, 0);
        Volatile.Write(ref uniformBridgeReady, 0);
        Volatile.Write(ref uploadedRoomStateCount, 0);
        Volatile.Write(ref compactedEnvelopeCount, 0);
        Volatile.Write(ref uploadedCoveredPositionCount, 0);
        Volatile.Write(ref lastUniformUploadFailure, "<none>");
    }

    public void OnRenderFrame(
        float deltaTime,
        EnumRenderStage stage
    )
    {
        if (
            stage != EnumRenderStage.Before
            || Volatile.Read(ref disposed) != 0
            || !StillGreenhousesRoomWindShaderPatch.Ready
        )
        {
            return;
        }

        AdvanceRoomTypeStates(
            deltaTime
        );

        UpdateRenderReference();

        long elapsedMs =
            api.ElapsedMilliseconds;

        if (
            elapsedMs
                >= nextSnapshotRefreshMs
        )
        {
            nextSnapshotRefreshMs =
                elapsedMs
                + SnapshotRefreshMs;

            RefreshTopologySnapshot();
        }

        bool firstRenderAfterReload =
            StillGreenhousesRoomWindShaderPatch
                .TryConsumeCompiledVerificationPending();

        if (firstRenderAfterReload)
        {
            DiscoverPrograms(
                "FirstRenderAfterReload"
            );

            nextProgramDiscoveryMs =
                elapsedMs
                + ProgramRecoveryRediscoveryMs;
        }
        else if (
            elapsedMs
                >= nextProgramDiscoveryMs
        )
        {
            nextProgramDiscoveryMs =
                elapsedMs
                + ProgramRecoveryRediscoveryMs;

            if (NeedsProgramRediscovery())
            {
                DiscoverPrograms(
                    "RecoveryVerification"
                );
            }
        }

        UploadRoomWindState();
    }

    private bool NeedsProgramRediscovery()
    {
        if (
            programBindings.Count == 0
            || !UniformBridgeReady
        )
        {
            return true;
        }

        foreach (
            ShaderProgramBinding binding
            in programBindings
        )
        {
            if (binding.Program.Disposed)
            {
                return true;
            }
        }

        return false;
    }

    private void AdvanceRoomTypeStates(
        float deltaTime
    )
    {
        float scaledDt =
            deltaTime
            * api.World.Calendar.SpeedOfTime
            / 60f;

        if (api.IsGamePaused)
        {
            scaledDt = 0f;
        }

        bool stateChanged = false;

        foreach (
            RoomWindRuntimeState state
            in roomTypeStates
        )
        {
            stateChanged |= state.Advance(scaledDt);
        }

        if (stateChanged)
        {
            stateRevision++;
            PublishStateMetrics();
        }
    }

    private void PublishStateMetrics()
    {
        Volatile.Write(
            ref greenhouseCurrentPercent,
            roomTypeStates[
                StillGreenhousesRoomWindEnvironment
                    .GetStateIndex(
                        ManagedRoomType.Greenhouse
                    )
            ].CurrentPercent
        );

        Volatile.Write(
            ref cellarCurrentPercent,
            roomTypeStates[
                StillGreenhousesRoomWindEnvironment
                    .GetStateIndex(
                        ManagedRoomType.Cellar
                    )
            ].CurrentPercent
        );

        Volatile.Write(
            ref roomCurrentPercent,
            roomTypeStates[
                StillGreenhousesRoomWindEnvironment
                    .GetStateIndex(
                        ManagedRoomType.Room
                    )
            ].CurrentPercent
        );

        Volatile.Write(
            ref greenhouseWaterCurrentPercent,
            roomTypeStates[
                StillGreenhousesRoomWindEnvironment
                    .GetStateIndex(
                        ManagedRoomType.Greenhouse,
                        RoomWindTargetKind.Water
                    )
            ].CurrentPercent
        );

        Volatile.Write(
            ref cellarWaterCurrentPercent,
            roomTypeStates[
                StillGreenhousesRoomWindEnvironment
                    .GetStateIndex(
                        ManagedRoomType.Cellar,
                        RoomWindTargetKind.Water
                    )
            ].CurrentPercent
        );

        Volatile.Write(
            ref roomWaterCurrentPercent,
            roomTypeStates[
                StillGreenhousesRoomWindEnvironment
                    .GetStateIndex(
                        ManagedRoomType.Room,
                        RoomWindTargetKind.Water
                    )
            ].CurrentPercent
        );
    }

    private void RefreshTopologySnapshot()
    {
        RefreshCellHashSnapshot();
    }

    private void RefreshCellHashSnapshot()
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        int registrationRevisionAtStart = RegistrationRevision;
        int dimension = api.World.Player?.Entity?.Pos.Dimension
            ?? int.MinValue;

        if (
            registrationRevisionAtStart == lastBuiltHashRegistrationRevision
            && dimension == lastBuiltHashDimension
        )
        {
            Interlocked.Increment(ref topologyRefreshSkips);
            return;
        }

        long elapsedMs = api.ElapsedMilliseconds;

        if (
            registrationRevisionAtStart != pendingHashRegistrationRevision
            || dimension != pendingHashDimension
        )
        {
            if (
                dimension != lastBuiltHashDimension
                && lastBuiltHashDimension != int.MinValue
            )
            {
                RoomWindCellHashSnapshot emptySnapshot =
                    RoomWindCellHash.Build(
                        Array.Empty<RoomWindCellContribution>()
                    );
                cellHashSnapshot = emptySnapshot;
                cellHashRevision++;
                Volatile.Write(ref uploadedPositionCount, 0);

                try
                {
                    UploadCellHashTexture(emptySnapshot);
                }
                catch
                {
                    // The next stable build retries the texture upload. The
                    // published zero entry count already prevents stale cells
                    // from matching in the new dimension.
                }
            }

            pendingHashRegistrationRevision = registrationRevisionAtStart;
            pendingHashDimension = dimension;
            pendingHashSinceMs = elapsedMs;
            Interlocked.Increment(ref topologyRefreshSkips);
            return;
        }

        if (
            elapsedMs - pendingHashSinceMs
                < CellHashRegistrationDebounceMs
        )
        {
            Interlocked.Increment(ref topologyRefreshSkips);
            return;
        }

        List<RoomWindCellContribution> contributions = new();
        int registrationCount = 0;
        int greenhouseCount = 0;
        int cellarCount = 0;
        int roomCount = 0;
        bool applyToWater = StillGreenhousesClientSystem.Config
            ?.ApplyToWater == true;

        foreach (
            KeyValuePair<
                ManagedVegetationShaderPosition,
                ManagedRoomWindRegistration
            > pair in RegisteredPositions
        )
        {
            ManagedVegetationShaderPosition position = pair.Key;

            if (position.Dimension != dimension)
            {
                continue;
            }

            ManagedRoomWindRegistration registration = pair.Value;
            int stateIndex = StillGreenhousesRoomWindEnvironment
                .GetStateIndex(registration.RoomType);
            bool included = false;

            if (
                (registration.Targets & RoomWindTargetKind.Vegetation) != 0
            )
            {
                AddEnvelopeCellContributions(
                    contributions,
                    position,
                    registration.VegetationEnvelope,
                    stateIndex,
                    RoomWindTargetKind.Vegetation,
                    EnvelopeMeasurementPadding
                );

                included = true;
            }

            if (
                applyToWater
                && (registration.Targets & RoomWindTargetKind.Water) != 0
            )
            {
                // Water uses its exact registered block envelope. Vegetation's
                // measurement padding must never bleed water motion into an
                // adjacent liquid block.
                AddEnvelopeCellContributions(
                    contributions,
                    position,
                    registration.WaterEnvelope,
                    stateIndex,
                    RoomWindTargetKind.Water,
                    padding: 0d
                );

                included = true;
            }

            if (!included)
            {
                continue;
            }

            registrationCount++;

            switch (registration.RoomType)
            {
                case ManagedRoomType.Greenhouse:
                    greenhouseCount++;
                    break;
                case ManagedRoomType.Cellar:
                    cellarCount++;
                    break;
                default:
                    roomCount++;
                    break;
            }
        }

        // Registrations may be produced by chunk tessellation threads while the
        // render thread is taking this snapshot. Publish only a stable revision;
        // otherwise wait for the next debounce window and try again.
        if (registrationRevisionAtStart != RegistrationRevision)
        {
            pendingHashRegistrationRevision = RegistrationRevision;
            pendingHashSinceMs = elapsedMs;
            Interlocked.Increment(ref topologyRefreshSkips);
            return;
        }

        try
        {
            RoomWindCellHashSnapshot nextSnapshot =
                RoomWindCellHash.Build(contributions);

            UploadCellHashTexture(nextSnapshot);
            cellHashSnapshot = nextSnapshot;
            cellHashRevision++;
            lastBuiltHashRegistrationRevision = registrationRevisionAtStart;
            lastBuiltHashDimension = dimension;
            pendingHashRegistrationRevision = registrationRevisionAtStart;
            pendingHashDimension = dimension;

            Volatile.Write(ref uploadedPositionCount, nextSnapshot.EntryCount);
            Volatile.Write(ref validPositionCount, registrationCount);
            Volatile.Write(ref compactedEnvelopeCount, registrationCount);
            Volatile.Write(
                ref uploadedCoveredPositionCount,
                nextSnapshot.EntryCount
            );
            PublishUploadedPositionTypeCounts(
                greenhouseCount,
                cellarCount,
                roomCount
            );
            Interlocked.Increment(ref snapshotRevision);

            if (StillGreenhousesClientSystem.Config?.DebugLogging == true)
            {
                StillGreenhousesClientSystem.DebugLiteral(
                    "[StillGreenhouses] ROOM WIND CELL HASH "
                    + $"registrationRevision={registrationRevisionAtStart}; "
                    + $"dimension={dimension}; "
                    + $"registrations={registrationCount}; "
                    + $"contributions={contributions.Count}; "
                    + $"cells={nextSnapshot.EntryCount}; "
                    + $"capacity={nextSnapshot.Capacity}; "
                    + $"texture={nextSnapshot.Width}x{nextSnapshot.Height}; "
                    + $"maxProbe={nextSnapshot.MaxProbeCountUsed}/{RoomWindCellHash.ProbeLimit}; "
                    + $"vegetationConflicts={nextSnapshot.VegetationConflictCount}; "
                    + $"waterConflicts={nextSnapshot.WaterConflictCount}"
                );
            }

            RecordTopologyRefreshPerformance(
                startTimestamp,
                contributions.Count,
                "cell-hash-registration-revision"
            );
        }
        catch (Exception e)
        {
            StillGreenhousesClientSystem.WarningLiteral(
                "[StillGreenhouses] ROOM WIND CELL HASH BUILD FAILED "
                + $"registrationRevision={registrationRevisionAtStart}; "
                + $"dimension={dimension}; "
                + $"error={e.GetType().Name}:{e.Message}; "
                + "fallback=previous-hash-or-vanilla"
            );

            pendingHashSinceMs = elapsedMs;
        }
    }

    private static void AddEnvelopeCellContributions(
        List<RoomWindCellContribution> contributions,
        ManagedVegetationShaderPosition position,
        ManagedRoomWindEnvelope envelope,
        int stateIndex,
        RoomWindTargetKind target,
        double padding
    )
    {
        int minX = RoomWindCellCoordinate.QuantizeWorldCoordinate(
            position.X + envelope.MinX - padding
        );
        int minY = RoomWindCellCoordinate.QuantizeWorldCoordinate(
            position.Y + envelope.MinY - padding
        );
        int minZ = RoomWindCellCoordinate.QuantizeWorldCoordinate(
            position.Z + envelope.MinZ - padding
        );
        int maxX = RoomWindCellCoordinate.QuantizeWorldCoordinate(
            position.X + envelope.MaxX + padding
        );
        int maxY = RoomWindCellCoordinate.QuantizeWorldCoordinate(
            position.Y + envelope.MaxY + padding
        );
        int maxZ = RoomWindCellCoordinate.QuantizeWorldCoordinate(
            position.Z + envelope.MaxZ + padding
        );

        long cellCount = (long)(maxX - minX + 1)
            * (maxY - minY + 1)
            * (maxZ - minZ + 1);

        if (cellCount <= 0 || cellCount > 1_000_000)
        {
            throw new InvalidOperationException(
                $"Invalid room-wind envelope cell count {cellCount}."
            );
        }

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    RoomWindCellCoordinate coordinate = new(x, y, z);
                    contributions.Add(
                        target == RoomWindTargetKind.Water
                            ? RoomWindCellContribution.ForWater(
                                coordinate,
                                stateIndex
                            )
                            : RoomWindCellContribution.ForVegetation(
                                coordinate,
                                stateIndex
                            )
                    );
                }
            }
        }
    }

    private void UploadCellHashTexture(
        RoomWindCellHashSnapshot snapshot
    )
    {
        if (
            cellHashTexture != null
            && (
                cellHashTexture.Width != snapshot.Width
                || cellHashTexture.Height != snapshot.Height
            )
        )
        {
            DisposeCellHashTexture();
        }

        LoadedTexture texture = cellHashTexture
            ?? new LoadedTexture(api);
        texture.Width = snapshot.Width;
        texture.Height = snapshot.Height;

        api.Render.LoadOrUpdateTextureFromBgra(
            snapshot.Pixels,
            linearMag: false,
            clampMode: 0,
            ref texture
        );

        cellHashTexture = texture;
    }

    private void DisposeCellHashTexture()
    {
        if (cellHashTexture == null)
        {
            return;
        }

        try
        {
            cellHashTexture.Dispose();
        }
        catch
        {
            // Render shutdown may already have released the OpenGL context.
        }

        cellHashTexture = null;
    }

    private void UpdateRenderReference()
    {
        Vec3d? reference = api.Render.ShaderUniforms.playerReferencePos;
        double x = reference?.X ?? 0d;
        double y = reference?.Y ?? 0d;
        double z = reference?.Z ?? 0d;

        SplitReferenceQuarter(
            x,
            out int quarterX,
            out float fractionX
        );
        SplitReferenceQuarter(
            y,
            out int quarterY,
            out float fractionY
        );
        SplitReferenceQuarter(
            z,
            out int quarterZ,
            out float fractionZ
        );

        if (
            quarterX == renderReferenceQuarterX
            && quarterY == renderReferenceQuarterY
            && quarterZ == renderReferenceQuarterZ
            && fractionX == renderReferenceQuarterFractionX
            && fractionY == renderReferenceQuarterFractionY
            && fractionZ == renderReferenceQuarterFractionZ
        )
        {
            return;
        }

        renderReferenceQuarterX = quarterX;
        renderReferenceQuarterY = quarterY;
        renderReferenceQuarterZ = quarterZ;
        renderReferenceQuarterFractionX = fractionX;
        renderReferenceQuarterFractionY = fractionY;
        renderReferenceQuarterFractionZ = fractionZ;
        renderReferenceRevision++;
    }

    private static void SplitReferenceQuarter(
        double coordinate,
        out int wholeQuarterCells,
        out float fractionalQuarterCell
    )
    {
        double scaled = coordinate
            * RoomWindCellCoordinate.CellsPerBlock;
        double whole = Math.Floor(scaled);

        if (whole < int.MinValue || whole > int.MaxValue)
        {
            throw new InvalidOperationException(
                "The render reference exceeds the room-wind hash range."
            );
        }

        wholeQuarterCells = (int)whole;
        fractionalQuarterCell = (float)(scaled - whole);
    }

    // Retained for the 0.18.0 rollback window. The spatial-hash path above no
    // longer invokes distance selection or the legacy 128/512 uniform budget.
    private void RefreshLegacyTopologySnapshot()
    {
        long startTimestamp =
            Stopwatch.GetTimestamp();

        int currentRegistrationRevision =
            RegistrationRevision;

        int positionBudget =
            ConfiguredPositionBudget;

        IClientPlayer? player =
            api.World.Player;

        if (player?.Entity == null)
        {
            bool refreshRequired =
                !topologyRefreshInitialized
                || currentRegistrationRevision
                    != lastObservedRegistrationRevision
                || positionBudget
                    != lastObservedPositionBudget
                || topology.PositionCount > 0;

            if (!refreshRequired)
            {
                Interlocked.Increment(
                    ref topologyRefreshSkips
                );

                return;
            }

            SetTopologySnapshot(
                Array.Empty<PositionCandidate>(),
                totalCompactedEnvelopes: 0,
                totalValidPositions: 0,
                referenceX: 0d,
                referenceY: 0d,
                referenceZ: 0d,
                shaderPlayerX: 0f,
                shaderPlayerY: 0f,
                shaderPlayerZ: 0f,
                referenceAvailable: false
            );

            topologyRefreshInitialized = true;
            lastObservedRegistrationRevision =
                currentRegistrationRevision;
            lastObservedPositionBudget =
                positionBudget;
            lastRenderReferenceAvailable = false;
            lastSelectionPlayerX = double.NaN;
            lastSelectionPlayerY = double.NaN;
            lastSelectionPlayerZ = double.NaN;
            lastSelectionPlayerDimension = int.MinValue;

            RecordTopologyRefreshPerformance(
                startTimestamp,
                candidatesEvaluated: 0,
                reason: "no-player"
            );

            return;
        }

        double playerX =
            player.Entity.Pos.X;

        double playerY =
            player.Entity.Pos.Y;

        double playerZ =
            player.Entity.Pos.Z;

        DefaultShaderUniforms shaderUniforms =
            api.Render.ShaderUniforms;

        Vec3d? renderReference =
            shaderUniforms.playerReferencePos;

        Vec3f? shaderPlayerPos =
            shaderUniforms.PlayerPos;

        double referenceX =
            renderReference?.X ?? 0d;

        double referenceY =
            renderReference?.Y ?? 0d;

        double referenceZ =
            renderReference?.Z ?? 0d;

        float shaderPlayerX =
            shaderPlayerPos?.X ?? 0f;

        float shaderPlayerY =
            shaderPlayerPos?.Y ?? 0f;

        float shaderPlayerZ =
            shaderPlayerPos?.Z ?? 0f;

        bool referenceAvailable =
            renderReference != null;

        bool wasInitialized =
            topologyRefreshInitialized;

        bool registrationChanged =
            currentRegistrationRevision
                != lastObservedRegistrationRevision;

        bool budgetChanged =
            positionBudget
                != lastObservedPositionBudget;

        bool referenceChanged =
            !topologyRefreshInitialized
            || referenceAvailable
                != lastRenderReferenceAvailable
            || (
                referenceAvailable
                && (
                    referenceX != lastRenderReferenceX
                    || referenceY != lastRenderReferenceY
                    || referenceZ != lastRenderReferenceZ
                )
            );

        int playerDimension =
            player.Entity.Pos.Dimension;

        bool dimensionChanged =
            playerDimension
                != lastSelectionPlayerDimension;

        bool selectionDependsOnPlayer =
            topology.TotalCompactedEnvelopes
                > positionBudget;

        double selectionMovementSquared =
            double.IsNaN(lastSelectionPlayerX)
                ? double.MaxValue
                : (
                    playerX - lastSelectionPlayerX
                ) * (
                    playerX - lastSelectionPlayerX
                )
                + (
                    playerY - lastSelectionPlayerY
                ) * (
                    playerY - lastSelectionPlayerY
                )
                + (
                    playerZ - lastSelectionPlayerZ
                ) * (
                    playerZ - lastSelectionPlayerZ
                );

        bool playerMovedEnough =
            selectionDependsOnPlayer
            && selectionMovementSquared
                >= TopologyMovementThresholdSquared;

        if (
            topologyRefreshInitialized
            && !registrationChanged
            && !budgetChanged
            && !referenceChanged
            && !dimensionChanged
            && !playerMovedEnough
        )
        {
            Interlocked.Increment(
                ref topologyRefreshSkips
            );

            return;
        }

        IReadOnlyList<CompactedPositionEnvelope>
            compacted = GetCompactedRegistrations(
                out int representedRegistrationRevision
            );

        List<PositionCandidate> candidates = new(
            Math.Min(
                compacted.Count,
                positionBudget * 4
            )
        );

        int totalValidPositions = 0;
        int totalCompactedEnvelopes = 0;
        int candidatesEvaluated = 0;

        foreach (
            CompactedPositionEnvelope entry
            in compacted
        )
        {
            ManagedVegetationShaderPosition registered =
                entry.Position;

            if (
                registered.Dimension
                    != playerDimension
            )
            {
                continue;
            }

            RoomWindTargetKind targets =
                entry.Targets;

            if (targets == RoomWindTargetKind.None)
            {
                continue;
            }

            totalValidPositions +=
                entry.CoveredPositionCount;

            totalCompactedEnvelopes++;
            candidatesEvaluated++;

            candidates.Add(
                new PositionCandidate(
                    registered,
                    entry.RoomType,
                    targets,
                    entry.Envelope,
                    entry.CoveredPositionCount,
                    CalculateEncodedEnvelopeDistanceSquared(
                        registered,
                        entry.Envelope,
                        playerX,
                        playerY,
                        playerZ
                    )
                )
            );
        }

        SetTopologySnapshot(
            candidates,
            totalCompactedEnvelopes,
            totalValidPositions,
            referenceX,
            referenceY,
            referenceZ,
            shaderPlayerX,
            shaderPlayerY,
            shaderPlayerZ,
            referenceAvailable
        );

        topologyRefreshInitialized = true;
        lastObservedRegistrationRevision =
            representedRegistrationRevision;
        lastObservedPositionBudget =
            positionBudget;
        lastSelectionPlayerX =
            playerX;
        lastSelectionPlayerY =
            playerY;
        lastSelectionPlayerZ =
            playerZ;
        lastSelectionPlayerDimension =
            playerDimension;
        lastRenderReferenceAvailable =
            referenceAvailable;
        lastRenderReferenceX =
            referenceX;
        lastRenderReferenceY =
            referenceY;
        lastRenderReferenceZ =
            referenceZ;

        string reason =
            !wasInitialized
                ? "initial"
                : registrationChanged
                    ? "registration-revision"
                    : budgetChanged
                        ? "position-budget"
                        : referenceChanged
                            ? "render-reference"
                            : dimensionChanged
                                ? "player-dimension"
                                : playerMovedEnough
                                    ? "nearest-selection-movement"
                                    : "forced";

        RecordTopologyRefreshPerformance(
            startTimestamp,
            candidatesEvaluated,
            reason
        );
    }

    private IReadOnlyList<CompactedPositionEnvelope>
        GetCompactedRegistrations(
            out int representedRegistrationRevision
        )
    {
        KeyValuePair<
            ManagedVegetationShaderPosition,
            ManagedRoomWindRegistration
        >[] snapshot;

        lock (RegistrationSync)
        {
            representedRegistrationRevision =
                RegistrationRevision;

            if (
                compactedRegistrationRevision
                    == representedRegistrationRevision
            )
            {
                return compactedRegistrations;
            }

            snapshot = RegisteredPositions.ToArray();
        }

        List<CompactedPositionEnvelope> compacted;

        try
        {
            compacted = BuildCompactedRegistrations(
                snapshot
            );
        }
        catch (Exception e)
        {
            // Compaction is an optimization. If an unexpected registration
            // shape defeats an invariant, preserve correctness by reverting
            // this revision to the original one-envelope-per-position form.
            compacted = BuildSingletonRegistrations(
                snapshot
            );

            if (
                Interlocked.Exchange(
                    ref compactionFailureWarningLogged,
                    1
                ) == 0
            )
            {
                StillGreenhousesClientSystem.WarningLiteral(
                    "[StillGreenhouses] ROOM WIND COMPACTION FALLBACK " +
                    $"error={e.GetType().Name}:{e.Message}. " +
                    "Raw position envelopes will be used for this world."
                );
            }
        }

        compactedRegistrations = compacted;
        compactedRegistrationRevision =
            representedRegistrationRevision;

        return compactedRegistrations;
    }

    internal static List<CompactedPositionEnvelope>
        BuildCompactedRegistrations(
            IEnumerable<KeyValuePair<
                ManagedVegetationShaderPosition,
                ManagedRoomWindRegistration
            >> registrations
        )
    {
        List<KeyValuePair<
            ManagedVegetationShaderPosition,
            ManagedRoomWindRegistration
        >> source = registrations
            .Where(entry =>
                entry.Value.Targets
                    != RoomWindTargetKind.None
            )
            .ToList();

        List<CompactedPositionEnvelope> result = new(
            source.Count
        );

        Dictionary<
            CompactionRunKey,
            List<ManagedVegetationShaderPosition>
        > runGroups = new();

        foreach (
            KeyValuePair<
                ManagedVegetationShaderPosition,
                ManagedRoomWindRegistration
            > entry
            in source
        )
        {
            ManagedRoomWindEnvelope envelope =
                entry.Value.GetEnvelope(
                    entry.Value.Targets
                );

            if (!CanJoinXRun(
                    entry.Key,
                    entry.Value,
                    envelope
                ))
            {
                result.Add(
                    CreateSingletonEnvelope(
                        entry.Key,
                        entry.Value,
                        envelope
                    )
                );

                continue;
            }

            CompactionRunKey runKey = new(
                GetRegistrationChunk(entry.Key),
                entry.Key.Y,
                entry.Key.Z,
                entry.Value.RoomKey,
                entry.Value.RoomType,
                entry.Value.Targets,
                envelope
            );

            if (!runGroups.TryGetValue(
                    runKey,
                    out List<
                        ManagedVegetationShaderPosition
                    >? positions
                ))
            {
                positions = new List<
                    ManagedVegetationShaderPosition
                >();

                runGroups.Add(
                    runKey,
                    positions
                );
            }

            positions.Add(entry.Key);
        }

        foreach (
            KeyValuePair<
                CompactionRunKey,
                List<ManagedVegetationShaderPosition>
            > group
            in runGroups
        )
        {
            List<ManagedVegetationShaderPosition> positions =
                group.Value;

            positions.Sort(
                (left, right) =>
                    left.X.CompareTo(right.X)
            );

            ManagedVegetationShaderPosition runAnchor =
                positions[0];

            ManagedVegetationShaderPosition previous =
                runAnchor;

            ManagedRoomWindEnvelope runEnvelope =
                group.Key.Envelope;

            int coveredPositionCount = 1;

            for (int i = 1; i < positions.Count; i++)
            {
                ManagedVegetationShaderPosition next =
                    positions[i];

                long xDifference =
                    (long)next.X - previous.X;

                ManagedRoomWindEnvelope translated =
                    group.Key.Envelope.Translate(
                        next.X - runAnchor.X,
                        0f,
                        0f
                    );

                ManagedRoomWindEnvelope proposedEnvelope =
                    runEnvelope.Union(translated);

                bool canExtend =
                    xDifference == 1L
                    && EncodedXEnvelopesTouch(
                        previous,
                        group.Key.Envelope,
                        next,
                        group.Key.Envelope
                    )
                    && CanPackEnvelope(proposedEnvelope);

                if (!canExtend)
                {
                    result.Add(
                        CreateCompactedEnvelope(
                            runAnchor,
                            group.Key,
                            runEnvelope,
                            coveredPositionCount
                        )
                    );

                    runAnchor = next;
                    runEnvelope = group.Key.Envelope;
                    coveredPositionCount = 1;
                }
                else
                {
                    runEnvelope = proposedEnvelope;
                    coveredPositionCount++;
                }

                previous = next;
            }

            result.Add(
                CreateCompactedEnvelope(
                    runAnchor,
                    group.Key,
                    runEnvelope,
                    coveredPositionCount
                )
            );
        }

        result = DecompactIncompatibleOverlaps(
            result,
            source
        );

        result.Sort(CompareCompactedEnvelopes);

        int covered = result.Sum(
            entry => entry.CoveredPositionCount
        );

        if (
            covered != source.Count
            || result.Any(entry =>
                entry.CoveredPositionCount < 1
                || (
                    entry.CoveredPositionCount > 1
                    && !CanPackEnvelope(entry.Envelope)
                )
            )
        )
        {
            throw new InvalidOperationException(
                "Spatial envelope compaction failed its coverage invariant."
            );
        }

        return result;
    }

    private static List<CompactedPositionEnvelope>
        DecompactIncompatibleOverlaps(
            List<CompactedPositionEnvelope> compacted,
            IReadOnlyList<KeyValuePair<
                ManagedVegetationShaderPosition,
                ManagedRoomWindRegistration
            >> source
        )
    {
        if (!compacted.Any(entry =>
                entry.CoveredPositionCount > 1
            ))
        {
            return compacted;
        }

        EncodedEnvelopeBounds[] bounds =
            compacted
                .Select(GetEncodedEnvelopeBounds)
                .ToArray();

        Dictionary<
            EncodedEnvelopeBucketKey,
            List<int>
        > spatialBuckets = new();

        List<int> unbucketed = new();

        for (int index = 0; index < compacted.Count; index++)
        {
            if (!TryGetEnvelopeBucketRange(
                    bounds[index],
                    out EnvelopeBucketRange range
                ))
            {
                unbucketed.Add(index);
                continue;
            }

            for (int x = range.MinX; x <= range.MaxX; x++)
            {
                for (int y = range.MinY; y <= range.MaxY; y++)
                {
                    for (int z = range.MinZ; z <= range.MaxZ; z++)
                    {
                        EncodedEnvelopeBucketKey key = new(
                            x,
                            y,
                            z,
                            compacted[index]
                                .Position.Dimension
                        );

                        if (!spatialBuckets.TryGetValue(
                                key,
                                out List<int>? indices
                            ))
                        {
                            indices = new List<int>();
                            spatialBuckets.Add(key, indices);
                        }

                        indices.Add(index);
                    }
                }
            }
        }

        HashSet<int> runsToExpand = new();

        int[] inspectedAtStamp =
            new int[compacted.Count];

        for (int i = 0; i < compacted.Count; i++)
        {
            CompactedPositionEnvelope candidate =
                compacted[i];

            if (candidate.CoveredPositionCount <= 1)
            {
                continue;
            }

            if (!TryGetEnvelopeBucketRange(
                    bounds[i],
                    out EnvelopeBucketRange candidateRange
                ))
            {
                // Merged runs are always finite and packable, so reaching
                // this path means an internal invariant was violated.
                throw new InvalidOperationException(
                    "A compacted run could not be assigned to a spatial bucket."
                );
            }

            int inspectionStamp = i + 1;
            bool conflictFound = false;

            for (
                int x = candidateRange.MinX;
                x <= candidateRange.MaxX && !conflictFound;
                x++
            )
            {
                for (
                    int y = candidateRange.MinY;
                    y <= candidateRange.MaxY && !conflictFound;
                    y++
                )
                {
                    for (
                        int z = candidateRange.MinZ;
                        z <= candidateRange.MaxZ && !conflictFound;
                        z++
                    )
                    {
                        EncodedEnvelopeBucketKey key = new(
                            x,
                            y,
                            z,
                            candidate.Position.Dimension
                        );

                        if (!spatialBuckets.TryGetValue(
                                key,
                                out List<int>? indices
                            ))
                        {
                            continue;
                        }

                        foreach (int j in indices)
                        {
                            if (
                                inspectedAtStamp[j]
                                    == inspectionStamp
                            )
                            {
                                continue;
                            }

                            inspectedAtStamp[j] =
                                inspectionStamp;

                            if (HasIncompatibleOverlap(
                                    i,
                                    j,
                                    compacted,
                                    bounds
                                ))
                            {
                                conflictFound = true;
                                break;
                            }
                        }
                    }
                }
            }

            if (!conflictFound)
            {
                foreach (int j in unbucketed)
                {
                    if (
                        inspectedAtStamp[j]
                            != inspectionStamp
                        && HasIncompatibleOverlap(
                            i,
                            j,
                            compacted,
                            bounds
                        )
                    )
                    {
                        conflictFound = true;
                        break;
                    }
                }
            }

            if (conflictFound)
            {
                runsToExpand.Add(i);
            }
        }

        if (runsToExpand.Count == 0)
        {
            return compacted;
        }

        Dictionary<
            ManagedVegetationShaderPosition,
            ManagedRoomWindRegistration
        > registrationsByPosition = new();

        foreach (
            KeyValuePair<
                ManagedVegetationShaderPosition,
                ManagedRoomWindRegistration
            > entry
            in source
        )
        {
            registrationsByPosition[entry.Key] =
                entry.Value;
        }

        List<CompactedPositionEnvelope> expanded = new(
            compacted.Count
            + runsToExpand.Sum(index =>
                compacted[index].CoveredPositionCount - 1
            )
        );

        for (int i = 0; i < compacted.Count; i++)
        {
            CompactedPositionEnvelope entry =
                compacted[i];

            if (!runsToExpand.Contains(i))
            {
                expanded.Add(entry);
                continue;
            }

            for (
                int offset = 0;
                offset < entry.CoveredPositionCount;
                offset++
            )
            {
                ManagedVegetationShaderPosition position =
                    new(
                        entry.Position.X + offset,
                        entry.Position.Y,
                        entry.Position.Z,
                        entry.Position.Dimension
                    );

                if (!registrationsByPosition.TryGetValue(
                        position,
                        out ManagedRoomWindRegistration registration
                    ))
                {
                    throw new InvalidOperationException(
                        "A compacted run could not be expanded to its source registrations."
                    );
                }

                expanded.Add(
                    CreateSingletonEnvelope(
                        position,
                        registration,
                        registration.GetEnvelope(
                            registration.Targets
                        )
                    )
                );
            }
        }

        return expanded;
    }

    private static bool HasIncompatibleOverlap(
        int candidateIndex,
        int otherIndex,
        IReadOnlyList<CompactedPositionEnvelope> compacted,
        IReadOnlyList<EncodedEnvelopeBounds> bounds
    )
    {
        if (candidateIndex == otherIndex)
        {
            return false;
        }

        CompactedPositionEnvelope candidate =
            compacted[candidateIndex];

        CompactedPositionEnvelope other =
            compacted[otherIndex];

        // Preserve the original per-position overlap arbitration across room
        // identities. Room state is currently shared by type, but treating a
        // different exact room as incompatible keeps this optimization safe if
        // room-local state is introduced later.
        return candidate.Position.Dimension
                   == other.Position.Dimension
               && candidate.RoomKey
                   != other.RoomKey
               && (
                   candidate.Targets
                   & other.Targets
               ) != RoomWindTargetKind.None
               && bounds[candidateIndex].Overlaps(
                   bounds[otherIndex]
               );
    }

    private static bool TryGetEnvelopeBucketRange(
        EncodedEnvelopeBounds bounds,
        out EnvelopeBucketRange range
    )
    {
        if (
            !double.IsFinite(bounds.MinX)
            || !double.IsFinite(bounds.MinY)
            || !double.IsFinite(bounds.MinZ)
            || !double.IsFinite(bounds.MaxX)
            || !double.IsFinite(bounds.MaxY)
            || !double.IsFinite(bounds.MaxZ)
        )
        {
            range = default;
            return false;
        }

        range = new EnvelopeBucketRange(
            GetEnvelopeBucketCoordinate(bounds.MinX),
            GetEnvelopeBucketCoordinate(bounds.MinY),
            GetEnvelopeBucketCoordinate(bounds.MinZ),
            GetEnvelopeBucketCoordinate(bounds.MaxX),
            GetEnvelopeBucketCoordinate(bounds.MaxY),
            GetEnvelopeBucketCoordinate(bounds.MaxZ)
        );

        return true;
    }

    private static int GetEnvelopeBucketCoordinate(
        double coordinate
    ) =>
        (int)Math.Floor(
            coordinate
            / CompactionOverlapBucketSizeBlocks
        );

    private static EncodedEnvelopeBounds
        GetEncodedEnvelopeBounds(
            CompactedPositionEnvelope entry
        )
    {
        double centerX =
            entry.Position.X
            + entry.Envelope.CenterX;

        double centerY =
            entry.Position.Y
            + entry.Envelope.CenterY;

        double centerZ =
            entry.Position.Z
            + entry.Envelope.CenterZ;

        double halfExtentX =
            QuantizeEnvelopeHalfExtent(
                entry.Envelope.HalfExtentX
            )
            * EnvelopeExtentQuantization;

        double halfExtentY =
            QuantizeEnvelopeHalfExtent(
                entry.Envelope.HalfExtentY
            )
            * EnvelopeExtentQuantization;

        double halfExtentZ =
            QuantizeEnvelopeHalfExtent(
                entry.Envelope.HalfExtentZ
            )
            * EnvelopeExtentQuantization;

        return new EncodedEnvelopeBounds(
            centerX - halfExtentX,
            centerY - halfExtentY,
            centerZ - halfExtentZ,
            centerX + halfExtentX,
            centerY + halfExtentY,
            centerZ + halfExtentZ
        );
    }

    internal static List<CompactedPositionEnvelope>
        BuildSingletonRegistrations(
            IEnumerable<KeyValuePair<
                ManagedVegetationShaderPosition,
                ManagedRoomWindRegistration
            >> registrations
        )
    {
        List<CompactedPositionEnvelope> result =
            registrations
                .Where(entry =>
                    entry.Value.Targets
                        != RoomWindTargetKind.None
                )
                .Select(entry =>
                    CreateSingletonEnvelope(
                        entry.Key,
                        entry.Value,
                        entry.Value.GetEnvelope(
                            entry.Value.Targets
                        )
                    )
                )
                .ToList();

        result.Sort(CompareCompactedEnvelopes);

        return result;
    }

    private static CompactedPositionEnvelope
        CreateSingletonEnvelope(
            ManagedVegetationShaderPosition position,
            ManagedRoomWindRegistration registration,
            ManagedRoomWindEnvelope envelope
        ) =>
            new(
                position,
                registration.RoomKey,
                registration.RoomType,
                registration.Targets,
                envelope,
                1
            );

    private static CompactedPositionEnvelope
        CreateCompactedEnvelope(
            ManagedVegetationShaderPosition position,
            CompactionRunKey runKey,
            ManagedRoomWindEnvelope envelope,
            int coveredPositionCount
        ) =>
            new(
                position,
                runKey.RoomKey,
                runKey.RoomType,
                runKey.Targets,
                envelope,
                coveredPositionCount
            );

    private static ChunkKey GetRegistrationChunk(
        ManagedVegetationShaderPosition position
    ) =>
        new(
            StillGreenhousesShared.FloorDiv(
                position.X,
                StillGreenhousesShared.ChunkSize
            ),
            StillGreenhousesShared.FloorDiv(
                position.Y,
                StillGreenhousesShared.ChunkSize
            ),
            StillGreenhousesShared.FloorDiv(
                position.Z,
                StillGreenhousesShared.ChunkSize
            ),
            position.Dimension
        );

    private static bool CanJoinXRun(
        ManagedVegetationShaderPosition position,
        ManagedRoomWindRegistration registration,
        ManagedRoomWindEnvelope envelope
    ) =>
        registration.AllowXRunCompaction
        && !registration.AllowRoomAbove
        && (
            registration.Targets
                is RoomWindTargetKind.Vegetation
                or RoomWindTargetKind.Water
        )
        && registration.RoomKey.Dimension
            == position.Dimension
        && registration.RoomKey.RoomType
            == registration.RoomType
        && IsFiniteOrderedEnvelope(envelope)
        && IsContainedInUnitBlock(envelope)
        && CanPackEnvelope(envelope);

    private static bool IsFiniteOrderedEnvelope(
        ManagedRoomWindEnvelope envelope
    ) =>
        float.IsFinite(envelope.MinX)
        && float.IsFinite(envelope.MinY)
        && float.IsFinite(envelope.MinZ)
        && float.IsFinite(envelope.MaxX)
        && float.IsFinite(envelope.MaxY)
        && float.IsFinite(envelope.MaxZ)
        && envelope.MinX <= envelope.MaxX
        && envelope.MinY <= envelope.MaxY
        && envelope.MinZ <= envelope.MaxZ;

    private static bool IsContainedInUnitBlock(
        ManagedRoomWindEnvelope envelope
    ) =>
        envelope.MinX >= 0f
        && envelope.MinY >= 0f
        && envelope.MinZ >= 0f
        && envelope.MaxX <= 1f
        && envelope.MaxY <= 1f
        && envelope.MaxZ <= 1f;

    private static bool EncodedXEnvelopesTouch(
        ManagedVegetationShaderPosition leftPosition,
        ManagedRoomWindEnvelope leftEnvelope,
        ManagedVegetationShaderPosition rightPosition,
        ManagedRoomWindEnvelope rightEnvelope
    )
    {
        double leftCenter =
            leftPosition.X
            + leftEnvelope.CenterX;

        double leftHalfExtent =
            QuantizeEnvelopeHalfExtent(
                leftEnvelope.HalfExtentX
            )
            * EnvelopeExtentQuantization;

        double rightCenter =
            rightPosition.X
            + rightEnvelope.CenterX;

        double rightHalfExtent =
            QuantizeEnvelopeHalfExtent(
                rightEnvelope.HalfExtentX
            )
            * EnvelopeExtentQuantization;

        return leftCenter + leftHalfExtent
            >= rightCenter - rightHalfExtent;
    }

    private static int CompareCompactedEnvelopes(
        CompactedPositionEnvelope left,
        CompactedPositionEnvelope right
    )
    {
        int comparison =
            left.Position.Dimension.CompareTo(
                right.Position.Dimension
            );

        if (comparison != 0) return comparison;

        comparison = left.Position.X.CompareTo(
            right.Position.X
        );

        if (comparison != 0) return comparison;

        comparison = left.Position.Y.CompareTo(
            right.Position.Y
        );

        return comparison != 0
            ? comparison
            : left.Position.Z.CompareTo(
                right.Position.Z
            );
    }

    private static void RecordTopologyRefreshPerformance(
        long startTimestamp,
        int candidatesEvaluated,
        string reason
    )
    {
        long elapsedMicroseconds =
            GetElapsedMicroseconds(
                startTimestamp
            );

        Interlocked.Increment(
            ref topologyRefreshRuns
        );

        Interlocked.Add(
            ref topologyCandidatesEvaluated,
            candidatesEvaluated
        );

        Interlocked.Exchange(
            ref lastTopologyRefreshMicroseconds,
            elapsedMicroseconds
        );

        UpdateMaximum(
            ref maxTopologyRefreshMicroseconds,
            elapsedMicroseconds
        );

        if (
            StillGreenhousesClientSystem.Config
                ?.DebugLogging == true
            && elapsedMicroseconds >= 2000
        )
        {
            StillGreenhousesClientSystem.DebugLiteral(
                "[StillGreenhouses] CLIENT PERF " +
                "operation=render-topology-refresh; " +
                $"elapsedMs={elapsedMicroseconds / 1000d:F3}; " +
                $"reason={reason}; " +
                $"registered={RegisteredPositions.Count}; " +
                $"candidatesEvaluated={candidatesEvaluated}; " +
                $"selected={UploadedPositionCount}/{ConfiguredPositionBudget}; " +
                "thread=render"
            );
        }
    }

    private static long GetElapsedMicroseconds(
        long startTimestamp
    ) =>
        (
            Stopwatch.GetTimestamp()
            - startTimestamp
        )
        * 1_000_000L
        / Stopwatch.Frequency;

    private static void UpdateMaximum(
        ref long target,
        long value
    )
    {
        long observed =
            Interlocked.Read(
                ref target
            );

        while (
            value > observed
            && Interlocked.CompareExchange(
                ref target,
                value,
                observed
            ) != observed
        )
        {
            observed =
                Interlocked.Read(
                    ref target
                );
        }
    }

    private static int QuantizeEnvelopeHalfExtent(
        float halfExtent
    )
    {
        float paddedExtent =
            Math.Max(
                0f,
                halfExtent
            )
            + EnvelopeMeasurementPadding;

        int quantized =
            (int)Math.Ceiling(
                paddedExtent
                / EnvelopeExtentQuantization
            );

        return Math.Clamp(
            quantized,
            1,
            EnvelopeExtentMask
        );
    }

    internal static bool CanPackEnvelope(
        ManagedRoomWindEnvelope envelope
    ) =>
        IsFiniteOrderedEnvelope(envelope)
        && CanPackEnvelopeHalfExtent(
            envelope.HalfExtentX
        )
        && CanPackEnvelopeHalfExtent(
            envelope.HalfExtentY
        )
        && CanPackEnvelopeHalfExtent(
            envelope.HalfExtentZ
        );

    private static bool CanPackEnvelopeHalfExtent(
        float halfExtent
    )
    {
        if (!float.IsFinite(halfExtent))
        {
            return false;
        }

        float paddedExtent =
            Math.Max(
                0f,
                halfExtent
            )
            + EnvelopeMeasurementPadding;

        int quantized =
            (int)Math.Ceiling(
                paddedExtent
                / EnvelopeExtentQuantization
            );

        return quantized >= 1
            && quantized <= EnvelopeExtentMask;
    }

    internal static double
        CalculateEncodedEnvelopeDistanceSquared(
            ManagedVegetationShaderPosition position,
            ManagedRoomWindEnvelope envelope,
            double pointX,
            double pointY,
            double pointZ
        )
    {
        double centerX =
            position.X + envelope.CenterX;

        double centerY =
            position.Y + envelope.CenterY;

        double centerZ =
            position.Z + envelope.CenterZ;

        double halfExtentX =
            QuantizeEnvelopeHalfExtent(
                envelope.HalfExtentX
            )
            * EnvelopeExtentQuantization;

        double halfExtentY =
            QuantizeEnvelopeHalfExtent(
                envelope.HalfExtentY
            )
            * EnvelopeExtentQuantization;

        double halfExtentZ =
            QuantizeEnvelopeHalfExtent(
                envelope.HalfExtentZ
            )
            * EnvelopeExtentQuantization;

        double dx = DistanceToInterval(
            pointX,
            centerX - halfExtentX,
            centerX + halfExtentX
        );

        double dy = DistanceToInterval(
            pointY,
            centerY - halfExtentY,
            centerY + halfExtentY
        );

        double dz = DistanceToInterval(
            pointZ,
            centerZ - halfExtentZ,
            centerZ + halfExtentZ
        );

        return dx * dx
            + dy * dy
            + dz * dz;
    }

    private static double DistanceToInterval(
        double value,
        double minimum,
        double maximum
    ) =>
        value < minimum
            ? minimum - value
            : value > maximum
                ? value - maximum
                : 0d;

    private static int PackTargetAndEnvelope(
        int packedTarget,
        ManagedRoomWindEnvelope envelope
    )
    {
        int extentX = QuantizeEnvelopeHalfExtent(
            envelope.HalfExtentX
        );

        int extentY = QuantizeEnvelopeHalfExtent(
            envelope.HalfExtentY
        );

        int extentZ = QuantizeEnvelopeHalfExtent(
            envelope.HalfExtentZ
        );

        return
            (packedTarget & PackedTargetMask)
            | (
                extentX
                << PackedTargetBits
            )
            | (
                extentY
                << (
                    PackedTargetBits
                    + EnvelopeExtentBits
                )
            )
            | (
                extentZ
                << (
                    PackedTargetBits
                    + EnvelopeExtentBits * 2
                )
            );
    }

    private void SetTopologySnapshot(
        IReadOnlyList<PositionCandidate> candidates,
        int totalCompactedEnvelopes,
        int totalValidPositions,
        double referenceX,
        double referenceY,
        double referenceZ,
        float shaderPlayerX,
        float shaderPlayerY,
        float shaderPlayerZ,
        bool referenceAvailable
    )
    {
        List<PositionCandidate> selectedCandidates =
            referenceAvailable
                ? SelectBudgetedPositions(candidates)
                : new List<PositionCandidate>();

        // Distance controls selection when the GPU budget is full, but it
        // must not control uniform-array order. Canonical ordering prevents
        // needless topology uploads when the same entries merely exchange
        // nearest-distance order as the player moves.
        selectedCandidates.Sort(
            (left, right) =>
            {
                int comparison =
                    left.Position.Dimension.CompareTo(
                        right.Position.Dimension
                    );

                if (comparison != 0) return comparison;

                comparison = left.Position.X.CompareTo(
                    right.Position.X
                );

                if (comparison != 0) return comparison;

                comparison = left.Position.Y.CompareTo(
                    right.Position.Y
                );

                return comparison != 0
                    ? comparison
                    : left.Position.Z.CompareTo(
                        right.Position.Z
                    );
            }
        );

        int selectedCount =
            selectedCandidates.Count;

        int selectedCoveredPositionCount =
            selectedCandidates.Sum(
                candidate =>
                    candidate.CoveredPositionCount
            );

        float[] positionValues =
            new float[
                selectedCount * 4
            ];

        ulong hash =
            14695981039346656037UL;

        int greenhousePositionCount = 0;
        int cellarPositionCount = 0;
        int roomPositionCount = 0;

        for (
            int i = 0;
            i < selectedCount;
            i++
        )
        {
            PositionCandidate candidate =
                selectedCandidates[i];

            int stateIndex =
                StillGreenhousesRoomWindEnvironment
                    .GetStateIndex(
                        candidate.RoomType
                    );

            switch (candidate.RoomType)
            {
                case ManagedRoomType.Greenhouse:
                    greenhousePositionCount++;
                    break;

                case ManagedRoomType.Cellar:
                    cellarPositionCount++;
                    break;

                default:
                    roomPositionCount++;
                    break;
            }

            int offset =
                i * 4;

            float relativeX =
                (float)(
                    candidate.Position.X
                    + candidate.Envelope.CenterX
                    - referenceX
                );

            float relativeY =
                (float)(
                    candidate.Position.Y
                    + candidate.Envelope.CenterY
                    - referenceY
                );

            float relativeZ =
                (float)(
                    candidate.Position.Z
                    + candidate.Envelope.CenterZ
                    - referenceZ
                );

            positionValues[offset] =
                relativeX;

            positionValues[offset + 1] =
                relativeY;

            positionValues[offset + 2] =
                relativeZ;

            int packedTarget =
                stateIndex
                + (
                    (int)candidate.Targets
                    * StillGreenhousesRoomWindEnvironment
                        .RoomTypeStateCount
                );

            int packedMetadata =
                PackTargetAndEnvelope(
                    packedTarget,
                    candidate.Envelope
                );

            positionValues[offset + 3] =
                packedMetadata;

            hash = AddHash(
                hash,
                BitConverter.SingleToInt32Bits(
                    relativeX
                )
            );

            hash = AddHash(
                hash,
                BitConverter.SingleToInt32Bits(
                    relativeY
                )
            );

            hash = AddHash(
                hash,
                BitConverter.SingleToInt32Bits(
                    relativeZ
                )
            );

            hash = AddHash(
                hash,
                packedMetadata
            );
        }

        hash = AddHash(
            hash,
            referenceAvailable ? 1 : 0
        );

        hash = AddHash(
            hash,
            selectedCount
        );

        if (
            hash == topology.ContentHash
            && totalCompactedEnvelopes
                == topology.TotalCompactedEnvelopes
            && totalValidPositions
                == topology.TotalValidPositions
        )
        {
            PublishUploadedPositionTypeCounts(
                greenhousePositionCount,
                cellarPositionCount,
                roomPositionCount
            );

            Volatile.Write(
                ref uploadedPositionCount,
                selectedCount
            );

            Volatile.Write(
                ref validPositionCount,
                totalValidPositions
            );

            Volatile.Write(
                ref compactedEnvelopeCount,
                totalCompactedEnvelopes
            );

            Volatile.Write(
                ref uploadedCoveredPositionCount,
                selectedCoveredPositionCount
            );

            return;
        }

        int revision =
            Interlocked.Increment(
                ref snapshotRevision
            );

        topology =
            new RoomWindTopologySnapshot(
                positionValues,
                selectedCount,
                totalCompactedEnvelopes,
                totalValidPositions,
                hash,
                revision
            );

        if (
            StillGreenhousesClientSystem.Config
                ?.DebugLogging == true
        )
        {
            string positionPreview =
                selectedCount == 0
                    ? "<none>"
                    : string.Join(
                        "|",
                        selectedCandidates
                            .Take(16)
                            .Select(candidate =>
                            {
                                float relativeX =
                                    (float)(
                                        candidate.Position.X
                                        + candidate.Envelope.CenterX
                                        - referenceX
                                    );

                                float relativeY =
                                    (float)(
                                        candidate.Position.Y
                                        + candidate.Envelope.CenterY
                                        - referenceY
                                    );

                                float relativeZ =
                                    (float)(
                                        candidate.Position.Z
                                        + candidate.Envelope.CenterZ
                                        - referenceZ
                                    );

                                int extentX = QuantizeEnvelopeHalfExtent(
                                    candidate.Envelope.HalfExtentX
                                );

                                int extentY = QuantizeEnvelopeHalfExtent(
                                    candidate.Envelope.HalfExtentY
                                );

                                int extentZ = QuantizeEnvelopeHalfExtent(
                                    candidate.Envelope.HalfExtentZ
                                );

                                return
                                    $"{candidate.Position.X},{candidate.Position.Y},{candidate.Position.Z}" +
                                    $"->renderCenter({relativeX:0.###},{relativeY:0.###},{relativeZ:0.###})" +
                                    $"/halfQ({extentX},{extentY},{extentZ})" +
                                    $"/covered={candidate.CoveredPositionCount}" +
                                    $"/local=[{candidate.Envelope.MinX:0.###},{candidate.Envelope.MinY:0.###},{candidate.Envelope.MinZ:0.###}.." +
                                    $"{candidate.Envelope.MaxX:0.###},{candidate.Envelope.MaxY:0.###},{candidate.Envelope.MaxZ:0.###}]:" +
                                    $"{candidate.RoomType}/{candidate.Targets}/d2={candidate.DistanceSquared:0.##}";
                            })
                    );

            string entityPosition =
                api.World.Player?.Entity == null
                    ? "<none>"
                    : $"{api.World.Player.Entity.Pos.X:0.###}," +
                      $"{api.World.Player.Entity.Pos.Y:0.###}," +
                      $"{api.World.Player.Entity.Pos.Z:0.###}";

            string renderReferenceDescription =
                referenceAvailable
                    ? $"{referenceX:0.###},{referenceY:0.###},{referenceZ:0.###}"
                    : "<unavailable>";

            StillGreenhousesClientSystem.DebugLiteral(
                "[StillGreenhouses] ROOM WIND RENDER REFERENCE " +
                $"revision={revision}; " +
                $"entityPos={entityPosition}; " +
                $"shaderPlayerPos={shaderPlayerX:0.###},{shaderPlayerY:0.###},{shaderPlayerZ:0.###}; " +
                $"shaderPlayerReferencePos={renderReferenceDescription}; " +
                $"referenceAvailable={referenceAvailable}"
            );

            StillGreenhousesClientSystem.DebugLiteral(
                "[StillGreenhouses] ROOM WIND POSITION SNAPSHOT " +
                $"revision={revision}; " +
                $"registered={RegisteredPositions.Count}; " +
                $"validPositions={totalValidPositions}; " +
                $"compactedEnvelopes={totalCompactedEnvelopes}; " +
                $"selected={selectedCount}/{ConfiguredPositionBudget}; " +
                $"coveredPositions={selectedCoveredPositionCount}; " +
                $"matchStrategy=PlayerReferenceRelativeWindVertexEnvelope; " +
                "referenceSource=DefaultShaderUniforms.playerReferencePos; " +
                $"reference={renderReferenceDescription}; " +
                $"debugCallSiteProof={StillGreenhousesClientSystem.Config?.DebugRoomWindCallSiteProof == true}; " +
                $"debugVisualProof={StillGreenhousesClientSystem.Config?.DebugRoomWindVisualProof == true}; " +
                "proofTransform=WindVerticesYPlus0.35; " +
                $"positions={positionPreview}"
            );
        }

        if (
            totalCompactedEnvelopes
                > ConfiguredPositionBudget
            && Interlocked.Exchange(
                ref positionLimitWarningLogged,
                1
            ) == 0
        )
        {
            StillGreenhousesClientSystem.WarningLiteral(
                "[StillGreenhouses] ROOM WIND POSITION LIMIT REACHED " +
                $"registered={totalValidPositions}; " +
                $"compacted={totalCompactedEnvelopes}; " +
                $"uploaded={selectedCount}/{ConfiguredPositionBudget}; " +
                $"covered={selectedCoveredPositionCount}. " +
                "Nearest compacted envelopes are prioritized; positions outside the GPU mask retain vanilla global wind."
            );
        }
        else if (
            totalCompactedEnvelopes
                <= ConfiguredPositionBudget
        )
        {
            Volatile.Write(
                ref positionLimitWarningLogged,
                0
            );
        }

        PublishUploadedPositionTypeCounts(
            greenhousePositionCount,
            cellarPositionCount,
            roomPositionCount
        );

        Volatile.Write(
            ref uploadedPositionCount,
            selectedCount
        );

        Volatile.Write(
            ref validPositionCount,
            totalValidPositions
        );

        Volatile.Write(
            ref compactedEnvelopeCount,
            totalCompactedEnvelopes
        );

        Volatile.Write(
            ref uploadedCoveredPositionCount,
            selectedCoveredPositionCount
        );
    }

    private static List<PositionCandidate>
        SelectBudgetedPositions(
            IReadOnlyList<PositionCandidate> candidates
        )
    {
        int positionBudget =
            ConfiguredPositionBudget;

        if (candidates.Count <= positionBudget)
        {
            return new List<PositionCandidate>(
                candidates
            );
        }

        ReservedPositionBudgets reservedBudgets =
            CalculateReservedPositionBudgets(
                positionBudget
            );

        List<PositionCandidate> selected =
            new(positionBudget);

        HashSet<ManagedVegetationShaderPosition>
            selectedPositions = new();

        AddBestPositions(
            candidates,
            reservedBudgets.Greenhouse,
            selected,
            selectedPositions,
            roomType: ManagedRoomType.Greenhouse
        );

        AddBestPositions(
            candidates,
            reservedBudgets.Cellar,
            selected,
            selectedPositions,
            roomType: ManagedRoomType.Cellar
        );

        AddBestPositions(
            candidates,
            reservedBudgets.Room,
            selected,
            selectedPositions,
            roomType: ManagedRoomType.Room
        );

        AddBestPositions(
            candidates,
            positionBudget - selected.Count,
            selected,
            selectedPositions,
            roomType: null
        );

        return selected;
    }

    private static ReservedPositionBudgets
        CalculateReservedPositionBudgets(
            int totalBudget
        )
    {
        int greenhouse =
            Math.Max(1, totalBudget / 2);

        int cellar =
            totalBudget >= 2
                ? Math.Max(1, totalBudget / 4)
                : 0;

        int room =
            totalBudget - greenhouse - cellar;

        if (
            totalBudget >= 3
            && room < 1
        )
        {
            room = 1;

            if (greenhouse > 1)
            {
                greenhouse--;
            }
            else
            {
                cellar--;
            }
        }

        return new ReservedPositionBudgets(
            greenhouse,
            cellar,
            room
        );
    }

    private static void AddBestPositions(
        IReadOnlyList<PositionCandidate> candidates,
        int budget,
        List<PositionCandidate> selected,
        HashSet<ManagedVegetationShaderPosition> selectedPositions,
        ManagedRoomType? roomType
    )
    {
        if (
            budget <= 0
            || selected.Count
                >= ConfiguredPositionBudget
        )
        {
            return;
        }

        int remainingCapacity =
            Math.Min(
                budget,
                ConfiguredPositionBudget
                    - selected.Count
            );

        PriorityQueue<
            PositionCandidate,
            CandidateSelectionPriority
        > bestCandidates = new(
            CandidateWorstFirstPriorityComparer.Instance
        );

        foreach (
            PositionCandidate candidate
            in candidates
        )
        {
            if (
                (
                    roomType.HasValue
                    && candidate.RoomType
                        != roomType.Value
                )
                || selectedPositions.Contains(
                    candidate.Position
                )
            )
            {
                continue;
            }

            bestCandidates.Enqueue(
                candidate,
                CandidateSelectionPriority.From(
                    candidate
                )
            );

            if (
                bestCandidates.Count
                    > remainingCapacity
            )
            {
                bestCandidates.Dequeue();
            }
        }

        while (
            bestCandidates.TryDequeue(
                out PositionCandidate candidate,
                out _
            )
        )
        {
            if (selectedPositions.Add(candidate.Position))
            {
                selected.Add(candidate);
            }
        }
    }

    private static void PublishUploadedPositionTypeCounts(
        int greenhouseCount,
        int cellarCount,
        int roomCount
    )
    {
        Volatile.Write(
            ref uploadedGreenhousePositionCount,
            greenhouseCount
        );

        Volatile.Write(
            ref uploadedCellarPositionCount,
            cellarCount
        );

        Volatile.Write(
            ref uploadedRoomPositionCount,
            roomCount
        );
    }

    private void DiscoverPrograms(
        string verificationStage
    )
    {
        long startTimestamp =
            Stopwatch.GetTimestamp();

        Interlocked.Increment(
            ref programDiscoveryRuns
        );

        bool bridgeWasReady = UniformBridgeReady;

        List<ShaderProgramBinding>
            discoveredBindings = new();

        List<ProgramDiagnostic>
            diagnostics = new();

        HashSet<int> seenProgramIds = new();

        bool chunkOpaqueBound = false;
        int diagnosticHash = 17;

        foreach (
            EnumShaderProgram target
            in TargetPrograms
        )
        {
            bool positionCountFound = false;
            bool positionsFound = false;
            bool stateCountFound = false;
            bool statesFound = false;
            bool debugVisualProofFound = false;
            bool bound = false;

            string reason =
                "<none>";

            string passName =
                "<unavailable>";

            int programId =
                -1;

            try
            {
                IShaderProgram? program =
                    api.Shader.GetProgram(
                        (int)target
                    );

                if (program == null)
                {
                    reason =
                        "program-null";
                }
                else if (program.Disposed)
                {
                    reason =
                        "program-disposed";
                }
                else
                {
                    passName =
                        program.PassName
                        ?? target.ToString();

                    programId =
                        program.ProgramId;

                    positionCountFound =
                        program.HasUniform(
                            StillGreenhousesRoomWindShaderPatch
                                .PositionCountUniformName
                        );

                    positionsFound =
                        HasCellHashUniforms(program);

                    string? positionsUniformName =
                        positionsFound
                            ? StillGreenhousesRoomWindShaderPatch
                                .CellHashTextureUniformName
                            : null;

                    stateCountFound =
                        program.HasUniform(
                            StillGreenhousesRoomWindShaderPatch
                                .StateCountUniformName
                        );

                    string? statesUniformName =
                        ResolveArrayUniformName(
                            program,
                            StillGreenhousesRoomWindShaderPatch
                                .StatesUniformName
                        );

                    statesFound =
                        statesUniformName != null;

                    debugVisualProofFound =
                        program.HasUniform(
                            StillGreenhousesRoomWindShaderPatch
                                .DebugVisualProofUniformName
                        );

                    if (!positionCountFound)
                    {
                        reason =
                            "position-count-uniform-missing";
                    }
                    else if (!positionsFound)
                    {
                        reason =
                            "cell-hash-uniform-missing";
                    }
                    else if (!stateCountFound)
                    {
                        reason =
                            "state-count-uniform-missing";
                    }
                    else if (!statesFound)
                    {
                        reason =
                            "states-uniform-missing";
                    }
                    else if (!seenProgramIds.Add(
                            program.ProgramId
                        ))
                    {
                        reason =
                            "duplicate-program-id";
                    }
                    else
                    {
                        discoveredBindings.Add(
                            new ShaderProgramBinding(
                                target,
                                program,
                                positionsUniformName!,
                                statesUniformName!,
                                debugVisualProofFound
                            )
                        );

                        bound = true;

                        if (
                            target
                                == EnumShaderProgram.Chunkopaque
                        )
                        {
                            chunkOpaqueBound = true;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                reason =
                    $"{e.GetType().Name}:{e.Message}";
            }

            diagnostics.Add(
                new ProgramDiagnostic(
                    target,
                    passName,
                    programId,
                    positionCountFound,
                    positionsFound,
                    stateCountFound,
                    statesFound,
                    debugVisualProofFound,
                    bound,
                    reason
                )
            );

            diagnosticHash =
                HashCode.Combine(
                    diagnosticHash,
                    target,
                    programId,
                    positionCountFound,
                    positionsFound,
                    stateCountFound,
                    statesFound,
                    debugVisualProofFound
                );

            diagnosticHash =
                HashCode.Combine(
                    diagnosticHash,
                    bound,
                    reason
                );
        }

        diagnosticHash =
            HashCode.Combine(
                diagnosticHash,
                verificationStage,
                StillGreenhousesRoomWindShaderPatch
                    .ResolvedOverrideSourceHash
            );

        bool diagnosticsChanged =
            diagnosticHash
                != lastProgramDiagnosticHash;

        if (diagnosticsChanged)
        {
            foreach (
                ProgramDiagnostic diagnostic
                in diagnostics
            )
            {
                StillGreenhousesClientSystem.DebugLiteral(
                    "[StillGreenhouses] ROOM WIND COMPILED BRIDGE VERIFY " +
                    $"stage={verificationStage}; " +
                    $"assetSourceHash={StillGreenhousesRoomWindShaderPatch.ResolvedOverrideSourceHash}; " +
                    $"target={diagnostic.Target}; " +
                    $"role={ResolveProgramRole(diagnostic.Target)}; " +
                    $"passName={diagnostic.PassName}; " +
                    $"programId={diagnostic.ProgramId}; " +
                    $"positionCount={diagnostic.PositionCountFound}; " +
                    $"positions={diagnostic.PositionsFound}; " +
                    $"stateCount={diagnostic.StateCountFound}; " +
                    $"states={diagnostic.StatesFound}; " +
                    $"debugVisualProof={diagnostic.DebugVisualProofFound}; " +
                    $"bound={diagnostic.Bound}; " +
                    $"reason={diagnostic.Reason}"
                );
            }
        }

        lastProgramDiagnosticHash =
            diagnosticHash;

        programBindings.Clear();
        programBindings.AddRange(
            discoveredBindings
        );

        int terrainVegetationProgramCount =
            programBindings.Count(
                binding =>
                    IsTerrainVegetationProgram(
                        binding.Target
                    )
            );

        int auxiliaryVegetationProgramCount =
            programBindings.Count(
                binding =>
                    IsAuxiliaryVegetationProgram(
                        binding.Target
                    )
            );

        int vegetationProgramCount =
            terrainVegetationProgramCount
            + auxiliaryVegetationProgramCount;

        int liquidProgramCount =
            programBindings.Count(
                binding =>
                    IsLiquidProgram(
                        binding.Target
                    )
            );

        Volatile.Write(
            ref activeProgramCount,
            programBindings.Count
        );

        Volatile.Write(
            ref activeVegetationProgramCount,
            vegetationProgramCount
        );

        Volatile.Write(
            ref activeTerrainVegetationProgramCount,
            terrainVegetationProgramCount
        );

        Volatile.Write(
            ref activeAuxiliaryVegetationProgramCount,
            auxiliaryVegetationProgramCount
        );

        Volatile.Write(
            ref activeLiquidProgramCount,
            liquidProgramCount
        );

        Volatile.Write(
            ref requiredChunkOpaqueBound,
            chunkOpaqueBound ? 1 : 0
        );

        Volatile.Write(
            ref uniformBridgeReady,
            chunkOpaqueBound ? 1 : 0
        );

        if (
            bridgeWasReady != chunkOpaqueBound
            && StillGreenhousesRoomWindEnvironment
                .IsShaderDrivenMode(
                    StillGreenhousesClientSystem
                        .PlantMovementMode
                )
        )
        {
            int redrawnChunks =
                StillGreenhousesClientSystem
                    .RedrawCachedManagedRoomChunksForShaderChange();

            api.Logger.Notification(
                "[StillGreenhouses] ROOM WIND BRIDGE STATE CHANGED " +
                $"ready={chunkOpaqueBound}; " +
                $"redrawnManagedChunks={redrawnChunks}; " +
                $"fallback={(chunkOpaqueBound ? "room-local-wind" : "vanilla-wind-preserved")}"
            );
        }

        if (diagnosticsChanged)
        {
            api.Logger.Notification(
                "[StillGreenhouses] ROOM WIND UNIFORM PROGRAMS " +
                $"stage={verificationStage}; " +
                $"assetSourceHash={StillGreenhousesRoomWindShaderPatch.ResolvedOverrideSourceHash}; " +
                $"activePrograms={programBindings.Count}; " +
                $"targetPrograms={TargetProgramCount}; " +
                $"activeVegetationPrograms={vegetationProgramCount}/{VegetationTargetProgramCount}; " +
                $"activeTerrainVegetationPrograms={terrainVegetationProgramCount}/{TerrainVegetationTargetProgramCount}; " +
                $"activeAuxiliaryVegetationPrograms={auxiliaryVegetationProgramCount}/{AuxiliaryVegetationTargetProgramCount}; " +
                $"activeLiquidPrograms={liquidProgramCount}/{LiquidTargetProgramCount}; " +
                $"topsoilWindConsumer=False; " +
                $"requiredChunkOpaqueBound={chunkOpaqueBound}; " +
                $"compiledBridgeVerified={chunkOpaqueBound}; " +
                $"shaderReloadAttempted={StillGreenhousesRoomWindShaderPatch.ShaderReloadAttempted}; " +
                $"shaderReloadSucceeded={StillGreenhousesRoomWindShaderPatch.ShaderReloadSucceeded}; " +
                "transport=quarter-block-texture-hash; " +
                $"hashProbeLimit={RoomWindCellHash.ProbeLimit}; " +
                $"hashCells={cellHashSnapshot.EntryCount}; " +
                $"hashCapacity={cellHashSnapshot.Capacity}; " +
                $"roomTypeStates={UploadedRoomTypeStateCount}"
            );
        }

        long elapsedMicroseconds =
            GetElapsedMicroseconds(
                startTimestamp
            );

        Interlocked.Exchange(
            ref lastProgramDiscoveryMicroseconds,
            elapsedMicroseconds
        );

        UpdateMaximum(
            ref maxProgramDiscoveryMicroseconds,
            elapsedMicroseconds
        );

        if (
            StillGreenhousesClientSystem.Config
                ?.DebugLogging == true
            && elapsedMicroseconds >= 2000
        )
        {
            StillGreenhousesClientSystem.DebugLiteral(
                "[StillGreenhouses] CLIENT PERF " +
                "operation=render-program-discovery; " +
                $"elapsedMs={elapsedMicroseconds / 1000d:F3}; " +
                $"stage={verificationStage}; " +
                $"targets={TargetProgramCount}; " +
                $"activePrograms={programBindings.Count}; " +
                "thread=render"
            );
        }
    }

    private static bool IsTerrainVegetationProgram(
        EnumShaderProgram target
    ) =>
        target
            is EnumShaderProgram.Chunkopaque
            or EnumShaderProgram.Chunktransparent
            or EnumShaderProgram.Chunkshadowmap
            or EnumShaderProgram.Chunkshadowmap_NoSSBOs;

    private static bool IsAuxiliaryVegetationProgram(
        EnumShaderProgram target
    ) =>
        target
            is EnumShaderProgram.Standard
            or EnumShaderProgram.Entityanimated
            or EnumShaderProgram.Shadowmapentityanimated
            or EnumShaderProgram.Entityanimated_Oit;

    private static bool IsVegetationProgram(
        EnumShaderProgram target
    ) =>
        IsTerrainVegetationProgram(target)
        || IsAuxiliaryVegetationProgram(target);

    private static bool IsLiquidProgram(
        EnumShaderProgram target
    ) =>
        target
            is EnumShaderProgram.Chunkliquid
            or EnumShaderProgram.Chunkliquiddepth;

    private static string ResolveProgramRole(
        EnumShaderProgram target
    ) =>
        IsTerrainVegetationProgram(target)
            ? "TerrainVegetation"
            : IsAuxiliaryVegetationProgram(target)
                ? "AuxiliaryVegetation"
                : IsLiquidProgram(target)
                    ? "Liquid"
                    : "IncludeOnly";

    private static string? ResolveArrayUniformName(
        IShaderProgram program,
        string baseName
    )
    {
        string arrayZeroName =
            baseName + "[0]";

        // OpenGL specifies the first element as the stable location for a
        // uniform array. Some drivers also accept the bare array name, but
        // accepting it in HasUniform() does not guarantee that a bulk upload
        // using that spelling will address element zero.
        if (program.HasUniform(arrayZeroName))
        {
            return arrayZeroName;
        }

        return program.HasUniform(baseName)
            ? baseName
            : null;
    }

    private static bool HasCellHashUniforms(
        IShaderProgram program
    ) =>
        program.HasUniform(
            StillGreenhousesRoomWindShaderPatch.CellHashTextureUniformName
        )
        && program.HasUniform(
            StillGreenhousesRoomWindShaderPatch.CellHashCapacityUniformName
        )
        && program.HasUniform(
            StillGreenhousesRoomWindShaderPatch.CellHashMaskUniformName
        )
        && program.HasUniform(
            StillGreenhousesRoomWindShaderPatch.CellHashSeedUniformName
        )
        && program.HasUniform(
            StillGreenhousesRoomWindShaderPatch.CellHashTextureWidthUniformName
        )
        && program.HasUniform(
            StillGreenhousesRoomWindShaderPatch.CellHashOriginXUniformName
        )
        && program.HasUniform(
            StillGreenhousesRoomWindShaderPatch.CellHashOriginYUniformName
        )
        && program.HasUniform(
            StillGreenhousesRoomWindShaderPatch.CellHashOriginZUniformName
        )
        && program.HasUniform(
            StillGreenhousesRoomWindShaderPatch.RenderReferenceQuarterXUniformName
        )
        && program.HasUniform(
            StillGreenhousesRoomWindShaderPatch.RenderReferenceQuarterYUniformName
        )
        && program.HasUniform(
            StillGreenhousesRoomWindShaderPatch.RenderReferenceQuarterZUniformName
        )
        && program.HasUniform(
            StillGreenhousesRoomWindShaderPatch.RenderReferenceFractionXUniformName
        )
        && program.HasUniform(
            StillGreenhousesRoomWindShaderPatch.RenderReferenceFractionYUniformName
        )
        && program.HasUniform(
            StillGreenhousesRoomWindShaderPatch.RenderReferenceFractionZUniformName
        );

    private void UploadRoomWindState()
    {
        if (cellHashTexture == null)
        {
            try
            {
                UploadCellHashTexture(cellHashSnapshot);
                cellHashRevision++;
            }
            catch (Exception e)
            {
                Volatile.Write(
                    ref lastUniformUploadFailure,
                    $"CellHashTexture:{e.GetType().Name}:{e.Message}"
                );
                return;
            }
        }

        FillStateValues(
            stateValueBuffer
        );

        LoadedTexture activeCellHashTexture = cellHashTexture
            ?? throw new InvalidOperationException(
                "The room-wind cell hash texture was not created."
            );

        long startTimestamp =
            Stopwatch.GetTimestamp();

        bool anyUploadSucceeded = false;
        bool anyUploadFailed = false;
        bool bindingsChanged = false;
        int programBindOperations = 0;

        int debugMode =
            StillGreenhousesClientSystem.Config
                ?.DebugRoomWindCallSiteProof == true
                    ? 2
                    : StillGreenhousesClientSystem.Config
                        ?.DebugRoomWindVisualProof == true
                            ? 1
                            : 0;

        for (
            int i = programBindings.Count - 1;
            i >= 0;
            i--
        )
        {
            ShaderProgramBinding binding =
                programBindings[i];

            if (binding.Program.Disposed)
            {
                programBindings.RemoveAt(i);
                bindingsChanged = true;
                nextProgramDiscoveryMs = 0;

                continue;
            }

            try
            {
                bool hashChanged =
                    binding.LastUploadedTopologyRevision
                        != cellHashRevision;
                bool stateChanged =
                    binding.LastUploadedStateRevision
                        != stateRevision;
                bool referenceChanged =
                    binding.LastUploadedRenderReferenceRevision
                        != renderReferenceRevision;
                bool debugChanged =
                    binding.DebugVisualProofUniformFound
                    && binding.LastUploadedDebugMode != debugMode;

                if (
                    !hashChanged
                    && !stateChanged
                    && !referenceChanged
                    && !debugChanged
                )
                {
                    continue;
                }

                binding.Program.Use();
                programBindOperations++;

                binding.Program.BindTexture2D(
                    StillGreenhousesRoomWindShaderPatch
                        .CellHashTextureUniformName,
                    activeCellHashTexture.TextureId,
                    CellHashTextureUnit
                );

                if (hashChanged)
                {
                    binding.Program.Uniform(
                        StillGreenhousesRoomWindShaderPatch
                            .PositionCountUniformName,
                        cellHashSnapshot.EntryCount
                    );
                    binding.Program.Uniform(
                        StillGreenhousesRoomWindShaderPatch
                            .CellHashCapacityUniformName,
                        cellHashSnapshot.Capacity
                    );
                    binding.Program.Uniform(
                        StillGreenhousesRoomWindShaderPatch
                            .CellHashMaskUniformName,
                        cellHashSnapshot.Mask
                    );
                    binding.Program.Uniform(
                        StillGreenhousesRoomWindShaderPatch
                            .CellHashSeedUniformName,
                        unchecked((int)cellHashSnapshot.Seed)
                    );
                    binding.Program.Uniform(
                        StillGreenhousesRoomWindShaderPatch
                            .CellHashTextureWidthUniformName,
                        cellHashSnapshot.Width
                    );
                    binding.Program.Uniform(
                        StillGreenhousesRoomWindShaderPatch
                            .CellHashOriginXUniformName,
                        cellHashSnapshot.Origin.X
                    );
                    binding.Program.Uniform(
                        StillGreenhousesRoomWindShaderPatch
                            .CellHashOriginYUniformName,
                        cellHashSnapshot.Origin.Y
                    );
                    binding.Program.Uniform(
                        StillGreenhousesRoomWindShaderPatch
                            .CellHashOriginZUniformName,
                        cellHashSnapshot.Origin.Z
                    );
                    binding.LastUploadedTopologyRevision =
                        cellHashRevision;
                }

                if (referenceChanged)
                {
                    UploadRenderReferenceUniforms(binding.Program);
                    binding.LastUploadedRenderReferenceRevision =
                        renderReferenceRevision;
                }

                if (debugChanged)
                {
                    binding.Program.Uniform(
                        StillGreenhousesRoomWindShaderPatch
                            .DebugVisualProofUniformName,
                        debugMode
                    );

                    binding.LastUploadedDebugMode =
                        debugMode;
                }

                if (stateChanged)
                {
                    binding.Program.Uniform(
                        StillGreenhousesRoomWindShaderPatch
                            .StateCountUniformName,
                        UploadedRoomTypeStateCount
                    );
                    binding.Program.Uniforms4(
                        binding.StatesUniformName,
                        UploadedShaderStateVectorCount,
                        stateValueBuffer
                    );
                    binding.LastUploadedStateRevision = stateRevision;
                }

                binding.Program.Stop();

                anyUploadSucceeded = true;
            }
            catch (Exception e)
            {
                anyUploadFailed = true;
                bindingsChanged = true;
                nextProgramDiscoveryMs = 0;

                try
                {
                    binding.Program.Stop();
                }
                catch
                {
                    // Ignore secondary state cleanup failures.
                }

                StillGreenhousesClientSystem.WarningLiteral(
                    "[StillGreenhouses] ROOM WIND UNIFORM UPLOAD FAILED " +
                    $"target={binding.Target}; " +
                    $"program={binding.Program.PassName}; " +
                    $"error={e.GetType().Name}:{e.Message}"
                );

                Volatile.Write(
                    ref lastUniformUploadFailure,
                    $"{binding.Target}:" +
                    $"{e.GetType().Name}:" +
                    e.Message.Replace('\r', ' ')
                        .Replace('\n', ' ')
                );

                programBindings.RemoveAt(i);
            }
        }

        // The sampler uniform is persistent, but OpenGL texture-unit bindings
        // are global state. Refresh the high-numbered unit once per frame when
        // no program needed a metadata upload; this replaces the previous nine
        // Use/Stop pairs per frame with one bounded operation.
        if (
            programBindOperations == 0
            && cellHashTexture != null
        )
        {
            foreach (ShaderProgramBinding binding in programBindings)
            {
                if (binding.Program.Disposed)
                {
                    continue;
                }

                try
                {
                    binding.Program.Use();
                    binding.Program.BindTexture2D(
                        StillGreenhousesRoomWindShaderPatch
                            .CellHashTextureUniformName,
                        activeCellHashTexture.TextureId,
                        CellHashTextureUnit
                    );
                    binding.Program.Stop();
                    programBindOperations++;
                    anyUploadSucceeded = true;
                    break;
                }
                catch
                {
                    try
                    {
                        binding.Program.Stop();
                    }
                    catch
                    {
                    }
                }
            }
        }

        if (
            anyUploadSucceeded
            && !anyUploadFailed
        )
        {
            Volatile.Write(
                ref lastUniformUploadFailure,
                "<none>"
            );
        }

        if (bindingsChanged)
        {
            PublishProgramBindingMetrics();
        }

        Volatile.Write(
            ref uploadedRoomStateCount,
            anyUploadSucceeded
                ? UploadedRoomTypeStateCount
                : 0
        );

        Interlocked.Increment(
            ref uniformUploadRuns
        );

        Interlocked.Add(
            ref uniformProgramBindOperations,
            programBindOperations
        );

        long elapsedMicroseconds =
            GetElapsedMicroseconds(
                startTimestamp
            );

        Interlocked.Exchange(
            ref lastUniformUploadMicroseconds,
            elapsedMicroseconds
        );

        UpdateMaximum(
            ref maxUniformUploadMicroseconds,
            elapsedMicroseconds
        );

        if (
            StillGreenhousesClientSystem.Config
                ?.DebugLogging == true
            && elapsedMicroseconds >= 2000
        )
        {
            StillGreenhousesClientSystem.DebugLiteral(
                "[StillGreenhouses] CLIENT PERF " +
                "operation=render-uniform-upload; " +
                $"elapsedMs={elapsedMicroseconds / 1000d:F3}; " +
                $"programBindings={programBindings.Count}; " +
                $"programUseStopPairs={programBindOperations}; " +
                $"topologyRevision={topology.Revision}; " +
                "thread=render"
            );
        }
    }

    private void PublishProgramBindingMetrics()
    {
        bool chunkOpaqueBound = false;
        int terrainVegetationProgramCount = 0;
        int auxiliaryVegetationProgramCount = 0;
        int liquidProgramCount = 0;

        foreach (
            ShaderProgramBinding binding
            in programBindings
        )
        {
            if (binding.Program.Disposed)
            {
                continue;
            }

            if (
                binding.Target
                    == EnumShaderProgram.Chunkopaque
            )
            {
                chunkOpaqueBound = true;
            }

            if (IsTerrainVegetationProgram(binding.Target))
            {
                terrainVegetationProgramCount++;
            }
            else if (IsAuxiliaryVegetationProgram(binding.Target))
            {
                auxiliaryVegetationProgramCount++;
            }
            else if (IsLiquidProgram(binding.Target))
            {
                liquidProgramCount++;
            }
        }

        int vegetationProgramCount =
            terrainVegetationProgramCount
            + auxiliaryVegetationProgramCount;

        Volatile.Write(
            ref activeProgramCount,
            terrainVegetationProgramCount
            + auxiliaryVegetationProgramCount
            + liquidProgramCount
        );

        Volatile.Write(
            ref activeVegetationProgramCount,
            vegetationProgramCount
        );

        Volatile.Write(
            ref activeTerrainVegetationProgramCount,
            terrainVegetationProgramCount
        );

        Volatile.Write(
            ref activeAuxiliaryVegetationProgramCount,
            auxiliaryVegetationProgramCount
        );

        Volatile.Write(
            ref activeLiquidProgramCount,
            liquidProgramCount
        );

        Volatile.Write(
            ref requiredChunkOpaqueBound,
            chunkOpaqueBound ? 1 : 0
        );

        Volatile.Write(
            ref uniformBridgeReady,
            chunkOpaqueBound ? 1 : 0
        );
    }

    private void UploadRenderReferenceUniforms(
        IShaderProgram program
    )
    {
        program.Uniform(
            StillGreenhousesRoomWindShaderPatch
                .RenderReferenceQuarterXUniformName,
            renderReferenceQuarterX
        );
        program.Uniform(
            StillGreenhousesRoomWindShaderPatch
                .RenderReferenceQuarterYUniformName,
            renderReferenceQuarterY
        );
        program.Uniform(
            StillGreenhousesRoomWindShaderPatch
                .RenderReferenceQuarterZUniformName,
            renderReferenceQuarterZ
        );
        program.Uniform(
            StillGreenhousesRoomWindShaderPatch
                .RenderReferenceFractionXUniformName,
            renderReferenceQuarterFractionX
        );
        program.Uniform(
            StillGreenhousesRoomWindShaderPatch
                .RenderReferenceFractionYUniformName,
            renderReferenceQuarterFractionY
        );
        program.Uniform(
            StillGreenhousesRoomWindShaderPatch
                .RenderReferenceFractionZUniformName,
            renderReferenceQuarterFractionZ
        );
    }

    private void FillStateValues(
        float[] values
    )
    {
        float timeCounter = api.Render.ShaderUniforms.TimeCounter;

        for (
            int i = 0;
            i < UploadedRoomTypeStateCount;
            i++
        )
        {
            RoomWindRuntimeSnapshot snapshot =
                roomTypeStates[i]
                    .Snapshot();

            int offset =
                i * 4;

            values[offset] =
                snapshot.WindSpeed;

            float regularRate =
                0.5f + 2f * snapshot.WindSpeed;

            float highFrequencyRate =
                (0.4f + snapshot.WindSpeed / 10f)
                * (0.5f + 5f * snapshot.WindSpeed);

            // Y and Z are phase offsets. The patched shader adds timeCounter
            // times the room-local rate, so motion remains continuous without
            // rebinding every target shader program on every frame.
            values[offset + 1] =
                snapshot.WindWaveCounter
                - timeCounter * regularRate;

            values[offset + 2] =
                snapshot.WindWaveCounterHighFreq
                - timeCounter * highFrequencyRate;

            // A negative W component is the shader's explicit exact-no-wind
            // marker. WindSpeed=0 alone is insufficient because Vanilla modes
            // 1, 2, and 13 retain a small baseline strength at zero wind.
            values[offset + 3] =
                snapshot.CurrentPercent <= 0.0001f
                    ? -1f
                    : snapshot.CurrentPercent;
        }
    }

    private static ulong AddHash(
        ulong hash,
        int value
    )
    {
        unchecked
        {
            uint data =
                (uint)value;

            for (int i = 0; i < 4; i++)
            {
                hash ^=
                    (byte)(
                        data
                        >> (i * 8)
                    );

                hash *=
                    1099511628211UL;
            }
        }

        return hash;
    }

    public void Dispose()
    {
        if (
            Interlocked.Exchange(
                ref disposed,
                1
            ) != 0
        )
        {
            return;
        }

        programBindings.Clear();
        compactedRegistrations.Clear();
        compactedRegistrationRevision = int.MinValue;
        DisposeCellHashTexture();

        Volatile.Write(
            ref activeProgramCount,
            0
        );

        Volatile.Write(
            ref activeVegetationProgramCount,
            0
        );

        Volatile.Write(
            ref activeTerrainVegetationProgramCount,
            0
        );

        Volatile.Write(
            ref activeAuxiliaryVegetationProgramCount,
            0
        );

        Volatile.Write(
            ref activeLiquidProgramCount,
            0
        );

        Volatile.Write(
            ref requiredChunkOpaqueBound,
            0
        );

        Volatile.Write(
            ref uniformBridgeReady,
            0
        );

        Volatile.Write(
            ref uploadedRoomStateCount,
            0
        );

        Volatile.Write(
            ref compactedEnvelopeCount,
            0
        );

        Volatile.Write(
            ref uploadedCoveredPositionCount,
            0
        );
    }

    private sealed class RoomWindRuntimeState
    {
        internal ManagedRoomType RoomType { get; }

        internal RoomWindTargetKind Target { get; }

        internal float LowerPercent { get; }

        internal float UpperPercent { get; }

        internal bool IsFixed =>
            Math.Abs(
                UpperPercent - LowerPercent
            ) < 0.0001f;

        internal float CurrentPercent
        {
            get;
            private set;
        }

        internal float WindSpeed =>
            CurrentPercent / 100f;

        private float windWaveCounter;
        private float windWaveCounterHighFreq;
        private float stepAccumulatorSeconds;
        private int stepDirection = 1;

        internal RoomWindRuntimeState(
            ManagedRoomType roomType,
            RoomWindTargetKind target,
            RoomWindRange range,
            float initialWindWaveCounter,
            float initialWindWaveCounterHighFreq,
            float phaseOffsetSeconds
        )
        {
            RoomType = roomType;
            Target = target;
            LowerPercent = range.LowerPercent;
            UpperPercent = range.UpperPercent;
            CurrentPercent = LowerPercent;

            windWaveCounter =
                initialWindWaveCounter;

            windWaveCounterHighFreq =
                initialWindWaveCounterHighFreq;

            stepAccumulatorSeconds =
                IsFixed
                    ? 0f
                    : Math.Clamp(
                        phaseOffsetSeconds,
                        0f,
                        AmbientWindStepIntervalSeconds
                    );
        }

        internal bool Advance(float scaledDt)
        {
            if (scaledDt <= 0f)
            {
                return false;
            }

            float previousPercent = CurrentPercent;

            AdvanceWindTarget(
                scaledDt
            );

            float windSpeed =
                WindSpeed;

            // Room phases intentionally depend only on the room-local speed.
            // The shader advances these same rates from timeCounter, allowing
            // the six state vectors to be uploaded only when a configured
            // range actually steps instead of once per render frame.
            windWaveCounter =
                (
                    windWaveCounter
                    + (
                        0.5f
                        + 2f
                        * windSpeed
                    )
                    * scaledDt
                )
                % 6000f;

            float freq =
                0.4f
                + windSpeed / 10f;

            windWaveCounterHighFreq =
                (
                    windWaveCounterHighFreq
                    + freq
                    * (
                        0.5f
                        + 5f
                        * windSpeed
                    )
                    * scaledDt
                )
                % 6000f;

            return Math.Abs(CurrentPercent - previousPercent) > 0.0001f;
        }

        private void AdvanceWindTarget(
            float scaledDt
        )
        {
            if (IsFixed)
            {
                CurrentPercent =
                    LowerPercent;

                return;
            }

            stepAccumulatorSeconds +=
                scaledDt;

            while (
                stepAccumulatorSeconds
                    >= AmbientWindStepIntervalSeconds
            )
            {
                stepAccumulatorSeconds -=
                    AmbientWindStepIntervalSeconds;

                StepWindPercent();
            }
        }

        private void StepWindPercent()
        {
            float span =
                UpperPercent
                - LowerPercent;

            float stepPercent =
                Math.Min(
                    1f,
                    span
                );

            float next =
                CurrentPercent
                + stepDirection
                * stepPercent;

            if (next >= UpperPercent)
            {
                CurrentPercent =
                    UpperPercent;

                stepDirection =
                    -1;

                return;
            }

            if (next <= LowerPercent)
            {
                CurrentPercent =
                    LowerPercent;

                stepDirection =
                    1;

                return;
            }

            CurrentPercent =
                next;
        }

        internal RoomWindRuntimeSnapshot Snapshot() =>
            new(
                RoomType,
                CurrentPercent,
                WindSpeed,
                windWaveCounter,
                windWaveCounterHighFreq
            );
    }

    private sealed class ShaderProgramBinding
    {
        internal EnumShaderProgram Target { get; }

        internal IShaderProgram Program { get; }

        internal string PositionsUniformName { get; }

        internal string StatesUniformName { get; }

        internal bool DebugVisualProofUniformFound { get; }

        internal int LastUploadedTopologyRevision { get; set; } =
            int.MinValue;

        internal int LastUploadedStateRevision { get; set; } =
            int.MinValue;

        internal int LastUploadedRenderReferenceRevision { get; set; } =
            int.MinValue;

        internal int LastUploadedDebugMode { get; set; } =
            int.MinValue;

        internal ShaderProgramBinding(
            EnumShaderProgram target,
            IShaderProgram program,
            string positionsUniformName,
            string statesUniformName,
            bool debugVisualProofUniformFound
        )
        {
            Target = target;
            Program = program;
            PositionsUniformName =
                positionsUniformName;
            StatesUniformName =
                statesUniformName;
            DebugVisualProofUniformFound =
                debugVisualProofUniformFound;
        }
    }

    private readonly record struct PositionCandidate(
        ManagedVegetationShaderPosition Position,
        ManagedRoomType RoomType,
        RoomWindTargetKind Targets,
        ManagedRoomWindEnvelope Envelope,
        int CoveredPositionCount,
        double DistanceSquared
    );

    private readonly record struct CompactionRunKey(
        ChunkKey Chunk,
        int Y,
        int Z,
        GreenhouseKey RoomKey,
        ManagedRoomType RoomType,
        RoomWindTargetKind Targets,
        ManagedRoomWindEnvelope Envelope
    );

    private readonly record struct EncodedEnvelopeBounds(
        double MinX,
        double MinY,
        double MinZ,
        double MaxX,
        double MaxY,
        double MaxZ
    )
    {
        internal bool Overlaps(
            EncodedEnvelopeBounds other
        ) =>
            MinX <= other.MaxX
            && MaxX >= other.MinX
            && MinY <= other.MaxY
            && MaxY >= other.MinY
            && MinZ <= other.MaxZ
            && MaxZ >= other.MinZ;
    }

    private readonly record struct EncodedEnvelopeBucketKey(
        int X,
        int Y,
        int Z,
        int Dimension
    );

    private readonly record struct EnvelopeBucketRange(
        int MinX,
        int MinY,
        int MinZ,
        int MaxX,
        int MaxY,
        int MaxZ
    );

    private readonly record struct CandidateSelectionPriority(
        bool IsVegetation,
        double DistanceSquared,
        int Dimension,
        int X,
        int Y,
        int Z,
        int Targets,
        int RoomType
    )
    {
        internal static CandidateSelectionPriority From(
            PositionCandidate candidate
        ) =>
            new(
                (
                    candidate.Targets
                    & RoomWindTargetKind.Vegetation
                ) != 0,
                candidate.DistanceSquared,
                candidate.Position.Dimension,
                candidate.Position.X,
                candidate.Position.Y,
                candidate.Position.Z,
                (int)candidate.Targets,
                (int)candidate.RoomType
            );
    }

    private sealed class CandidateBestFirstPriorityComparer :
        IComparer<CandidateSelectionPriority>
    {
        internal static CandidateBestFirstPriorityComparer Instance { get; } =
            new();

        public int Compare(
            CandidateSelectionPriority left,
            CandidateSelectionPriority right
        )
        {
            int vegetationComparison =
                right.IsVegetation.CompareTo(
                    left.IsVegetation
                );

            if (vegetationComparison != 0)
            {
                return vegetationComparison;
            }

            int comparison =
                left.DistanceSquared.CompareTo(
                    right.DistanceSquared
                );

            if (comparison != 0) return comparison;

            comparison = left.Dimension.CompareTo(
                right.Dimension
            );

            if (comparison != 0) return comparison;

            comparison = left.X.CompareTo(right.X);
            if (comparison != 0) return comparison;

            comparison = left.Y.CompareTo(right.Y);
            if (comparison != 0) return comparison;

            comparison = left.Z.CompareTo(right.Z);
            if (comparison != 0) return comparison;

            comparison = left.Targets.CompareTo(
                right.Targets
            );

            return comparison != 0
                ? comparison
                : left.RoomType.CompareTo(
                    right.RoomType
                );
        }
    }

    private sealed class CandidateWorstFirstPriorityComparer :
        IComparer<CandidateSelectionPriority>
    {
        internal static CandidateWorstFirstPriorityComparer Instance { get; } =
            new();

        public int Compare(
            CandidateSelectionPriority left,
            CandidateSelectionPriority right
        ) =>
            -CandidateBestFirstPriorityComparer
                .Instance
                .Compare(
                    left,
                    right
                );
    }

    private readonly record struct ReservedPositionBudgets(
        int Greenhouse,
        int Cellar,
        int Room
    );

    private readonly record struct ProgramDiagnostic(
        EnumShaderProgram Target,
        string PassName,
        int ProgramId,
        bool PositionCountFound,
        bool PositionsFound,
        bool StateCountFound,
        bool StatesFound,
        bool DebugVisualProofFound,
        bool Bound,
        string Reason
    );

    private sealed record RoomWindTopologySnapshot(
        float[] PositionValues,
        int PositionCount,
        int TotalCompactedEnvelopes,
        int TotalValidPositions,
        ulong ContentHash,
        int Revision
    )
    {
        internal static RoomWindTopologySnapshot Empty { get; } =
            new(
                Array.Empty<float>(),
                0,
                0,
                0,
                0UL,
                0
            );
    }
}

// Shader bridge for the real Vanilla terrain topology.
//
// The supplied top-level shaders show that chunkopaque, chunktransparent, and
// chunkshadowmap pass vertex+origin into applyVertexWarping(). Vanilla then
// derives absolute world position as worldPos+playerpos inside vertexwarp.vsh.
//
// 0.17.2 keeps Vanilla's original WindMode/WindData switch intact and
// substitutes room-local wind speed and counters inside applyVertexWarping().
// 0.18 keeps those measured mesh envelopes but transports their rasterized
// absolute cells through a bounded-probe texture hash. Vegetation and water
// occupy independent payload bits and can never consume each other's budget.
//
// After Vanilla selects the native wind-mode branch, mode-aware correction
// normalizes only modes whose branch formulas do not interpret windSpeed as a
// direct movement-strength input. Modes 1, 2, and 13 remain fully Vanilla.
// Terrain call sites stay wrapped only for diagnostic proof transforms. Normal
// rendering uses the corrected native warp directly; there is no universal
// post-warp room-amplitude multiplier.
//
// Standard and Entityanimated-family programs are optional uniform consumers
// because their Vanilla shaders also include vertexwarp.vsh and call
// applyVertexWarping(). Chunkopaque remains the core bridge-readiness gate.
//
// chunktopsoil includes vertexwarp.vsh but deliberately does not call
// applyVertexWarping(); it is audited and excluded from active vegetation
// program readiness.
internal static partial class StillGreenhousesRoomWindShaderPatch
{
    internal const string TargetAssetLocation =
        "game:shaderincludes/vertexwarp.vsh";

    internal const string PositionCountUniformName =
        "stillGreenhousesRoomWindPositionCount";

    internal const string PositionsUniformName =
        "stillGreenhousesRoomWindPositions";

    internal const string CellHashTextureUniformName =
        "stillGreenhousesRoomWindCellHash";

    internal const string CellHashCapacityUniformName =
        "stillGreenhousesRoomWindCellHashCapacity";

    internal const string CellHashMaskUniformName =
        "stillGreenhousesRoomWindCellHashMask";

    internal const string CellHashSeedUniformName =
        "stillGreenhousesRoomWindCellHashSeed";

    internal const string CellHashTextureWidthUniformName =
        "stillGreenhousesRoomWindCellHashTextureWidth";

    internal const string CellHashOriginXUniformName =
        "stillGreenhousesRoomWindCellHashOriginX";

    internal const string CellHashOriginYUniformName =
        "stillGreenhousesRoomWindCellHashOriginY";

    internal const string CellHashOriginZUniformName =
        "stillGreenhousesRoomWindCellHashOriginZ";

    internal const string RenderReferenceQuarterXUniformName =
        "stillGreenhousesRoomWindReferenceQuarterX";

    internal const string RenderReferenceQuarterYUniformName =
        "stillGreenhousesRoomWindReferenceQuarterY";

    internal const string RenderReferenceQuarterZUniformName =
        "stillGreenhousesRoomWindReferenceQuarterZ";

    internal const string RenderReferenceFractionXUniformName =
        "stillGreenhousesRoomWindReferenceFractionX";

    internal const string RenderReferenceFractionYUniformName =
        "stillGreenhousesRoomWindReferenceFractionY";

    internal const string RenderReferenceFractionZUniformName =
        "stillGreenhousesRoomWindReferenceFractionZ";

    internal const string StateCountUniformName =
        "stillGreenhousesRoomWindStateCount";

    internal const string StatesUniformName =
        "stillGreenhousesRoomWindStates";

    internal const string DebugVisualProofUniformName =
        "stillGreenhousesRoomWindDebugVisualProof";

    private const string TargetFunctionName =
        "applyVertexWarping";

    private const string PatchMarker =
        "StillGreenhouses Vanilla room wind render reference bridge";

    private const string ModeAwareCorrectionMarker =
        "StillGreenhouses mode-aware room amplitude correction";

    private const string CallSiteWrapperFunctionName =
        "applyStillGreenhousesRoomWindPostWarp";

    private const string CallSitePatchMarker =
        "StillGreenhouses Vanilla warp managed proof callsite";

    private const string TargetLiquidFunctionName =
        "applyLiquidWarping";

    private const string LiquidPatchMarker =
        "StillGreenhouses room liquid low wind bridge";

    private const string OverrideDataFolder =
        "StillGreenhouses";

    private const string OverrideOriginFolder =
            "shader-origin-0.18.0";

    private const int MaxFunctionSourceChunkCharacters =
        2200;

    private const int RequiredOverrideAssetCount = 4;
    private const int RequiredCallSiteAssetCount = 3;

    private static readonly ShaderCallSiteAsset[]
        CallSiteAssets =
        [
            new(
                "game:shaders/chunkopaque.vsh",
                1
            ),
            new(
                "game:shaders/chunktransparent.vsh",
                1
            ),
            new(
                "game:shaders/chunkshadowmap.vsh",
                2
            )
        ];

    private const string TopsoilAuditAssetLocation =
        "game:shaders/chunktopsoil.vsh";

    private static readonly ShaderCallSiteAsset[]
        LiquidCallSiteAuditAssets =
        [
            new(
                "game:shaders/chunkliquid.vsh",
                2
            ),
            new(
                "game:shaders/chunkliquiddepth.vsh",
                1
            )
        ];

    private static readonly ConcurrentDictionary<
        string,
        string
    > PreparedOverrideAssetHashes =
        new(StringComparer.Ordinal);

    private static int originPreparationAttempted;
    private static int originPrepared;
    private static int overrideResolved;
    private static int overrideMarkerPresent;
    private static int overrideMatchesPreparedHash;
    private static int originPriorityIndex = -1;
    private static int shaderReloadAttempted;
    private static int shaderReloadSucceeded;
    private static int compiledVerificationPending;
    private static int windSpeedReplacements;
    private static int windWaveCounterReplacements;
    private static int highFreqCounterReplacements;
    private static int functionSourceChunksLogged;
    private static int overrideAssetCount;
    private static int callSiteAssetsPatched;
    private static int callSiteCallsWrapped;
    private static int topsoilActiveVertexWarpCalls;
    private static int topsoilCommentedVertexWarpDetected;

    private static string lastFailureReason =
        "<none>";

    private static string shaderReloadFailureReason =
        "<not-attempted>";

    private static string overrideOriginPath =
        "<none>";

    private static string resolvedAssetOriginPath =
        "<none>";

    private static string baseSourceHash =
        "<none>";

    private static string preparedOverrideSourceHash =
        "<none>";

    private static string resolvedOverrideSourceHash =
        "<none>";

    internal static bool Ready =>
        OverrideResolved;

    internal static bool OriginPrepared =>
        Volatile.Read(
            ref originPrepared
        ) != 0;

    internal static bool OverrideResolved =>
        Volatile.Read(
            ref overrideResolved
        ) != 0;

    internal static bool OverrideMarkerPresent =>
        Volatile.Read(
            ref overrideMarkerPresent
        ) != 0;

    internal static bool OverrideMatchesPreparedHash =>
        Volatile.Read(
            ref overrideMatchesPreparedHash
        ) != 0;

    internal static int OriginPriorityIndex =>
        Volatile.Read(
            ref originPriorityIndex
        );

    internal static bool ShaderReloadAttempted =>
        Volatile.Read(
            ref shaderReloadAttempted
        ) != 0;

    internal static bool ShaderReloadSucceeded =>
        Volatile.Read(
            ref shaderReloadSucceeded
        ) != 0;

    internal static bool CompiledVerificationPending =>
        Volatile.Read(
            ref compiledVerificationPending
        ) != 0;

    internal static int WindSpeedReplacements =>
        Volatile.Read(
            ref windSpeedReplacements
        );

    internal static int WindWaveCounterReplacements =>
        Volatile.Read(
            ref windWaveCounterReplacements
        );

    internal static int HighFreqCounterReplacements =>
        Volatile.Read(
            ref highFreqCounterReplacements
        );

    internal static int RequiredOverrideAssets =>
        RequiredOverrideAssetCount;

    internal static int RequiredCallSiteAssets =>
        RequiredCallSiteAssetCount;

    internal static int OverrideAssetCount =>
        Volatile.Read(
            ref overrideAssetCount
        );

    internal static int CallSiteAssetsPatched =>
        Volatile.Read(
            ref callSiteAssetsPatched
        );

    internal static int CallSiteCallsWrapped =>
        Volatile.Read(
            ref callSiteCallsWrapped
        );

    internal static int TopsoilActiveVertexWarpCalls =>
        Volatile.Read(
            ref topsoilActiveVertexWarpCalls
        );

    internal static bool TopsoilCommentedVertexWarpDetected =>
        Volatile.Read(
            ref topsoilCommentedVertexWarpDetected
        ) != 0;

    internal static string ShaderTopology =>
        "VanillaNativeWarpWithRoomLocalEnvironmentAndCellHash";

    internal static string AbsolutePositionStrategy =>
        "AbsoluteQuarterBlockCellHashWithRenderReferenceReconstruction";

    internal static int FunctionSourceChunksLogged =>
        Volatile.Read(
            ref functionSourceChunksLogged
        );

    internal static string LastFailureReason =>
        Volatile.Read(
            ref lastFailureReason
        );

    internal static string ShaderReloadFailureReason =>
        Volatile.Read(
            ref shaderReloadFailureReason
        );

    internal static string OverrideOriginPath =>
        Volatile.Read(
            ref overrideOriginPath
        );

    internal static string ResolvedAssetOriginPath =>
        Volatile.Read(
            ref resolvedAssetOriginPath
        );

    internal static string BaseSourceHash =>
        Volatile.Read(
            ref baseSourceHash
        );

    internal static string PreparedOverrideSourceHash =>
        Volatile.Read(
            ref preparedOverrideSourceHash
        );

    internal static string ResolvedOverrideSourceHash =>
        Volatile.Read(
            ref resolvedOverrideSourceHash
        );

    internal static bool TryConsumeCompiledVerificationPending() =>
        Interlocked.Exchange(
            ref compiledVerificationPending,
            0
        ) != 0;

    // Start() is before normal asset loading. IAssetManager.Get() is used only
    // for the base game shader include, then a physical game-domain mod origin
    // is registered before the asset manager resolves the final shader source.
    internal static bool PrepareAssetOrigin(
        ICoreClientAPI api
    )
    {
        if (
            Interlocked.Exchange(
                ref originPreparationAttempted,
                1
            ) != 0
        )
        {
            return OriginPrepared;
        }

        Dictionary<string, string> baseSources =
            new(StringComparer.Ordinal);

        Dictionary<string, string> overrides =
            new(StringComparer.Ordinal);

        if (!TryReadAssetSource(
                api,
                TargetAssetLocation,
                out string source,
                out string sourceDiagnostic
            ))
        {
            return PublishOriginPreparationFailure(
                api,
                "base-asset-read-failed; "
                + sourceDiagnostic
            );
        }

        baseSources[TargetAssetLocation] =
            source;

        if (source.Contains(
                PatchMarker,
                StringComparison.Ordinal
            ))
        {
            return PublishOriginPreparationFailure(
                api,
                "base-source-already-contains-stillgreenhouses-marker"
            );
        }

        if (!TryPatchSource(
                source,
                out string patchedSource,
                out string diagnostic,
                out string capturedFunctionSource,
                out int patchedWindSpeedUses,
                out int patchedWindWaveCounterUses,
                out int patchedHighFreqUses
            ))
        {
            int loggedChunks =
                LogPatchFailure(
                    api,
                    "reason=asset-origin-source-patch-failed; "
                    + diagnostic,
                    capturedFunctionSource
                );

            Volatile.Write(
                ref functionSourceChunksLogged,
                loggedChunks
            );

            return PublishOriginPreparationFailure(
                api,
                CompactDiagnostic(diagnostic)
            );
        }

        if (TryPatchLiquidWarping(
                patchedSource,
                out string liquidPatchedSource,
                out string liquidDiagnostic
            ))
        {
            patchedSource =
                liquidPatchedSource;

            api.Logger.Notification(
                "[StillGreenhouses] ROOM WIND LIQUID PATCH APPLIED "
                + liquidDiagnostic
            );
        }
        else
        {
            api.Logger.Warning(
                "[StillGreenhouses] ROOM WIND LIQUID PATCH SKIPPED "
                + liquidDiagnostic
            );
        }

        overrides[TargetAssetLocation] =
            patchedSource;

        int wrappedCallCount = 0;

        foreach (
            ShaderCallSiteAsset callSiteAsset
            in CallSiteAssets
        )
        {
            if (!TryReadAssetSource(
                    api,
                    callSiteAsset.AssetLocation,
                    out string callSiteSource,
                    out string callSiteReadDiagnostic
                ))
            {
                return PublishOriginPreparationFailure(
                    api,
                    "callsite-asset-read-failed; "
                    + $"location={callSiteAsset.AssetLocation}; "
                    + callSiteReadDiagnostic
                );
            }

            baseSources[callSiteAsset.AssetLocation] =
                callSiteSource;

            if (callSiteSource.Contains(
                    CallSitePatchMarker,
                    StringComparison.Ordinal
                ))
            {
                return PublishOriginPreparationFailure(
                    api,
                    "callsite-source-already-contains-stillgreenhouses-marker; "
                    + $"location={callSiteAsset.AssetLocation}"
                );
            }

            if (!TryPatchVertexWarpCallSites(
                    callSiteSource,
                    callSiteAsset.ExpectedCalls,
                    out string patchedCallSiteSource,
                    out string callSiteDiagnostic,
                    out int applyVertexWarpingCalls,
                    out int wrappedCalls
                ))
            {
                return PublishOriginPreparationFailure(
                    api,
                    "callsite-source-patch-failed; "
                    + $"location={callSiteAsset.AssetLocation}; "
                    + callSiteDiagnostic
                );
            }

            overrides[callSiteAsset.AssetLocation] =
                patchedCallSiteSource;

            wrappedCallCount +=
                wrappedCalls;

            api.Logger.Notification(
                "[StillGreenhouses] ROOM WIND SHADER CALLSITE PATCH "
                + $"asset={callSiteAsset.AssetLocation}; "
                + $"applyVertexWarpingCalls={applyVertexWarpingCalls}; "
                + $"wrappedCalls={wrappedCalls}; "
                + $"expectedCalls={callSiteAsset.ExpectedCalls}; "
                + "topology=VanillaNativeWarpWithRoomEnvironmentalInputs; "
                + "positionMatch=worldPos+playerpos/playerReferencePos-relative-wind-vertex-envelopes"
            );
        }

        if (!TryReadAssetSource(
                api,
                TopsoilAuditAssetLocation,
                out string topsoilSource,
                out string topsoilReadDiagnostic
            ))
        {
            return PublishOriginPreparationFailure(
                api,
                "topsoil-audit-asset-read-failed; "
                + topsoilReadDiagnostic
            );
        }

        baseSources[TopsoilAuditAssetLocation] =
            topsoilSource;

        int topsoilActiveCalls =
            CountIdentifierInCode(
                topsoilSource,
                TargetFunctionName
            );

        bool topsoilCommentedCall =
            topsoilActiveCalls == 0
            && topsoilSource.Contains(
                TargetFunctionName,
                StringComparison.Ordinal
            );

        if (topsoilActiveCalls != 0)
        {
            return PublishOriginPreparationFailure(
                api,
                "topsoil-vertex-warp-topology-changed; "
                + $"activeCalls={topsoilActiveCalls}; expected=0"
            );
        }

        api.Logger.Notification(
            "[StillGreenhouses] ROOM WIND SHADER CALLSITE AUDIT "
            + $"asset={TopsoilAuditAssetLocation}; "
            + $"applyVertexWarpingCalls={topsoilActiveCalls}; "
            + $"commentedCallDetected={topsoilCommentedCall}; "
            + "action=include-only-skip; "
            + "activeVegetationConsumer=False"
        );

        foreach (
            ShaderCallSiteAsset liquidAuditAsset
            in LiquidCallSiteAuditAssets
        )
        {
            if (!TryReadAssetSource(
                    api,
                    liquidAuditAsset.AssetLocation,
                    out string liquidCallSiteSource,
                    out string liquidCallSiteDiagnostic
                ))
            {
                api.Logger.Warning(
                    "[StillGreenhouses] ROOM WIND SHADER LIQUID CALLSITE AUDIT "
                    + $"asset={liquidAuditAsset.AssetLocation}; "
                    + "available=False; "
                    + liquidCallSiteDiagnostic
                );

                continue;
            }

            baseSources[
                liquidAuditAsset.AssetLocation
            ] =
                liquidCallSiteSource;

            int liquidWarpCalls =
                CountIdentifierInCode(
                    liquidCallSiteSource,
                    TargetLiquidFunctionName
                );

            api.Logger.Notification(
                "[StillGreenhouses] ROOM WIND SHADER LIQUID CALLSITE AUDIT "
                + $"asset={liquidAuditAsset.AssetLocation}; "
                + $"applyLiquidWarpingCalls={liquidWarpCalls}; "
                + $"expectedCalls={liquidAuditAsset.ExpectedCalls}; "
                + $"matchesExpected={liquidWarpCalls == liquidAuditAsset.ExpectedCalls}; "
                + "action=include-function-patch"
            );
        }

        string dataRoot;
        string originRoot;

        PreparedOverrideAssetHashes.Clear();

        try
        {
            dataRoot =
                api.GetOrCreateDataPath(
                    OverrideDataFolder
                );

            originRoot =
                Path.Combine(
                    dataRoot,
                    OverrideOriginFolder
                );

            if (Directory.Exists(originRoot))
            {
                Directory.Delete(
                    originRoot,
                    recursive: true
                );
            }

            foreach (
                KeyValuePair<string, string> pair
                in overrides.OrderBy(
                    pair => pair.Key,
                    StringComparer.Ordinal
                )
            )
            {
                string overrideFile =
                    Path.Combine(
                        originRoot,
                        GetAssetRelativePath(
                            pair.Key
                        )
                    );

                Directory.CreateDirectory(
                    Path.GetDirectoryName(overrideFile)!
                );

                File.WriteAllText(
                    overrideFile,
                    pair.Value,
                    new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false
                    )
                );

                string persistedSource =
                    File.ReadAllText(
                        overrideFile,
                        Encoding.UTF8
                    );

                string requiredMarker =
                    pair.Key == TargetAssetLocation
                        ? PatchMarker
                        : CallSitePatchMarker;

                if (
                    !persistedSource.Contains(
                        requiredMarker,
                        StringComparison.Ordinal
                    )
                    || !string.Equals(
                        persistedSource,
                        pair.Value,
                        StringComparison.Ordinal
                    )
                )
                {
                    return PublishOriginPreparationFailure(
                        api,
                        "override-file-readback-mismatch; "
                        + $"location={pair.Key}"
                    );
                }

                PreparedOverrideAssetHashes[
                    pair.Key
                ] =
                    ComputeStableSourceHash(
                        persistedSource
                    );
            }
        }
        catch (Exception e)
        {
            return PublishOriginPreparationFailure(
                api,
                "override-file-write-or-readback-failed; "
                + $"error={e.GetType().Name}:{Sanitize(e.Message)}"
            );
        }

        if (
            overrides.Count
                != RequiredOverrideAssetCount
            || PreparedOverrideAssetHashes.Count
                != RequiredOverrideAssetCount
        )
        {
            return PublishOriginPreparationFailure(
                api,
                "override-asset-count-invalid; "
                + $"overrides={overrides.Count}; "
                + $"hashes={PreparedOverrideAssetHashes.Count}; "
                + $"expected={RequiredOverrideAssetCount}"
            );
        }

        try
        {
            api.Assets.AddModOrigin(
                "game",
                originRoot
            );
        }
        catch (Exception e)
        {
            return PublishOriginPreparationFailure(
                api,
                "add-mod-origin-failed; "
                + $"error={e.GetType().Name}:{Sanitize(e.Message)}"
            );
        }

        int priorityIndex =
            FindOriginPriorityIndex(
                api,
                originRoot
            );

        Volatile.Write(
            ref overrideOriginPath,
            originRoot
        );

        Volatile.Write(
            ref baseSourceHash,
            ComputeSourceSetHash(
                baseSources
            )
        );

        Volatile.Write(
            ref preparedOverrideSourceHash,
            ComputeSourceSetHash(
                overrides
            )
        );

        Volatile.Write(
            ref originPriorityIndex,
            priorityIndex
        );

        Volatile.Write(
            ref windSpeedReplacements,
            patchedWindSpeedUses
        );

        Volatile.Write(
            ref windWaveCounterReplacements,
            patchedWindWaveCounterUses
        );

        Volatile.Write(
            ref highFreqCounterReplacements,
            patchedHighFreqUses
        );

        Volatile.Write(
            ref overrideAssetCount,
            overrides.Count
        );

        Volatile.Write(
            ref callSiteAssetsPatched,
            CallSiteAssets.Length
        );

        Volatile.Write(
            ref callSiteCallsWrapped,
            wrappedCallCount
        );

        Volatile.Write(
            ref topsoilActiveVertexWarpCalls,
            topsoilActiveCalls
        );

        Volatile.Write(
            ref topsoilCommentedVertexWarpDetected,
            topsoilCommentedCall ? 1 : 0
        );

        Volatile.Write(
            ref lastFailureReason,
            "<none>"
        );

        Volatile.Write(
            ref originPrepared,
            1
        );

        api.Logger.Notification(
            "[StillGreenhouses] ROOM WIND SHADER ORIGIN PREPARED "
            + $"location={TargetAssetLocation}; "
            + "strategy=vanilla-native-warp-with-room-environmental-inputs; "
            + "positionMatch=bounded-quarter-block-texture-hash; "
            + $"originPath={originRoot}; "
            + $"originPriorityIndex={priorityIndex}; "
            + $"baseSourceSetHash={BaseSourceHash}; "
            + $"overrideSourceSetHash={PreparedOverrideSourceHash}; "
            + $"overrideAssets={overrides.Count}/{RequiredOverrideAssetCount}; "
            + $"callSiteAssets={CallSiteAssets.Length}/{RequiredCallSiteAssetCount}; "
            + $"wrappedCalls={wrappedCallCount}; "
            + $"topsoilActiveWarpCalls={topsoilActiveCalls}; "
            + $"topsoilCommentedCallDetected={topsoilCommentedCall}; "
            + $"windSpeedUses={patchedWindSpeedUses}; "
            + $"windWaveCounterUses={patchedWindWaveCounterUses}; "
            + $"highFreqCounterUses={patchedHighFreqUses}; "
            + $"hashProbeLimit={RoomWindCellHash.ProbeLimit}; "
            + $"roomTypeStateCount={StillGreenhousesRoomWindEnvironment.RoomTypeStateCount}"
        );

        return true;
    }

    internal static bool VerifyResolvedOverrideAsset(
        ICoreAPI api,
        string stage
    )
    {
        if (
            PreparedOverrideAssetHashes.Count
                != RequiredOverrideAssetCount
        )
        {
            return PublishResolvedOverrideFailure(
                api,
                stage,
                "prepared-override-asset-manifest-invalid; "
                + $"count={PreparedOverrideAssetHashes.Count}; "
                + $"expected={RequiredOverrideAssetCount}"
            );
        }

        Dictionary<string, string> resolvedSources =
            new(StringComparer.Ordinal);

        bool allMarkersPresent = true;
        bool allAssetHashesMatch = true;
        bool allOriginsMatch = true;
        string primaryAssetOriginPath =
            "<none>";

        foreach (
            KeyValuePair<string, string> expected
            in PreparedOverrideAssetHashes
                .OrderBy(
                    pair => pair.Key,
                    StringComparer.Ordinal
                )
        )
        {
            IAsset? asset;

            try
            {
                asset =
                    api.Assets.TryGet(
                        new AssetLocation(
                            expected.Key
                        ),
                        loadAsset: true
                    );
            }
            catch (Exception e)
            {
                return PublishResolvedOverrideFailure(
                    api,
                    stage,
                    "resolved-asset-read-failed; "
                    + $"location={expected.Key}; "
                    + $"error={e.GetType().Name}:{Sanitize(e.Message)}"
                );
            }

            if (asset == null)
            {
                return PublishResolvedOverrideFailure(
                    api,
                    stage,
                    "resolved-asset-null; "
                    + $"location={expected.Key}"
                );
            }

            string source;

            try
            {
                source =
                    asset.ToText();
            }
            catch (Exception e)
            {
                return PublishResolvedOverrideFailure(
                    api,
                    stage,
                    "resolved-asset-text-read-failed; "
                    + $"location={expected.Key}; "
                    + $"error={e.GetType().Name}:{Sanitize(e.Message)}"
                );
            }

            string sourceHash =
                ComputeStableSourceHash(
                    source
                );

            string requiredMarker =
                expected.Key == TargetAssetLocation
                    ? PatchMarker
                    : CallSitePatchMarker;

            bool markerPresent =
                source.Contains(
                    requiredMarker,
                    StringComparison.Ordinal
                );

            bool assetHashMatches =
                string.Equals(
                    sourceHash,
                    expected.Value,
                    StringComparison.Ordinal
                );

            string assetOriginPath =
                asset.Origin?.OriginPath
                ?? "<none>";

            bool originMatchesRegistered =
                PathsEqual(
                    assetOriginPath,
                    OverrideOriginPath
                );

            if (
                expected.Key
                    == TargetAssetLocation
            )
            {
                primaryAssetOriginPath =
                    assetOriginPath;
            }

            allMarkersPresent &=
                markerPresent;

            allAssetHashesMatch &=
                assetHashMatches;

            allOriginsMatch &=
                originMatchesRegistered;

            resolvedSources[expected.Key] =
                source;

            api.Logger.Notification(
                "[StillGreenhouses] ROOM WIND SHADER OVERRIDE ASSET "
                + $"stage={stage}; "
                + $"location={expected.Key}; "
                + $"markerPresent={markerPresent}; "
                + $"sourceHash={sourceHash}; "
                + $"preparedHash={expected.Value}; "
                + $"matchesPreparedHash={assetHashMatches}; "
                + $"resolvedOriginPath={assetOriginPath}; "
                + $"originMatchesRegistered={originMatchesRegistered}"
            );
        }

        string resolvedSourceSetHash =
            ComputeSourceSetHash(
                resolvedSources
            );

        bool matchesPreparedSourceSetHash =
            string.Equals(
                resolvedSourceSetHash,
                PreparedOverrideSourceHash,
                StringComparison.Ordinal
            );

        bool resolved =
            OriginPrepared
            && resolvedSources.Count
                == RequiredOverrideAssetCount
            && allMarkersPresent
            && allAssetHashesMatch
            && allOriginsMatch
            && matchesPreparedSourceSetHash;

        Volatile.Write(
            ref resolvedAssetOriginPath,
            primaryAssetOriginPath
        );

        Volatile.Write(
            ref resolvedOverrideSourceHash,
            resolvedSourceSetHash
        );

        Volatile.Write(
            ref overrideMarkerPresent,
            allMarkersPresent ? 1 : 0
        );

        Volatile.Write(
            ref overrideMatchesPreparedHash,
            (
                allAssetHashesMatch
                && matchesPreparedSourceSetHash
            )
                ? 1
                : 0
        );

        Volatile.Write(
            ref overrideResolved,
            resolved ? 1 : 0
        );

        if (!resolved)
        {
            Volatile.Write(
                ref lastFailureReason,
                "resolved-shader-asset-set-is-not-stillgreenhouses-origin"
            );
        }

        api.Logger.Notification(
            "[StillGreenhouses] ROOM WIND SHADER OVERRIDE RESOLVED "
            + $"stage={stage}; "
            + $"location={TargetAssetLocation}; "
            + $"originPrepared={OriginPrepared}; "
            + $"overrideAssets={resolvedSources.Count}/{RequiredOverrideAssetCount}; "
            + $"allMarkersPresent={allMarkersPresent}; "
            + $"sourceSetHash={resolvedSourceSetHash}; "
            + $"preparedSourceSetHash={PreparedOverrideSourceHash}; "
            + $"matchesPreparedSourceSetHash={matchesPreparedSourceSetHash}; "
            + $"allAssetHashesMatch={allAssetHashesMatch}; "
            + $"resolvedOriginPath={primaryAssetOriginPath}; "
            + $"registeredOriginPath={OverrideOriginPath}; "
            + $"allOriginsMatch={allOriginsMatch}; "
            + $"originPriorityIndex={OriginPriorityIndex}; "
            + $"resolved={resolved}"
        );

        return resolved;
    }

    // The shader origin is registered during Start(), before normal asset
    // resolution. Once BlockTexturesLoaded confirms that all resolved shader
    // assets match that origin, no global shader reload is required.
    //
    // Calling IShaderAPI.ReloadShaders() here used to rebuild every engine
    // program during login. On mod-heavy clients this can leave animated
    // programs without their native Animation UBO metadata, after which
    // AnimatableRenderer crashes while indexing prog.UBOs["Animation"].
    // Compiled bridge verification remains authoritative: if the engine did
    // not compile the prepared override, the room-wind environment stays
    // inactive and safely falls back to Vanilla rendering.
    internal static bool FinalizePreparedShaderLifecycle(
        ICoreClientAPI api,
        string stage
    )
    {
        if (
            Interlocked.Exchange(
                ref shaderReloadAttempted,
                1
            ) != 0
        )
        {
            return ShaderReloadSucceeded;
        }

        if (!Ready)
        {
            Volatile.Write(
                ref shaderReloadSucceeded,
                0
            );

            Volatile.Write(
                ref shaderReloadFailureReason,
                "shader-override-not-resolved"
            );

            api.Logger.Notification(
                "[StillGreenhouses] ROOM WIND SHADER LIFECYCLE " +
                $"stage={stage}; " +
                "globalReload=False; " +
                "ready=False; " +
                $"originPrepared={OriginPrepared}; " +
                $"overrideResolved={OverrideResolved}; " +
                $"sourceHash={ResolvedOverrideSourceHash}; " +
                "reason=shader-override-not-resolved"
            );

            return false;
        }

        Volatile.Write(
            ref shaderReloadSucceeded,
            1
        );

        Volatile.Write(
            ref shaderReloadFailureReason,
            "<global-reload-not-required>"
        );

        Volatile.Write(
            ref compiledVerificationPending,
            1
        );

        api.Logger.Notification(
            "[StillGreenhouses] ROOM WIND SHADER LIFECYCLE " +
            $"stage={stage}; " +
            "globalReload=False; " +
            "ready=True; " +
            $"originPrepared={OriginPrepared}; " +
            $"overrideResolved={OverrideResolved}; " +
            $"sourceHash={ResolvedOverrideSourceHash}; " +
            "reason=early-asset-origin-already-resolved"
        );

        return true;
    }

    internal static void RemoveAssetOrigin(
        ICoreAPI api
    )
    {
        string originPath =
            OverrideOriginPath;

        if (originPath == "<none>")
        {
            ResetClientLifecycleState();

            return;
        }

        try
        {
            api.Assets.RemoveModOrigin(
                "game",
                originPath
            );
        }
        catch
        {
            // Disposal must not fail because the asset manager is already
            // shutting down or the origin has already been removed.
        }
        finally
        {
            ResetClientLifecycleState();
        }
    }

    private static void ResetClientLifecycleState()
    {
        PreparedOverrideAssetHashes.Clear();

        Volatile.Write(ref originPreparationAttempted, 0);
        Volatile.Write(ref originPrepared, 0);
        Volatile.Write(ref overrideResolved, 0);
        Volatile.Write(ref overrideMarkerPresent, 0);
        Volatile.Write(ref overrideMatchesPreparedHash, 0);
        Volatile.Write(ref originPriorityIndex, -1);
        Volatile.Write(ref shaderReloadAttempted, 0);
        Volatile.Write(ref shaderReloadSucceeded, 0);
        Volatile.Write(ref compiledVerificationPending, 0);
        Volatile.Write(ref windSpeedReplacements, 0);
        Volatile.Write(ref windWaveCounterReplacements, 0);
        Volatile.Write(ref highFreqCounterReplacements, 0);
        Volatile.Write(ref functionSourceChunksLogged, 0);
        Volatile.Write(ref overrideAssetCount, 0);
        Volatile.Write(ref callSiteAssetsPatched, 0);
        Volatile.Write(ref callSiteCallsWrapped, 0);
        Volatile.Write(ref topsoilActiveVertexWarpCalls, 0);
        Volatile.Write(ref topsoilCommentedVertexWarpDetected, 0);

        Volatile.Write(ref lastFailureReason, "<none>");
        Volatile.Write(ref shaderReloadFailureReason, "<not-attempted>");
        Volatile.Write(ref overrideOriginPath, "<none>");
        Volatile.Write(ref resolvedAssetOriginPath, "<none>");
        Volatile.Write(ref baseSourceHash, "<none>");
        Volatile.Write(ref preparedOverrideSourceHash, "<none>");
        Volatile.Write(ref resolvedOverrideSourceHash, "<none>");
    }

    private static bool PublishOriginPreparationFailure(
        ICoreAPI api,
        string reason
    )
    {
        Volatile.Write(
            ref originPrepared,
            0
        );

        Volatile.Write(
            ref overrideResolved,
            0
        );

        Volatile.Write(
            ref lastFailureReason,
            CompactDiagnostic(reason)
        );

        api.Logger.Warning(
            "[StillGreenhouses] ROOM WIND SHADER ORIGIN PREPARE FAILED " +
            $"location={TargetAssetLocation}; " +
            $"reason={reason}"
        );

        return false;
    }

    private static bool PublishResolvedOverrideFailure(
        ICoreAPI api,
        string stage,
        string reason
    )
    {
        Volatile.Write(
            ref overrideResolved,
            0
        );

        Volatile.Write(
            ref overrideMarkerPresent,
            0
        );

        Volatile.Write(
            ref overrideMatchesPreparedHash,
            0
        );

        Volatile.Write(
            ref lastFailureReason,
            CompactDiagnostic(reason)
        );

        api.Logger.Warning(
            "[StillGreenhouses] ROOM WIND SHADER OVERRIDE RESOLVED " +
            $"stage={stage}; " +
            $"location={TargetAssetLocation}; " +
            "resolved=False; " +
            $"reason={reason}"
        );

        return false;
    }

    private static bool TryReadAssetSource(
        ICoreAPI api,
        string assetLocation,
        out string source,
        out string diagnostic
    )
    {
        source =
            string.Empty;

        try
        {
            IAsset asset =
                api.Assets.Get(
                    new AssetLocation(
                        assetLocation
                    )
                );

            source =
                asset.ToText();

            diagnostic =
                "reason=<none>";

            return true;
        }
        catch (Exception e)
        {
            diagnostic =
                $"error={e.GetType().Name}:{Sanitize(e.Message)}";

            return false;
        }
    }

    private static string GetAssetRelativePath(
        string assetLocation
    )
    {
        int colon =
            assetLocation.IndexOf(
                ':'
            );

        string relative =
            colon >= 0
                ? assetLocation[
                    (colon + 1)..
                ]
                : assetLocation;

        return relative.Replace(
            '/',
            Path.DirectorySeparatorChar
        );
    }

    private static string ComputeSourceSetHash(
        IReadOnlyDictionary<string, string> sources
    )
    {
        StringBuilder manifest =
            new();

        foreach (
            KeyValuePair<string, string> pair
            in sources.OrderBy(
                pair => pair.Key,
                StringComparer.Ordinal
            )
        )
        {
            manifest.Append(
                pair.Key
            );

            manifest.Append(
                '\n'
            );

            manifest.Append(
                ComputeStableSourceHash(
                    pair.Value
                )
            );

            manifest.Append(
                '\n'
            );
        }

        return ComputeStableSourceHash(
            manifest.ToString()
        );
    }

    private static bool TryPatchVertexWarpCallSites(
        string source,
        int expectedCallCount,
        out string patchedSource,
        out string diagnostic,
        out int applyVertexWarpingCalls,
        out int wrappedCalls
    )
    {
        patchedSource =
            source;

        diagnostic =
            string.Empty;

        applyVertexWarpingCalls =
            0;

        wrappedCalls =
            0;

        if (source.Contains(
                CallSitePatchMarker,
                StringComparison.Ordinal
            ))
        {
            diagnostic =
                "reason=callsite-marker-already-present";

            return false;
        }

        List<int> identifiers =
            FindIdentifierPositionsInCode(
                source,
                TargetFunctionName
            );

        List<SourceRewrite> rewrites =
            new();

        foreach (
            int identifierIndex
            in identifiers
        )
        {
            int openParen =
                FindNextNonWhitespace(
                    source,
                    identifierIndex
                        + TargetFunctionName.Length
                );

            if (
                openParen < 0
                || source[openParen] != '('
            )
            {
                continue;
            }

            int closeParen =
                FindMatchingDelimiter(
                    source,
                    openParen,
                    '(',
                    ')'
                );

            if (closeParen < 0)
            {
                diagnostic =
                    "reason=callsite-closing-parenthesis-not-found";

                return false;
            }

            int statementStart =
                FindStatementStart(
                    source,
                    identifierIndex
                );

            int semicolon =
                FindNextCodeCharacter(
                    source,
                    closeParen + 1,
                    ';'
                );

            if (semicolon < 0)
            {
                diagnostic =
                    "reason=callsite-semicolon-not-found";

                return false;
            }

            string statement =
                source.Substring(
                    statementStart,
                    semicolon
                        - statementStart
                        + 1
                );

            if (
                !statement.Contains(
                    "worldPos",
                    StringComparison.Ordinal
                )
                || !statement.Contains(
                    '='
                )
            )
            {
                continue;
            }

            string argumentSource =
                source.Substring(
                    openParen + 1,
                    closeParen
                        - openParen
                        - 1
                );

            List<string> arguments =
                SplitTopLevelArguments(
                    argumentSource
                );

            if (arguments.Count != 2)
            {
                diagnostic =
                    "reason=applyVertexWarping-argument-count-invalid; "
                    + $"arguments={arguments.Count}; expected=2";

                return false;
            }

            string originalCall =
                source.Substring(
                    identifierIndex,
                    closeParen
                        - identifierIndex
                        + 1
                );

            string replacement =
                CallSiteWrapperFunctionName
                + "("
                + arguments[0].Trim()
                + ", "
                + arguments[1].Trim()
                + ", "
                + originalCall
                + ")";

            rewrites.Add(
                new SourceRewrite(
                    identifierIndex,
                    closeParen + 1,
                    replacement
                )
            );
        }

        applyVertexWarpingCalls =
            rewrites.Count;

        if (
            applyVertexWarpingCalls
                != expectedCallCount
        )
        {
            diagnostic =
                "reason=applyVertexWarping-call-count-invalid; "
                + $"found={applyVertexWarpingCalls}; "
                + $"expected={expectedCallCount}; "
                + $"identifierOccurrences={identifiers.Count}";

            return false;
        }

        foreach (
            SourceRewrite rewrite
            in rewrites
                .OrderByDescending(
                    rewrite => rewrite.Start
                )
        )
        {
            patchedSource =
                patchedSource.Remove(
                    rewrite.Start,
                    rewrite.EndExclusive
                        - rewrite.Start
                )
                .Insert(
                    rewrite.Start,
                    rewrite.Replacement
                );
        }

        int firstLineEnd =
            patchedSource.IndexOf(
                '\n'
            );

        if (firstLineEnd < 0)
        {
            diagnostic =
                "reason=shader-first-line-not-found";

            return false;
        }

        patchedSource =
            patchedSource.Insert(
                firstLineEnd + 1,
                "// "
                + CallSitePatchMarker
                + "\n"
            );

        wrappedCalls =
            CountIdentifierInCode(
                patchedSource,
                CallSiteWrapperFunctionName
            );

        int remainingVanillaCalls =
            CountIdentifierInCode(
                patchedSource,
                TargetFunctionName
            );

        if (
            wrappedCalls
                != expectedCallCount
            || remainingVanillaCalls
                != expectedCallCount
            || !patchedSource.Contains(
                CallSitePatchMarker,
                StringComparison.Ordinal
            )
        )
        {
            diagnostic =
                "reason=callsite-post-patch-verification-failed; "
                + $"wrappedCalls={wrappedCalls}; "
                + $"vanillaCalls={remainingVanillaCalls}; "
                + $"expected={expectedCallCount}";

            return false;
        }

        diagnostic =
            "reason=<none>; "
            + "topology=VanillaNativeWarpWithRoomEnvironmentalInputs";

        return true;
    }

    private static List<string> SplitTopLevelArguments(
        string source
    )
    {
        List<string> arguments =
            new();

        int segmentStart =
            0;

        int parenthesisDepth =
            0;

        int bracketDepth =
            0;

        int braceDepth =
            0;

        for (
            int i = 0;
            i < source.Length;
            i++
        )
        {
            char current =
                source[i];

            switch (current)
            {
                case '(':
                    parenthesisDepth++;
                    break;

                case ')':
                    parenthesisDepth--;
                    break;

                case '[':
                    bracketDepth++;
                    break;

                case ']':
                    bracketDepth--;
                    break;

                case '{':
                    braceDepth++;
                    break;

                case '}':
                    braceDepth--;
                    break;

                case ',':
                    if (
                        parenthesisDepth == 0
                        && bracketDepth == 0
                        && braceDepth == 0
                    )
                    {
                        arguments.Add(
                            source.Substring(
                                segmentStart,
                                i - segmentStart
                            )
                        );

                        segmentStart =
                            i + 1;
                    }

                    break;
            }
        }

        arguments.Add(
            source[
                segmentStart..
            ]
        );

        return arguments;
    }

    private static int FindOriginPriorityIndex(
        ICoreAPI api,
        string originPath
    )
    {
        List<IAssetOrigin> origins =
            api.Assets.Origins;

        for (int i = 0; i < origins.Count; i++)
        {
            if (PathsEqual(
                    origins[i].OriginPath,
                    originPath
                ))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool PathsEqual(
        string? left,
        string? right
    )
    {
        if (
            string.IsNullOrWhiteSpace(left)
            || string.IsNullOrWhiteSpace(right)
        )
        {
            return false;
        }

        try
        {
            string normalizedLeft =
                Path.GetFullPath(left)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar
                    );

            string normalizedRight =
                Path.GetFullPath(right)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar
                    );

            return string.Equals(
                normalizedLeft,
                normalizedRight,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            );
        }
        catch
        {
            return string.Equals(
                left,
                right,
                StringComparison.Ordinal
            );
        }
    }

    private static string ComputeStableSourceHash(
        string source
    )
    {
        byte[] bytes =
            Encoding.UTF8.GetBytes(source);

        ulong hash =
            14695981039346656037UL;

        unchecked
        {
            foreach (byte value in bytes)
            {
                hash ^= value;
                hash *= 1099511628211UL;
            }
        }

        return hash.ToString(
            "X16",
            CultureInfo.InvariantCulture
        );
    }

    internal static bool TryPatchVertexWarpSourceForChecks(
        string source,
        out string patchedSource,
        out string diagnostic
    )
    {
        if (!TryPatchSource(
                source,
                out patchedSource,
                out diagnostic,
                out _,
                out _,
                out _,
                out _
            ))
        {
            return false;
        }

        if (!TryPatchLiquidWarping(
                patchedSource,
                out string liquidPatchedSource,
                out string liquidDiagnostic
            ))
        {
            diagnostic += "; liquid=" + liquidDiagnostic;
            return false;
        }

        patchedSource = liquidPatchedSource;
        diagnostic += "; liquid=" + liquidDiagnostic;
        return true;
    }

    private static bool TryPatchSource(
        string source,
        out string patchedSource,
        out string diagnostic,
        out string capturedFunctionSource,
        out int patchedWindSpeedUses,
        out int patchedWindWaveCounterUses,
        out int patchedHighFreqUses
    )
    {
        patchedSource = source;
        diagnostic = string.Empty;
        capturedFunctionSource = string.Empty;
        patchedWindSpeedUses = 0;
        patchedWindWaveCounterUses = 0;
        patchedHighFreqUses = 0;

        if (!TryFindUniqueFunctionRange(
                source,
                TargetFunctionName,
                out SourceRange functionRange,
                out diagnostic
            ))
        {
            return false;
        }

        string functionSource =
            source.Substring(
                functionRange.Start,
                functionRange.Length
            );

        capturedFunctionSource =
            functionSource;

        if (!TryFindUniqueStatementByIdentifier(
                functionSource,
                "windMode",
                new[]
                {
                    "int",
                    "renderFlags",
                    "WindModePosition",
                    "0xF"
                },
                out _,
                out string windModeDiagnostic
            ))
        {
            diagnostic =
                "reason=windMode-declaration-not-unambiguous; "
                + windModeDiagnostic;

            return false;
        }

        if (!TryFindUniqueStatementByIdentifier(
                functionSource,
                "windData",
                new[]
                {
                    "int",
                    "renderFlags",
                    "WindDataPosition",
                    "0x7"
                },
                out SourceRange windDataStatement,
                out string windDataDiagnostic
            ))
        {
            diagnostic =
                "reason=windData-declaration-not-unambiguous; "
                + windDataDiagnostic;

            return false;
        }

        if (!TryFindUniqueWindModeSwitch(
                functionSource,
                out int windModeSwitchIndex,
                out string switchDiagnostic
            ))
        {
            diagnostic =
                "reason=windMode-switch-not-unambiguous; "
                + switchDiagnostic;

            return false;
        }

        int windModeSwitchOpenParen =
            FindNextNonWhitespace(
                functionSource,
                windModeSwitchIndex + "switch".Length
            );

        int windModeSwitchCloseParen =
            windModeSwitchOpenParen < 0
                ? -1
                : FindMatchingDelimiter(
                    functionSource,
                    windModeSwitchOpenParen,
                    '(',
                    ')'
                );

        int windModeSwitchOpenBrace =
            windModeSwitchCloseParen < 0
                ? -1
                : FindNextNonWhitespace(
                    functionSource,
                    windModeSwitchCloseParen + 1
                );

        int windModeSwitchCloseBrace =
            windModeSwitchOpenBrace < 0
                || functionSource[windModeSwitchOpenBrace] != '{'
                ? -1
                : FindMatchingDelimiter(
                    functionSource,
                    windModeSwitchOpenBrace,
                    '{',
                    '}'
                );

        if (
            windModeSwitchOpenParen < 0
            || windModeSwitchCloseParen < 0
            || windModeSwitchOpenBrace < 0
            || windModeSwitchCloseBrace < 0
        )
        {
            diagnostic =
                "reason=windMode-switch-delimiters-not-found";

            return false;
        }

        if (
            windDataStatement.EndExclusive
                >= windModeSwitchIndex
        )
        {
            diagnostic =
                "reason=shader-landmark-order-invalid; "
                + $"windDataEnd={windDataStatement.EndExclusive}; "
                + $"switchIndex={windModeSwitchIndex}";

            return false;
        }

        string sourceAfterWindData =
            functionSource[
                windDataStatement.EndExclusive..
            ];

        int windSpeedUses =
            CountIdentifierInCode(
                sourceAfterWindData,
                "windSpeed"
            );

        int windWaveCounterUses =
            CountIdentifierInCode(
                sourceAfterWindData,
                "windWaveCounter"
            );

        int highFreqUses =
            CountIdentifierInCode(
                sourceAfterWindData,
                "windWaveCounterHighFreq"
            );

        if (
            windSpeedUses < 1
            || windWaveCounterUses < 1
            || highFreqUses < 1
        )
        {
            diagnostic =
                "reason=required-wind-identifiers-not-found-after-windData; "
                + $"windSpeed={windSpeedUses}; "
                + $"windWaveCounter={windWaveCounterUses}; "
                + $"windWaveCounterHighFreq={highFreqUses}";

            return false;
        }

        string indent =
            GetLineIndent(
                functionSource,
                windDataStatement.Start
            );

        string environmentBlock =
            "\n"
            + indent
            + "// "
            + PatchMarker
            + "\n"
            + indent
            + "vec4 stillGreenhousesRoomState = vec4(0.0);\n"
            + indent
            + "int stillGreenhousesTargetFlag = "
            + "windMode == 6 ? 2 : 1;\n"
            + indent
            + "bool stillGreenhousesRoomWind = (windMode != 0) &&\n"
            + indent
            + "    "
            + "stillGreenhousesResolveRoomWindState("
            + "worldPos, stillGreenhousesTargetFlag, "
            + "stillGreenhousesRoomState);\n"
            + indent
            + "float stillGreenhousesWindSpeed = "
            + "stillGreenhousesRoomWind ? "
            + "stillGreenhousesRoomState.x : windSpeed;\n"
            + indent
            + "float stillGreenhousesWindWaveCounter = "
            + "stillGreenhousesRoomWind ? "
            + "stillGreenhousesRegularWindPhase("
            + "stillGreenhousesRoomState) : windWaveCounter;\n"
            + indent
            + "float stillGreenhousesWindWaveCounterHighFreq = "
            + "stillGreenhousesRoomWind ? "
            + "stillGreenhousesHighFrequencyWindPhase("
            + "stillGreenhousesRoomState) : windWaveCounterHighFreq;\n";

        string rewrittenFunction =
            functionSource.Insert(
                windDataStatement.EndExclusive,
                environmentBlock
            );

        int environmentEnd =
            windDataStatement.EndExclusive
            + environmentBlock.Length;

        int modeCorrectionIndex =
            windModeSwitchCloseBrace
            + environmentBlock.Length
            + 1;

        const string vanillaModeSixDelta =
            "worldPos.y += gnoise(noisepos) / 10;";

        int modeSixDeltaIndex = rewrittenFunction.IndexOf(
            vanillaModeSixDelta,
            StringComparison.Ordinal
        );

        if (modeSixDeltaIndex < 0)
        {
            diagnostic = "reason=wind-mode-6-water-delta-not-found";
            return false;
        }

        const string managedModeSixDelta =
            "float stillGreenhousesModeSixAmplitude = "
            + "stillGreenhousesRoomWind ? "
            + "(stillGreenhousesRoomState.w < 0.0 ? 0.0 : "
            + "0.75 + 0.9 * clamp(stillGreenhousesRoomState.x, 0.0, 2.0)) "
            + ": 1.0;\n\t\t\tworldPos.y += "
            + "stillGreenhousesModeSixAmplitude * gnoise(noisepos) / 10;";

        rewrittenFunction = rewrittenFunction.Remove(
            modeSixDeltaIndex,
            vanillaModeSixDelta.Length
        ).Insert(
            modeSixDeltaIndex,
            managedModeSixDelta
        );

        string switchIndent =
            GetLineIndent(
                functionSource,
                windModeSwitchIndex
            );

        string modeCorrectionBlock =
            "\n"
            + switchIndent
            + "// "
            + ModeAwareCorrectionMarker
            + "\n"
            + switchIndent
            + "if (stillGreenhousesRoomWind) {\n"
            + switchIndent
            + "    bool stillGreenhousesNoWind = "
            + "stillGreenhousesRoomState.w < 0.0;\n"
            + switchIndent
            + "    float stillGreenhousesRoomAmplitude = "
            + "clamp(stillGreenhousesWindSpeed, 0.0, 2.0);\n"
            + switchIndent
            + "    float stillGreenhousesSharedStrengthScale = "
            + "(2.0 * stillGreenhousesRoomAmplitude) / "
            + "max(0.0001, 1.0 + stillGreenhousesRoomAmplitude);\n"
            + switchIndent
            + "    if (stillGreenhousesNoWind) {\n"
            + switchIndent
            + "        strength = 0.0;\n"
            + switchIndent
            + "        heightBend = 0.0;\n"
            + switchIndent
            + "    } else if (windMode == 3 || windMode == 10) {\n"
            + switchIndent
            + "        strength *= stillGreenhousesSharedStrengthScale;\n"
            + switchIndent
            + "        heightBend *= stillGreenhousesRoomAmplitude;\n"
            + switchIndent
            + "    } else if (windMode == 8 || windMode == 9) {\n"
            + switchIndent
            + "        strength *= stillGreenhousesSharedStrengthScale;\n"
            + switchIndent
            + "    } else if (windMode == 4 || windMode == 5) {\n"
            + switchIndent
            + "        heightBend *= stillGreenhousesRoomAmplitude;\n"
            + switchIndent
            + "    } else if (windMode == 7) {\n"
            + switchIndent
            + "        strength *= stillGreenhousesRoomAmplitude;\n"
            + switchIndent
            + "        heightBend *= stillGreenhousesRoomAmplitude;\n"
            + switchIndent
            + "    } else if (windMode == 11) {\n"
            + switchIndent
            + "        strength *= (0.015 * stillGreenhousesRoomAmplitude) / "
            + "max(0.0001, 0.013 + 0.002 * stillGreenhousesRoomAmplitude);\n"
            + switchIndent
            + "        heightBend *= stillGreenhousesRoomAmplitude;\n"
            + switchIndent
            + "    }\n"
            + switchIndent
            + "}\n";

        // Insert mode-aware correction while source indices still correspond to
        // the original Vanilla function. Identifier remapping below lengthens
        // several tokens and would invalidate the switch-close index.
        rewrittenFunction =
            rewrittenFunction.Insert(
                modeCorrectionIndex,
                modeCorrectionBlock
            );

        string functionPrefix =
            rewrittenFunction[..environmentEnd];

        string functionSuffix =
            rewrittenFunction[environmentEnd..];

        functionSuffix =
            ReplaceIdentifierInCode(
                functionSuffix,
                "windWaveCounterHighFreq",
                "stillGreenhousesWindWaveCounterHighFreq",
                out int actualHighFreqReplacements
            );

        functionSuffix =
            ReplaceIdentifierInCode(
                functionSuffix,
                "windWaveCounter",
                "stillGreenhousesWindWaveCounter",
                out int actualWindWaveCounterReplacements
            );

        functionSuffix =
            ReplaceIdentifierInCode(
                functionSuffix,
                "windSpeed",
                "stillGreenhousesWindSpeed",
                out int actualWindSpeedReplacements
            );

        rewrittenFunction =
            functionPrefix
            + functionSuffix;

        int residualWindSpeedUses =
            CountIdentifierInCode(
                rewrittenFunction[
                    environmentEnd..
                ],
                "windSpeed"
            );

        int residualWindWaveCounterUses =
            CountIdentifierInCode(
                rewrittenFunction[
                    environmentEnd..
                ],
                "windWaveCounter"
            );

        int residualHighFreqUses =
            CountIdentifierInCode(
                rewrittenFunction[
                    environmentEnd..
                ],
                "windWaveCounterHighFreq"
            );

        if (
            !rewrittenFunction.Contains(
                PatchMarker,
                StringComparison.Ordinal
            )
            || !rewrittenFunction.Contains(
                ModeAwareCorrectionMarker,
                StringComparison.Ordinal
            )
            || actualWindSpeedReplacements
                != windSpeedUses
            || actualWindWaveCounterReplacements
                != windWaveCounterUses
            || actualHighFreqReplacements
                != highFreqUses
            || residualWindSpeedUses != 0
            || residualWindWaveCounterUses != 0
            || residualHighFreqUses != 0
        )
        {
            diagnostic =
                "reason=post-patch-source-verification-failed; "
                + $"expectedWindSpeedUses={windSpeedUses}; "
                + $"actualWindSpeedUses={actualWindSpeedReplacements}; "
                + $"expectedWindWaveCounterUses={windWaveCounterUses}; "
                + $"actualWindWaveCounterUses={actualWindWaveCounterReplacements}; "
                + $"expectedHighFreqUses={highFreqUses}; "
                + $"actualHighFreqUses={actualHighFreqReplacements}; "
                + $"residualWindSpeed={residualWindSpeedUses}; "
                + $"residualWindWaveCounter={residualWindWaveCounterUses}; "
                + $"residualHighFreq={residualHighFreqUses}";

            capturedFunctionSource =
                rewrittenFunction;

            return false;
        }

        string uniformBlock = BuildCellHashUniformBlock();
        string helperBlock = BuildCellHashHelperBlock();

        patchedWindSpeedUses =
            actualWindSpeedReplacements;

        patchedWindWaveCounterUses =
            actualWindWaveCounterReplacements;

        patchedHighFreqUses =
            actualHighFreqReplacements;

        string sourceWithRewrittenFunction =
            source.Remove(
                functionRange.Start,
                functionRange.Length
            )
            .Insert(
                functionRange.Start,
                rewrittenFunction
            );

        patchedSource =
            uniformBlock
            + sourceWithRewrittenFunction.Insert(
                functionRange.Start,
                helperBlock
            );

        diagnostic =
            "reason=<none>; "
            + "strategy=vanilla-native-warp-with-room-environmental-inputs; "
            + "positionMatch=bounded-quarter-block-texture-hash";

        capturedFunctionSource =
            rewrittenFunction;

        return true;
    }

    private static string BuildCellHashUniformBlock() => $$"""

// {{PatchMarker}} uniforms
uniform int {{PositionCountUniformName}};
uniform sampler2D {{CellHashTextureUniformName}};
uniform int {{CellHashCapacityUniformName}};
uniform int {{CellHashMaskUniformName}};
uniform int {{CellHashSeedUniformName}};
uniform int {{CellHashTextureWidthUniformName}};
uniform int {{CellHashOriginXUniformName}};
uniform int {{CellHashOriginYUniformName}};
uniform int {{CellHashOriginZUniformName}};
uniform int {{RenderReferenceQuarterXUniformName}};
uniform int {{RenderReferenceQuarterYUniformName}};
uniform int {{RenderReferenceQuarterZUniformName}};
uniform float {{RenderReferenceFractionXUniformName}};
uniform float {{RenderReferenceFractionYUniformName}};
uniform float {{RenderReferenceFractionZUniformName}};
uniform int {{StateCountUniformName}};
uniform vec4 {{StatesUniformName}}[{{StillGreenhousesRoomWindUniformRenderer.UploadedShaderStateVectorCount}}];
uniform int {{DebugVisualProofUniformName}};
bool stillGreenhousesResolveRoomWindState(vec4 worldPos, int requiredTargetFlag, out vec4 roomState);
float stillGreenhousesRegularWindPhase(vec4 roomState);
float stillGreenhousesHighFrequencyWindPhase(vec4 roomState);

""";

    private static string BuildCellHashHelperBlock() => $$"""

// {{PatchMarker}} helpers
{{RoomWindCellHash.GlslHashSource}}

int stillGreenhousesDecodeSigned16(uint lowByte, uint highByte)
{
    int value = int(lowByte | (highByte << 8u));
    return value >= 32768 ? value - 65536 : value;
}

ivec2 stillGreenhousesCellHashTexelCoordinate(int linearTexel)
{
    return ivec2(
        linearTexel % {{CellHashTextureWidthUniformName}},
        linearTexel / {{CellHashTextureWidthUniformName}}
    );
}

bool stillGreenhousesResolveRoomWindState(
    vec4 worldPos,
    int requiredTargetFlag,
    out vec4 roomState
)
{
    roomState = vec4(0.0);

    if (
        {{PositionCountUniformName}} <= 0
        || {{CellHashCapacityUniformName}} <= 0
        || {{CellHashTextureWidthUniformName}} <= 0
    ) return false;

    vec3 renderRelativeQuarter =
        (worldPos.xyz + playerpos) * 4.0
        + vec3(
            {{RenderReferenceFractionXUniformName}},
            {{RenderReferenceFractionYUniformName}},
            {{RenderReferenceFractionZUniformName}}
        );

        // Liquid vertices frequently lie exactly on block and quarter-cell
        // boundaries. A small positive bias prevents floating-point reconstruction
        // from intermittently flooring them into the neighboring lower cell when
        // the render reference changes.
        if (requiredTargetFlag == 2)
        {
            const float liquidCellBoundaryBias = 0.01;
            renderRelativeQuarter += vec3(liquidCellBoundaryBias);
        }

ivec3 absoluteCell =
    ivec3(floor(renderRelativeQuarter))
    + ivec3(
        stillGreenhousesRenderReferenceQuarterX,
        stillGreenhousesRenderReferenceQuarterY,
        stillGreenhousesRenderReferenceQuarterZ
    );

    ivec3 absoluteCell = ivec3(floor(renderRelativeQuarter))
        + ivec3(
            {{RenderReferenceQuarterXUniformName}},
            {{RenderReferenceQuarterYUniformName}},
            {{RenderReferenceQuarterZUniformName}}
        );

    ivec3 relativeCell = absoluteCell
        - ivec3(
            {{CellHashOriginXUniformName}},
            {{CellHashOriginYUniformName}},
            {{CellHashOriginZUniformName}}
        );

    if (
        any(lessThan(relativeCell, ivec3(-32768)))
        || any(greaterThan(relativeCell, ivec3(32767)))
    ) return false;

    uint hash = stillGreenhousesHashRelativeCell(
        relativeCell,
        uint({{CellHashSeedUniformName}})
    );

    for (int probe = 0; probe < {{RoomWindCellHash.ProbeLimit}}; probe++)
    {
        int slot = int(
            (hash + uint(probe))
            & uint({{CellHashMaskUniformName}})
        );
        int linearTexel = slot * {{RoomWindCellHash.TexelsPerSlot}};
        uvec4 keyBytes = uvec4(round(
            texelFetch(
                {{CellHashTextureUniformName}},
                stillGreenhousesCellHashTexelCoordinate(linearTexel),
                0
            ) * 255.0
        ));
        uvec4 valueBytes = uvec4(round(
            texelFetch(
                {{CellHashTextureUniformName}},
                stillGreenhousesCellHashTexelCoordinate(linearTexel + 1),
                0
            ) * 255.0
        ));

        if (valueBytes.a == 0u) return false;

        ivec3 storedCell = ivec3(
            stillGreenhousesDecodeSigned16(keyBytes.r, keyBytes.g),
            stillGreenhousesDecodeSigned16(keyBytes.b, keyBytes.a),
            stillGreenhousesDecodeSigned16(valueBytes.r, valueBytes.g)
        );

        if (any(notEqual(storedCell, relativeCell))) continue;

        int payload = int(valueBytes.b);
        int targetCode = requiredTargetFlag == 2
            ? (payload >> 2) & 3
            : payload & 3;

        if (targetCode == 0) return false;

        int stateIndex = targetCode - 1;
        if (requiredTargetFlag == 2) stateIndex += 3;

        if (
            stateIndex < 0
            || stateIndex >= {{StateCountUniformName}}
        ) return false;

        roomState = {{StatesUniformName}}[stateIndex];
        return true;
    }

    return false;
}

float stillGreenhousesRegularWindPhase(vec4 roomState)
{
    float speed = clamp(roomState.x, 0.0, 2.0);
    return mod(roomState.y + timeCounter * (0.5 + 2.0 * speed), 6000.0);
}

float stillGreenhousesHighFrequencyWindPhase(vec4 roomState)
{
    float speed = clamp(roomState.x, 0.0, 2.0);
    float rate = (0.4 + speed / 10.0) * (0.5 + 5.0 * speed);
    return mod(roomState.z + timeCounter * rate, 6000.0);
}

vec4 {{CallSiteWrapperFunctionName}}(
    int renderFlags,
    vec4 basePos,
    vec4 warpedPos
)
{
    if ((renderFlags & WindModeBitMask) <= 0) return warpedPos;

    if ({{DebugVisualProofUniformName}} == 2)
    {
        vec4 callSiteProof = warpedPos;
        callSiteProof.x += 0.75;
        return callSiteProof;
    }

    int windMode = (renderFlags >> WindModePosition) & 0xF;
    if (windMode == 6 || windMode == 12) return warpedPos;

    if ({{DebugVisualProofUniformName}} == 1)
    {
        vec4 roomState = vec4(0.0);
        if (stillGreenhousesResolveRoomWindState(basePos, 1, roomState))
        {
            vec4 managedPositionProof = warpedPos;
            managedPositionProof.y += 0.35;
            return managedPositionProof;
        }
    }

    return warpedPos;
}

""";

    private static bool TryPatchLiquidWarping(
        string source,
        out string patchedSource,
        out string diagnostic
    )
    {
        patchedSource = source;

        if (source.Contains(
                LiquidPatchMarker,
                StringComparison.Ordinal
            ))
        {
            diagnostic =
                "reason=liquid-marker-already-present";

            return false;
        }

        if (!TryFindLiquidFunctionRange(
                source,
                out SourceRange functionRange,
                out string findDiagnostic
            ))
        {
            diagnostic =
                "reason=liquid-function-not-unambiguous; "
                + findDiagnostic;

            return false;
        }

        string functionSource =
            source.Substring(
                functionRange.Start,
                functionRange.Length
            );

        if (
            !functionSource.Contains(
                "applyPerceptionWarping",
                StringComparison.Ordinal
            )
            || CountIdentifierInCode(
                functionSource,
                "waterWaveCounter"
            ) < 1
            || CountIdentifierInCode(
                functionSource,
                "windWaveCounter"
            ) < 1
            || CountIdentifierInCode(
                functionSource,
                "waterWaveIntensity"
            ) < 1
        )
        {
            diagnostic =
                "reason=liquid-function-landmarks-missing";

            return false;
        }

        patchedSource =
            source.Remove(
                functionRange.Start,
                functionRange.Length
            )
            .Insert(
                functionRange.Start,
                BuildPatchedLiquidFunction()
            );

        diagnostic =
            "reason=<none>; "
            + "strategy=room-local-vanilla-liquid-environment";

        return true;
    }

    private static bool TryFindLiquidFunctionRange(
        string source,
        out SourceRange range,
        out string diagnostic
    )
    {
        List<int> identifiers =
            FindIdentifierPositionsInCode(
                source,
                TargetLiquidFunctionName
            );

        List<SourceRange> candidates = new();

        foreach (int identifierIndex in identifiers)
        {
            int openParen =
                FindNextNonWhitespace(
                    source,
                    identifierIndex
                    + TargetLiquidFunctionName.Length
                );

            if (
                openParen < 0
                || source[openParen] != '('
            )
            {
                continue;
            }

            int closeParen =
                FindMatchingDelimiter(
                    source,
                    openParen,
                    '(',
                    ')'
                );

            if (closeParen < 0)
            {
                continue;
            }

            string parameters =
                source.Substring(
                    openParen + 1,
                    closeParen - openParen - 1
                );

            if (
                !parameters.Contains(
                    "worldPos",
                    StringComparison.Ordinal
                )
                || !parameters.Contains(
                    "div",
                    StringComparison.Ordinal
                )
                || !parameters.Contains(
                    "vec4",
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }

            int openBrace =
                FindNextNonWhitespace(
                    source,
                    closeParen + 1
                );

            if (
                openBrace < 0
                || source[openBrace] != '{'
            )
            {
                continue;
            }

            int closeBrace =
                FindMatchingDelimiter(
                    source,
                    openBrace,
                    '{',
                    '}'
                );

            if (closeBrace < 0)
            {
                continue;
            }

            int start =
                FindLineStart(
                    source,
                    identifierIndex
                );

            candidates.Add(
                new SourceRange(
                    start,
                    closeBrace + 1
                )
            );
        }

        if (candidates.Count != 1)
        {
            diagnostic =
                $"identifierOccurrences={identifiers.Count}; "
                + $"candidates={candidates.Count}; expected=1";

            range = default;

            return false;
        }

        range =
            candidates[0];

        diagnostic =
            "reason=<none>";

        return true;
    }

    // Liquid keeps Vanilla's two-noise deformation shape. Managed room water
    // uses the room-local wind counter for the wind-affected phase, then scales
    // the complete Vanilla liquid-warp delta by the room wind scalar. Liquid
    // remains a separate optional behavior; vegetation now relies only on
    // Vanilla's native warp evaluated with room-local environmental inputs.
    private static string BuildPatchedLiquidFunction() => $$"""
vec4 applyLiquidWarping(bool windAffected, vec4 worldPos, float div) {
	#if WAVINGSTUFF == 1
	// {{LiquidPatchMarker}}
	vec4 stillGreenhousesLiquidRoomState = vec4(0.0);
	bool stillGreenhousesRoomLiquid = windAffected
		&& stillGreenhousesResolveRoomWindState(
			worldPos,
			2,
			stillGreenhousesLiquidRoomState
		);
	bool stillGreenhousesLiquidNoWind = stillGreenhousesRoomLiquid
		&& stillGreenhousesLiquidRoomState.w < 0.0;
	float stillGreenhousesLiquidWindSpeed = stillGreenhousesRoomLiquid
		? clamp(stillGreenhousesLiquidRoomState.x, 0.0, 2.0)
		: windSpeed;
	float stillGreenhousesLiquidWindWaveCounter = stillGreenhousesRoomLiquid
		? stillGreenhousesRegularWindPhase(stillGreenhousesLiquidRoomState)
		: windWaveCounter;
	float stillGreenhousesLiquidWaterWaveIntensity = stillGreenhousesRoomLiquid
		? (stillGreenhousesLiquidNoWind
			? 0.0
			: 0.75 + 0.9 * stillGreenhousesLiquidWindSpeed)
		: waterWaveIntensity;
	float stillGreenhousesLiquidSecondaryIntensity = stillGreenhousesLiquidNoWind
		? 0.0
		: windWaveIntensity;

	vec3 noisepos = vec3(
		(worldPos.x + playerpos.x) / 3,
		(worldPos.z + playerpos.z) / 3,
		waterWaveCounter / 8
			+ (windAffected ? stillGreenhousesLiquidWindWaveCounter / 4 : 0)
	);
	worldPos.y += stillGreenhousesLiquidWaterWaveIntensity
		* gnoise(noisepos) / div;

	if (windAffected) worldPos.y += stillGreenhousesLiquidSecondaryIntensity
		* gnoise(noisepos * 3.5) / (div * 4);

	worldPos.xyz = applyPerceptionWarping(worldPos.xyz);
	#endif

	return worldPos;
}
""";

    // Kept in-source for the 0.18.0 rollback window. It is no longer emitted;
    // it used the shared linear descriptor list and scaled the final delta.
    private static string BuildPatchedLiquidFunctionLegacy() =>
        "vec4 applyLiquidWarping(bool windAffected, vec4 worldPos, float div) {\n"
        + "\t#if WAVINGSTUFF == 1\n"
        + "\t// " + LiquidPatchMarker + "\n"
        + "\tvec4 stillGreenhousesLiquidBasePos = worldPos;\n"
        + "\tbool stillGreenhousesRoomLiquid = false;\n"
        + "\tvec4 stillGreenhousesLiquidRoomState = vec4(0.0);\n"
        + "\tvec3 stillGreenhousesLiquidRelativePos = "
        + "worldPos.xyz + playerpos;\n"
        + "\tfloat stillGreenhousesLiquidBestDistanceSquared = 1e30;\n"
        + "\tfor (int stillGreenhousesLiquidIndex = 0; "
        + "stillGreenhousesLiquidIndex < stillGreenhousesRoomWindPositionCount; "
        + "stillGreenhousesLiquidIndex++) {\n"
        + "\t\tvec4 stillGreenhousesLiquidEntry = "
        + "stillGreenhousesRoomWindPositions[stillGreenhousesLiquidIndex];\n"
        + "\t\tint stillGreenhousesLiquidPackedMetadata = "
        + "int(stillGreenhousesLiquidEntry.w + 0.5);\n"
        + "\t\tint stillGreenhousesLiquidPackedTarget = "
        + "stillGreenhousesLiquidPackedMetadata & 15;\n"
        + "\t\tint stillGreenhousesLiquidStateIndex = "
        + "stillGreenhousesLiquidPackedTarget % "
        + StillGreenhousesRoomWindEnvironment.RoomTypeStateCount
        + ";\n"
        + "\t\tint stillGreenhousesLiquidTargetFlags = "
        + "stillGreenhousesLiquidPackedTarget / "
        + StillGreenhousesRoomWindEnvironment.RoomTypeStateCount
        + ";\n"
        + "\t\tstillGreenhousesLiquidStateIndex += "
        + StillGreenhousesRoomWindEnvironment.RoomTypeStateCount
        + ";\n"
        + "\t\tbool stillGreenhousesWaterTarget = "
        + "(stillGreenhousesLiquidTargetFlags & 2) != 0;\n"
        + "\t\tbool stillGreenhousesLiquidValidState = "
        + "stillGreenhousesLiquidStateIndex >= 0 && "
        + "stillGreenhousesLiquidStateIndex < "
        + "stillGreenhousesRoomWindStateCount;\n"
        + "\t\tif (!stillGreenhousesWaterTarget || "
        + "!stillGreenhousesLiquidValidState) continue;\n"
        + "\t\tint stillGreenhousesLiquidExtentX = "
        + "(stillGreenhousesLiquidPackedMetadata >> 4) & 63;\n"
        + "\t\tint stillGreenhousesLiquidExtentY = "
        + "(stillGreenhousesLiquidPackedMetadata >> 10) & 63;\n"
        + "\t\tint stillGreenhousesLiquidExtentZ = "
        + "(stillGreenhousesLiquidPackedMetadata >> 16) & 63;\n"
        + "\t\tvec3 stillGreenhousesLiquidHalfExtents = "
        + "vec3(float(stillGreenhousesLiquidExtentX), "
        + "float(stillGreenhousesLiquidExtentY), "
        + "float(stillGreenhousesLiquidExtentZ)) * 0.125;\n"
        + "\t\tvec3 stillGreenhousesLiquidMin = "
        + "stillGreenhousesLiquidEntry.xyz - "
        + "stillGreenhousesLiquidHalfExtents;\n"
        + "\t\tvec3 stillGreenhousesLiquidMax = "
        + "stillGreenhousesLiquidEntry.xyz + "
        + "stillGreenhousesLiquidHalfExtents;\n"
        + "\t\tbool stillGreenhousesLiquidInside = "
        + "all(greaterThanEqual(stillGreenhousesLiquidRelativePos, "
        + "stillGreenhousesLiquidMin)) && "
        + "all(lessThanEqual(stillGreenhousesLiquidRelativePos, "
        + "stillGreenhousesLiquidMax));\n"
        + "\t\tif (!stillGreenhousesLiquidInside) continue;\n"
        + "\t\tvec3 stillGreenhousesLiquidCenter = "
        + "stillGreenhousesLiquidEntry.xyz;\n"
        + "\t\tvec3 stillGreenhousesLiquidCenterDelta = "
        + "stillGreenhousesLiquidRelativePos - stillGreenhousesLiquidCenter;\n"
        + "\t\tfloat stillGreenhousesLiquidDistanceSquared = "
        + "dot(stillGreenhousesLiquidCenterDelta, "
        + "stillGreenhousesLiquidCenterDelta);\n"
        + "\t\tif (!stillGreenhousesRoomLiquid || "
        + "stillGreenhousesLiquidDistanceSquared < "
        + "stillGreenhousesLiquidBestDistanceSquared) {\n"
        + "\t\t\tstillGreenhousesRoomLiquid = true;\n"
        + "\t\t\tstillGreenhousesLiquidBestDistanceSquared = "
        + "stillGreenhousesLiquidDistanceSquared;\n"
        + "\t\t\tstillGreenhousesLiquidRoomState = "
        + "stillGreenhousesRoomWindStates[stillGreenhousesLiquidStateIndex];\n"
        + "\t\t}\n"
        + "\t}\n"
        + "\tfloat stillGreenhousesLiquidWindWaveCounter = "
        + "stillGreenhousesRoomLiquid ? "
        + "stillGreenhousesLiquidRoomState.y : windWaveCounter;\n"
        + "\tvec3 noisepos = vec3((worldPos.x + playerpos.x) / 3, "
        + "(worldPos.z + playerpos.z) / 3, "
        + "waterWaveCounter / 8 + "
        + "(windAffected ? stillGreenhousesLiquidWindWaveCounter / 4 : 0));\n"
        + "\tworldPos.y += waterWaveIntensity * gnoise(noisepos) / div;\n"
        + "\t\n"
        + "\tif (windAffected) worldPos.y += windWaveIntensity "
        + "* gnoise(noisepos * 3.5) / (div * 4);\n"
        + "\t\n"
        + "\tif (stillGreenhousesRoomLiquid) {\n"
        + "\t\tfloat stillGreenhousesLiquidAmplitude = "
        + "clamp(stillGreenhousesLiquidRoomState.x, 0.0, 2.0);\n"
        + "\t\tworldPos.xyz = stillGreenhousesLiquidBasePos.xyz + "
        + "(worldPos.xyz - stillGreenhousesLiquidBasePos.xyz) "
        + "* stillGreenhousesLiquidAmplitude;\n"
        + "\t}\n"
        + "\t\n"
        + "\tworldPos.xyz = applyPerceptionWarping(worldPos.xyz);\n"
        + "\t\n"
        + "\t#endif\n"
        + "\t\n"
        + "\treturn worldPos;\n"
        + "}";

    private static bool TryFindUniqueFunctionRange(
        string source,
        string functionName,
        out SourceRange range,
        out string diagnostic
    )
    {
        List<int> identifiers =
            FindIdentifierPositionsInCode(
                source,
                functionName
            );

        List<SourceRange> candidates = new();

        foreach (int identifierIndex in identifiers)
        {
            int openParen =
                FindNextNonWhitespace(
                    source,
                    identifierIndex
                        + functionName.Length
                );

            if (
                openParen < 0
                || source[openParen] != '('
            )
            {
                continue;
            }

            int closeParen =
                FindMatchingDelimiter(
                    source,
                    openParen,
                    '(',
                    ')'
                );

            if (closeParen < 0)
            {
                continue;
            }

            string parameters =
                source.Substring(
                    openParen + 1,
                    closeParen - openParen - 1
                );

            if (
                !parameters.Contains(
                    "renderFlags",
                    StringComparison.Ordinal
                )
                || !parameters.Contains(
                    "worldPos",
                    StringComparison.Ordinal
                )
                || !parameters.Contains(
                    "int",
                    StringComparison.Ordinal
                )
                || !parameters.Contains(
                    "vec4",
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }

            int openBrace =
                FindNextNonWhitespace(
                    source,
                    closeParen + 1
                );

            if (
                openBrace < 0
                || source[openBrace] != '{'
            )
            {
                continue;
            }

            int closeBrace =
                FindMatchingDelimiter(
                    source,
                    openBrace,
                    '{',
                    '}'
                );

            if (closeBrace < 0)
            {
                diagnostic =
                    "function-closing-brace-not-found";

                range = default;
                return false;
            }

            int start =
                FindLineStart(
                    source,
                    identifierIndex
                );

            candidates.Add(
                new SourceRange(
                    start,
                    closeBrace + 1
                )
            );
        }

        if (candidates.Count != 1)
        {
            diagnostic =
                "function-candidate-count-invalid; " +
                $"function={functionName}; " +
                $"identifierOccurrences={identifiers.Count}; " +
                $"candidates={candidates.Count}; expected=1";

            range = default;
            return false;
        }

        range = candidates[0];
        diagnostic = "reason=<none>";

        return true;
    }

    private static bool TryFindUniqueStatementByIdentifier(
        string source,
        string primaryIdentifier,
        IReadOnlyList<string> requiredFragments,
        out SourceRange range,
        out string diagnostic
    )
    {
        List<int> positions =
            FindIdentifierPositionsInCode(
                source,
                primaryIdentifier
            );

        List<SourceRange> candidates = new();

        foreach (int position in positions)
        {
            int statementStart =
                FindStatementStart(
                    source,
                    position
                );

            int semicolon =
                FindNextCodeCharacter(
                    source,
                    position,
                    ';'
                );

            if (semicolon < 0)
            {
                continue;
            }

            SourceRange candidate =
                new(
                    statementStart,
                    semicolon + 1
                );

            string statement =
                source.Substring(
                    candidate.Start,
                    candidate.Length
                );

            bool matches = true;

            foreach (
                string requiredFragment
                in requiredFragments
            )
            {
                if (!statement.Contains(
                        requiredFragment,
                        StringComparison.Ordinal
                    ))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                candidates.Add(candidate);
            }
        }

        candidates =
            DeduplicateRanges(candidates);

        if (candidates.Count != 1)
        {
            diagnostic =
                $"identifier={primaryIdentifier}; " +
                $"identifierOccurrences={positions.Count}; " +
                $"matchingStatements={candidates.Count}; " +
                $"required={string.Join(",", requiredFragments)}";

            range = default;
            return false;
        }

        range = candidates[0];
        diagnostic = "reason=<none>";

        return true;
    }

    private static bool TryFindUniqueWindModeSwitch(
        string source,
        out int switchIndex,
        out string diagnostic
    )
    {
        List<int> switchPositions =
            FindIdentifierPositionsInCode(
                source,
                "switch"
            );

        List<int> candidates = new();

        foreach (int position in switchPositions)
        {
            int openParen =
                FindNextNonWhitespace(
                    source,
                    position + "switch".Length
                );

            if (
                openParen < 0
                || source[openParen] != '('
            )
            {
                continue;
            }

            int closeParen =
                FindMatchingDelimiter(
                    source,
                    openParen,
                    '(',
                    ')'
                );

            if (closeParen < 0)
            {
                continue;
            }

            string condition =
                source.Substring(
                    openParen + 1,
                    closeParen - openParen - 1
                );

            if (ContainsIdentifierInCode(
                    condition,
                    "windMode"
                ))
            {
                candidates.Add(position);
            }
        }

        if (candidates.Count != 1)
        {
            diagnostic =
                $"switchOccurrences={switchPositions.Count}; " +
                $"windModeSwitchCandidates={candidates.Count}; " +
                "expected=1";

            switchIndex = -1;
            return false;
        }

        switchIndex = candidates[0];
        diagnostic = "reason=<none>";

        return true;
    }

    private static bool TryFindUniqueCaseLabel(
        string source,
        int requestedMode,
        out int caseIndex,
        out string diagnostic
    )
    {
        List<int> matches =
            FindCaseLabelPositions(
                source,
                requestedMode
            );

        if (matches.Count != 1)
        {
            diagnostic =
                $"mode={requestedMode}; " +
                $"caseLabelCount={matches.Count}; " +
                "expected=1";

            caseIndex = -1;
            return false;
        }

        caseIndex = matches[0];
        diagnostic = "reason=<none>";

        return true;
    }

    private static int CountCaseLabels(
        string source,
        int requestedMode
    ) =>
        FindCaseLabelPositions(
            source,
            requestedMode
        ).Count;

    private static List<int> FindCaseLabelPositions(
        string source,
        int requestedMode
    )
    {
        List<int> result = new();

        int lineStart = 0;

        while (lineStart <= source.Length)
        {
            int lineEnd =
                source.IndexOf(
                    '\n',
                    lineStart
                );

            if (lineEnd < 0)
            {
                lineEnd = source.Length;
            }

            string line =
                source.Substring(
                    lineStart,
                    lineEnd - lineStart
                );

            int cursor = 0;

            while (
                cursor < line.Length
                && (
                    line[cursor] == ' '
                    || line[cursor] == '\t'
                )
            )
            {
                cursor++;
            }

            if (
                cursor + 4 <= line.Length
                && string.Equals(
                    line.Substring(cursor, 4),
                    "case",
                    StringComparison.Ordinal
                )
            )
            {
                cursor += 4;

                if (
                    cursor < line.Length
                    && !char.IsWhiteSpace(line[cursor])
                )
                {
                    goto NextLine;
                }

                while (
                    cursor < line.Length
                    && char.IsWhiteSpace(line[cursor])
                )
                {
                    cursor++;
                }

                int numberStart = cursor;

                while (
                    cursor < line.Length
                    && char.IsDigit(line[cursor])
                )
                {
                    cursor++;
                }

                if (
                    numberStart < cursor
                    && int.TryParse(
                        line[numberStart..cursor],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int parsedMode
                    )
                )
                {
                    while (
                        cursor < line.Length
                        && char.IsWhiteSpace(line[cursor])
                    )
                    {
                        cursor++;
                    }

                    if (
                        cursor < line.Length
                        && line[cursor] == ':'
                        && parsedMode == requestedMode
                    )
                    {
                        result.Add(
                            lineStart
                            + numberStart
                            - 5
                        );

                        // Normalize to the actual first non-whitespace
                        // character of the case label.
                        result[^1] =
                            lineStart
                            + (
                                line.Length
                                - line.TrimStart(' ', '\t').Length
                            );
                    }
                }
            }

        NextLine:
            if (lineEnd == source.Length)
            {
                break;
            }

            lineStart =
                lineEnd + 1;
        }

        return result;
    }

    private static List<SourceRange> DeduplicateRanges(
        List<SourceRange> ranges
    )
    {
        List<SourceRange> result = new();

        foreach (SourceRange range in ranges)
        {
            bool alreadyPresent = false;

            foreach (SourceRange existing in result)
            {
                if (
                    existing.Start == range.Start
                    && existing.EndExclusive
                        == range.EndExclusive
                )
                {
                    alreadyPresent = true;
                    break;
                }
            }

            if (!alreadyPresent)
            {
                result.Add(range);
            }
        }

        return result;
    }

    private static int FindStatementStart(
        string source,
        int position
    )
    {
        for (
            int i = position - 1;
            i >= 0;
            i--
        )
        {
            if (
                source[i] == ';'
                || source[i] == '{'
                || source[i] == '}'
            )
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static int FindNextCodeCharacter(
        string source,
        int startIndex,
        char requestedCharacter
    )
    {
        bool lineComment = false;
        bool blockComment = false;
        bool stringLiteral = false;
        char stringDelimiter = '\0';

        for (
            int i = Math.Max(0, startIndex);
            i < source.Length;
            i++
        )
        {
            char current = source[i];

            char next =
                i + 1 < source.Length
                    ? source[i + 1]
                    : '\0';

            if (lineComment)
            {
                if (current == '\n')
                {
                    lineComment = false;
                }

                continue;
            }

            if (blockComment)
            {
                if (
                    current == '*'
                    && next == '/'
                )
                {
                    blockComment = false;
                    i++;
                }

                continue;
            }

            if (stringLiteral)
            {
                if (
                    current == '\\'
                    && next != '\0'
                )
                {
                    i++;
                    continue;
                }

                if (current == stringDelimiter)
                {
                    stringLiteral = false;
                }

                continue;
            }

            if (
                current == '/'
                && next == '/'
            )
            {
                lineComment = true;
                i++;
                continue;
            }

            if (
                current == '/'
                && next == '*'
            )
            {
                blockComment = true;
                i++;
                continue;
            }

            if (current is '\'' or '"')
            {
                stringLiteral = true;
                stringDelimiter = current;
                continue;
            }

            if (current == requestedCharacter)
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindMatchingDelimiter(
        string source,
        int openIndex,
        char openCharacter,
        char closeCharacter
    )
    {
        int depth = 0;
        bool lineComment = false;
        bool blockComment = false;
        bool stringLiteral = false;
        char stringDelimiter = '\0';

        for (
            int i = openIndex;
            i < source.Length;
            i++
        )
        {
            char current = source[i];

            char next =
                i + 1 < source.Length
                    ? source[i + 1]
                    : '\0';

            if (lineComment)
            {
                if (current == '\n')
                {
                    lineComment = false;
                }

                continue;
            }

            if (blockComment)
            {
                if (
                    current == '*'
                    && next == '/'
                )
                {
                    blockComment = false;
                    i++;
                }

                continue;
            }

            if (stringLiteral)
            {
                if (
                    current == '\\'
                    && next != '\0'
                )
                {
                    i++;
                    continue;
                }

                if (current == stringDelimiter)
                {
                    stringLiteral = false;
                }

                continue;
            }

            if (
                current == '/'
                && next == '/'
            )
            {
                lineComment = true;
                i++;
                continue;
            }

            if (
                current == '/'
                && next == '*'
            )
            {
                blockComment = true;
                i++;
                continue;
            }

            if (current is '\'' or '"')
            {
                stringLiteral = true;
                stringDelimiter = current;
                continue;
            }

            if (current == openCharacter)
            {
                depth++;
                continue;
            }

            if (current == closeCharacter)
            {
                depth--;

                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static int FindNextNonWhitespace(
        string source,
        int startIndex
    )
    {
        for (
            int i = Math.Max(0, startIndex);
            i < source.Length;
            i++
        )
        {
            if (!char.IsWhiteSpace(source[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindLineStart(
        string source,
        int index
    )
    {
        int lineStart =
            source.LastIndexOf(
                '\n',
                Math.Max(0, index - 1)
            );

        return lineStart < 0
            ? 0
            : lineStart + 1;
    }

    private static string GetLineIndent(
        string source,
        int index
    )
    {
        int lineStart =
            FindLineStart(
                source,
                index
            );

        int cursor = lineStart;

        while (
            cursor < source.Length
            && (
                source[cursor] == ' '
                || source[cursor] == '\t'
            )
        )
        {
            cursor++;
        }

        return source.Substring(
            lineStart,
            cursor - lineStart
        );
    }

    private static bool ContainsIdentifierInCode(
        string source,
        string identifier
    ) =>
        FindIdentifierPositionsInCode(
            source,
            identifier
        ).Count > 0;

    private static int CountIdentifierInCode(
        string source,
        string identifier
    ) =>
        FindIdentifierPositionsInCode(
            source,
            identifier
        ).Count;

    private static List<int> FindIdentifierPositionsInCode(
        string source,
        string identifier
    )
    {
        List<int> result = new();

        bool lineComment = false;
        bool blockComment = false;
        bool stringLiteral = false;
        char stringDelimiter = '\0';

        for (int i = 0; i < source.Length;)
        {
            char current = source[i];

            char next =
                i + 1 < source.Length
                    ? source[i + 1]
                    : '\0';

            if (lineComment)
            {
                if (current == '\n')
                {
                    lineComment = false;
                }

                i++;
                continue;
            }

            if (blockComment)
            {
                if (
                    current == '*'
                    && next == '/'
                )
                {
                    blockComment = false;
                    i += 2;
                    continue;
                }

                i++;
                continue;
            }

            if (stringLiteral)
            {
                if (
                    current == '\\'
                    && next != '\0'
                )
                {
                    i += 2;
                    continue;
                }

                if (current == stringDelimiter)
                {
                    stringLiteral = false;
                }

                i++;
                continue;
            }

            if (
                current == '/'
                && next == '/'
            )
            {
                lineComment = true;
                i += 2;
                continue;
            }

            if (
                current == '/'
                && next == '*'
            )
            {
                blockComment = true;
                i += 2;
                continue;
            }

            if (current is '\'' or '"')
            {
                stringLiteral = true;
                stringDelimiter = current;
                i++;
                continue;
            }

            if (
                char.IsLetter(current)
                || current == '_'
            )
            {
                int tokenStart = i;
                i++;

                while (
                    i < source.Length
                    && (
                        char.IsLetterOrDigit(source[i])
                        || source[i] == '_'
                    )
                )
                {
                    i++;
                }

                int tokenLength =
                    i - tokenStart;

                if (
                    tokenLength == identifier.Length
                    && string.CompareOrdinal(
                        source,
                        tokenStart,
                        identifier,
                        0,
                        identifier.Length
                    ) == 0
                )
                {
                    result.Add(tokenStart);
                }

                continue;
            }

            i++;
        }

        return result;
    }

    private static string ReplaceIdentifierInCode(
        string source,
        string identifier,
        string replacement,
        out int replacementCount
    )
    {
        StringBuilder builder =
            new(source.Length + 256);

        replacementCount = 0;

        bool lineComment = false;
        bool blockComment = false;
        bool stringLiteral = false;
        char stringDelimiter = '\0';

        for (int i = 0; i < source.Length;)
        {
            char current = source[i];

            char next =
                i + 1 < source.Length
                    ? source[i + 1]
                    : '\0';

            if (lineComment)
            {
                builder.Append(current);

                if (current == '\n')
                {
                    lineComment = false;
                }

                i++;
                continue;
            }

            if (blockComment)
            {
                builder.Append(current);

                if (
                    current == '*'
                    && next == '/'
                )
                {
                    builder.Append(next);
                    blockComment = false;
                    i += 2;
                    continue;
                }

                i++;
                continue;
            }

            if (stringLiteral)
            {
                builder.Append(current);

                if (
                    current == '\\'
                    && next != '\0'
                )
                {
                    builder.Append(next);
                    i += 2;
                    continue;
                }

                if (current == stringDelimiter)
                {
                    stringLiteral = false;
                }

                i++;
                continue;
            }

            if (
                current == '/'
                && next == '/'
            )
            {
                builder.Append(current);
                builder.Append(next);
                lineComment = true;
                i += 2;
                continue;
            }

            if (
                current == '/'
                && next == '*'
            )
            {
                builder.Append(current);
                builder.Append(next);
                blockComment = true;
                i += 2;
                continue;
            }

            if (current is '\'' or '"')
            {
                builder.Append(current);
                stringLiteral = true;
                stringDelimiter = current;
                i++;
                continue;
            }

            if (
                char.IsLetter(current)
                || current == '_'
            )
            {
                int tokenStart = i;
                i++;

                while (
                    i < source.Length
                    && (
                        char.IsLetterOrDigit(source[i])
                        || source[i] == '_'
                    )
                )
                {
                    i++;
                }

                int tokenLength =
                    i - tokenStart;

                if (
                    tokenLength == identifier.Length
                    && string.CompareOrdinal(
                        source,
                        tokenStart,
                        identifier,
                        0,
                        identifier.Length
                    ) == 0
                )
                {
                    builder.Append(replacement);
                    replacementCount++;
                }
                else
                {
                    builder.Append(
                        source,
                        tokenStart,
                        tokenLength
                    );
                }

                continue;
            }

            builder.Append(current);
            i++;
        }

        return builder.ToString();
    }

    private static int LogPatchFailure(
        ICoreAPI api,
        string diagnostic,
        string? functionSource
    )
    {
        string message =
            "[StillGreenhouses] ROOM WIND SHADER PATCH FAILED " +
            $"location={TargetAssetLocation}; " +
            diagnostic;

        api.Logger.Warning(message);

        api.Logger.Debug(
            "{0}",
            message
        );

        if (string.IsNullOrEmpty(functionSource))
        {
            return 0;
        }

        List<string> chunks =
            BuildFunctionSourceChunks(
                functionSource
            );

        for (int i = 0; i < chunks.Count; i++)
        {
            api.Logger.Debug(
                "{0}",
                "[StillGreenhouses] ROOM WIND SHADER FUNCTION SOURCE " +
                $"chunk={i + 1}/{chunks.Count}; " +
                $"source={chunks[i]}"
            );
        }

        return chunks.Count;
    }

    private static List<string> BuildFunctionSourceChunks(
        string functionSource
    )
    {
        string normalized =
            functionSource
                .Replace(
                    "\r\n",
                    "\n",
                    StringComparison.Ordinal
                )
                .Replace(
                    '\r',
                    '\n'
                );

        string[] lines =
            normalized.Split('\n');

        StringBuilder formatted = new();

        for (int i = 0; i < lines.Length; i++)
        {
            if (formatted.Length > 0)
            {
                formatted.Append(" || ");
            }

            formatted.Append(i + 1);
            formatted.Append(':');
            formatted.Append(
                lines[i]
                    .Replace('\t', ' ')
                    .Trim()
            );
        }

        List<string> result = new();

        for (
            int offset = 0;
            offset < formatted.Length;
            offset += MaxFunctionSourceChunkCharacters
        )
        {
            int length =
                Math.Min(
                    MaxFunctionSourceChunkCharacters,
                    formatted.Length - offset
                );

            result.Add(
                formatted.ToString(
                    offset,
                    length
                )
            );
        }

        return result;
    }

    private static string ToShaderFloat(
        float value
    )
    {
        string result =
            value.ToString(
                "0.0#####",
                CultureInfo.InvariantCulture
            );

        return result.Contains('.')
            ? result
            : result + ".0";
    }

    private static string CompactDiagnostic(
        string value
    ) =>
        Sanitize(value)
            .Replace(';', ',');

    private static string Sanitize(
        string value
    ) =>
        value
            .Replace('\r', ' ')
            .Replace('\n', ' ');

    private readonly record struct ShaderCallSiteAsset(
        string AssetLocation,
        int ExpectedCalls
    );

    private readonly record struct SourceRewrite(
        int Start,
        int EndExclusive,
        string Replacement
    );

    private readonly record struct SourceRange(
        int Start,
        int EndExclusive
    )
    {
        internal int Length =>
            EndExclusive - Start;
    }

}
