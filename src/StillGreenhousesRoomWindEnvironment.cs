/*
version 0.10.16a
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace StillGreenhouses;

// 0.10.16a preserves every vegetation vertex's original Vanilla WindMode and
// WindData, masks exact managed vegetation block positions, and supplies one
// room-owned environmental wind state per ManagedRoomType.
//
// Vanilla applyVertexWarping() still performs the native per-mode deformation.
// The active terrain call sites then pass that Vanilla result through a room
// amplitude wrapper. Managed vertices keep their native movement profile while
// the room scales the full Vanilla deformation delta by its own wind percent.
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
        && StillGreenhousesClientSystem
            .PlantMovementMode
            == RoomPlantMovementMode.VanillaLowWind;

    internal static bool ShouldUseRoomWindShader =>
        EnvironmentActive;

    internal static string EffectiveWindTarget =>
        StillGreenhousesClientSystem
            .PlantMovementMode
            == RoomPlantMovementMode.NoWind
                ? RoomPlantMovementMode.NoWind.ToString()
                : EnvironmentActive
                    ? "VanillaNativeWarp@RoomTypeAmplitude/SharedStates"
                    : "CompiledBridgeUnavailable/VanillaGlobalWind";

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

    internal static RoomWindRange GetRange(
        StillGreenhousesConfig config,
        ManagedRoomType roomType
    )
    {
        float lower;
        float upper;

        switch (roomType)
        {
            case ManagedRoomType.Greenhouse:
                lower =
                    config.GreenhouseWindLowerPercent;

                upper =
                    config.GreenhouseWindUpperPercent;

                break;

            case ManagedRoomType.Cellar:
                lower =
                    config.CellarWindLowerPercent;

                upper =
                    config.CellarWindUpperPercent;

                break;

            default:
                lower =
                    config.RoomWindLowerPercent;

                upper =
                    config.RoomWindUpperPercent;

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
        ManagedRoomType roomType
    )
    {
        RoomWindRange range =
            GetRange(
                config,
                roomType
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

        if (
            StillGreenhousesClientSystem
                .PlantMovementMode
                == RoomPlantMovementMode.NoWind
        )
        {
            return RoomPlantMovementMode
                .NoWind
                .ToString();
        }

        if (!EnvironmentActive)
        {
            return
                "CompiledBridgeUnavailable/VanillaGlobalWind";
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

internal readonly record struct ManagedRoomWindRegistration(
    ManagedRoomType RoomType,
    RoomWindTargetKind Targets
);

internal sealed class StillGreenhousesRoomWindUniformRenderer :
    IRenderer
{
    internal const int MaxUploadedPositions = 128;

    // First-pass fair budgets. Any unused slots are redistributed globally by
    // nearest distance so all 128 uniforms can still be used.
    internal const int GreenhouseReservedPositionBudget = 64;
    internal const int CellarReservedPositionBudget = 32;
    internal const int RoomReservedPositionBudget = 32;

    // One state per ManagedRoomType:
    // 0 = Greenhouse, 1 = Cellar, 2 = normal Room.
    internal const int UploadedRoomTypeStateCount =
        StillGreenhousesRoomWindEnvironment
            .RoomTypeStateCount;

    // A one-percentage-point triangular step every 12 scaled game seconds.
    // Ranges narrower than 1% use the full configured span as their step.
    internal const float AmbientWindStepIntervalSeconds =
        12f;

    private const int SnapshotRefreshMs = 250;
    private const int ProgramRediscoveryMs = 1000;

    private static readonly EnumShaderProgram[] TargetPrograms =
    [
        // Active vegetation consumers. The supplied Vanilla top-level shaders
        // call applyVertexWarping() in these passes.
        EnumShaderProgram.Chunkopaque,
        EnumShaderProgram.Chunktransparent,
        EnumShaderProgram.Chunkshadowmap,
        EnumShaderProgram.Chunkshadowmap_NoSSBOs,

        // Liquid consumers use the separately patched applyLiquidWarping().
        EnumShaderProgram.Chunkliquid,
        EnumShaderProgram.Chunkliquiddepth
    ];

    internal const int VegetationTargetProgramCount = 4;
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
    private static int uploadedRoomStateCount;
    private static int activeProgramCount;
    private static int activeVegetationProgramCount;
    private static int activeLiquidProgramCount;
    private static int requiredChunkOpaqueBound;
    private static int snapshotRevision;
    private static int uniformBridgeReady;

    private static float greenhouseCurrentPercent;
    private static float cellarCurrentPercent;
    private static float roomCurrentPercent;

    private readonly ICoreClientAPI api;

    private readonly RoomWindRuntimeState[]
        roomTypeStates;

    private readonly List<ShaderProgramBinding>
        programBindings = new();

    private RoomWindTopologySnapshot topology =
        RoomWindTopologySnapshot.Empty;

    private long nextSnapshotRefreshMs;
    private long nextProgramDiscoveryMs;
    private int lastProgramDiagnosticHash =
        int.MinValue;

    private int disposed;

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

    internal static int SnapshotRevision =>
        Volatile.Read(
            ref snapshotRevision
        );

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

    public double RenderOrder => 0.99d;

    public int RenderRange => int.MaxValue;

    internal StillGreenhousesRoomWindUniformRenderer(
        ICoreClientAPI api
    )
    {
        this.api = api;

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
                uniforms,
                phaseOffsetSeconds: 0f
            ),
            CreateRoomTypeState(
                config,
                ManagedRoomType.Cellar,
                uniforms,
                phaseOffsetSeconds:
                    AmbientWindStepIntervalSeconds / 3f
            ),
            CreateRoomTypeState(
                config,
                ManagedRoomType.Room,
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
        DefaultShaderUniforms uniforms,
        float phaseOffsetSeconds
    )
    {
        RoomWindRange range =
            StillGreenhousesRoomWindEnvironment
                .GetRange(
                    config,
                    roomType
                );

        return new RoomWindRuntimeState(
            roomType,
            range,
            uniforms.WindWaveCounter,
            uniforms.WindWaveCounterHighFreq,
            phaseOffsetSeconds
        );
    }

    internal static void RegisterPosition(
        BlockPos pos,
        GreenhouseRegion room
    ) =>
        RegisterTargetPosition(
            pos,
            room,
            RoomWindTargetKind.Vegetation
        );

    internal static void RegisterWaterPosition(
        BlockPos pos,
        GreenhouseRegion room
    ) =>
        RegisterTargetPosition(
            pos,
            room,
            RoomWindTargetKind.Water
        );

    private static void RegisterTargetPosition(
        BlockPos pos,
        GreenhouseRegion room,
        RoomWindTargetKind target
    )
    {
        if (
            StillGreenhousesClientSystem
                .PlantMovementMode
                != RoomPlantMovementMode.VanillaLowWind
        )
        {
            RemovePosition(pos);

            return;
        }

        ManagedVegetationShaderPosition key =
            ManagedVegetationShaderPosition.From(pos);

        ChunkKey chunkKey =
            ChunkKey.From(pos);

        lock (RegistrationSync)
        {
            RegisteredPositions.AddOrUpdate(
                key,
                _ =>
                    new ManagedRoomWindRegistration(
                        room.RoomType,
                        target
                    ),
                (_, existing) =>
                    new ManagedRoomWindRegistration(
                        room.RoomType,
                        existing.Targets | target
                    )
            );

            RegisteredPositionsByChunk
                .GetOrAdd(
                    chunkKey,
                    _ => new ConcurrentDictionary<
                        ManagedVegetationShaderPosition,
                        byte
                    >()
                )[key] = 0;
        }
    }

    internal static void RemovePosition(
        BlockPos pos
    ) =>
        RemoveRegisteredPosition(
            ManagedVegetationShaderPosition.From(pos)
        );

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

            return removed;
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

            return removed;
        }
    }

    internal static void ClearRegisteredPositions()
    {
        lock (RegistrationSync)
        {
            RegisteredPositions.Clear();
            RegisteredPositionsByChunk.Clear();
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
            ref uploadedRoomStateCount,
            0
        );
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
                + ProgramRediscoveryMs;
        }
        else if (
            elapsedMs
                >= nextProgramDiscoveryMs
        )
        {
            // Periodic rediscovery automatically rebinds after later graphics-
            // setting shader reloads and records any program-id/interface change.
            nextProgramDiscoveryMs =
                elapsedMs
                + ProgramRediscoveryMs;

            DiscoverPrograms(
                "PeriodicRenderVerification"
            );
        }

        UploadRoomWindState();
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

        float glitchStrength =
            api.Render.ShaderUniforms
                .GlitchStrength;

        foreach (
            RoomWindRuntimeState state
            in roomTypeStates
        )
        {
            state.Advance(
                scaledDt,
                glitchStrength
            );
        }

        PublishStateMetrics();
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
    }

    private void RefreshTopologySnapshot()
    {
        IClientPlayer? player =
            api.World.Player;

        if (player?.Entity == null)
        {
            SetTopologySnapshot(
                Array.Empty<PositionCandidate>(),
                totalValid: 0
            );

            return;
        }

        double playerX =
            player.Entity.Pos.X;

        double playerY =
            player.Entity.Pos.Y;

        double playerZ =
            player.Entity.Pos.Z;

        int playerDimension =
            player.Entity.Pos.Dimension;

        List<PositionCandidate> candidates = new();
        int totalValid = 0;

        foreach (
            KeyValuePair<
                ManagedVegetationShaderPosition,
                ManagedRoomWindRegistration
            > entry
            in RegisteredPositions
        )
        {
            ManagedVegetationShaderPosition registered =
                entry.Key;

            if (
                registered.Dimension
                    != playerDimension
            )
            {
                continue;
            }

            BlockPos blockPos =
                registered.ToBlockPos();

            if (!StillGreenhousesClientSystem.TryGetCachedGreenhouse(
                    blockPos,
                    requestIfUnknown: false,
                    out GreenhouseRegion? room
                ))
            {
                if (StillGreenhousesClientSystem
                        .HasCompleteCachedSnapshot(blockPos))
                {
                    RemoveRegisteredPosition(
                        registered
                    );
                }

                continue;
            }

            Block block =
                api.World.BlockAccessor.GetBlock(
                    blockPos
                );

            StillGreenhousesConfig? config =
                StillGreenhousesClientSystem.Config;

            RoomWindTargetKind validTargets =
                RoomWindTargetKind.None;

            if (
                (
                    entry.Value.Targets
                    & RoomWindTargetKind.Vegetation
                ) != 0
                && StillGreenhousesShared.IsVegetationCandidate(
                    block,
                    config
                )
            )
            {
                validTargets |=
                    RoomWindTargetKind.Vegetation;
            }

            if (
                (
                    entry.Value.Targets
                    & RoomWindTargetKind.Water
                ) != 0
                && config?.ApplyToWater == true
                && StillGreenhousesShared.IsWaterSurfaceSourceBlock(
                    api.World.BlockAccessor,
                    blockPos
                )
            )
            {
                validTargets |=
                    RoomWindTargetKind.Water;
            }

            if (validTargets == RoomWindTargetKind.None)
            {
                RemoveRegisteredPosition(
                    registered
                );

                continue;
            }

            if (!RegisteredPositions.TryUpdate(
                    registered,
                    new ManagedRoomWindRegistration(
                        room.RoomType,
                        validTargets
                    ),
                    entry.Value
                ))
            {
                continue;
            }

            totalValid++;

            double dx =
                registered.X
                + 0.5d
                - playerX;

            double dy =
                registered.Y
                + 0.5d
                - playerY;

            double dz =
                registered.Z
                + 0.5d
                - playerZ;

            candidates.Add(
                new PositionCandidate(
                    registered,
                    room.RoomType,
                    validTargets,
                    dx * dx
                    + dy * dy
                    + dz * dz
                )
            );
        }

        candidates.Sort(
            (left, right) =>
                left.DistanceSquared.CompareTo(
                    right.DistanceSquared
                )
        );

        SetTopologySnapshot(
            candidates,
            totalValid
        );
    }

    private void SetTopologySnapshot(
        IReadOnlyList<PositionCandidate> candidates,
        int totalValid
    )
    {
        List<PositionCandidate> selectedCandidates =
            SelectBudgetedPositions(candidates);

        int selectedCount =
            selectedCandidates.Count;

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

            positionValues[offset] =
                candidate.Position.X;

            positionValues[offset + 1] =
                candidate.Position.Y;

            positionValues[offset + 2] =
                candidate.Position.Z;

            int packedTarget =
                stateIndex
                + (
                    (int)candidate.Targets
                    * StillGreenhousesRoomWindEnvironment
                        .RoomTypeStateCount
                );

            positionValues[offset + 3] =
                packedTarget;

            hash = AddHash(
                hash,
                candidate.Position.X
            );

            hash = AddHash(
                hash,
                candidate.Position.Y
            );

            hash = AddHash(
                hash,
                candidate.Position.Z
            );

            hash = AddHash(
                hash,
                packedTarget
            );
        }

        hash = AddHash(
            hash,
            selectedCount
        );

        if (
            hash == topology.ContentHash
            && totalValid == topology.TotalValid
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
                totalValid
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
                totalValid,
                hash,
                revision
            );

        string positionPreview =
            selectedCount == 0
                ? "<none>"
                : string.Join(
                    "|",
                    selectedCandidates
                        .Take(16)
                        .Select(candidate =>
                            $"{candidate.Position.X},{candidate.Position.Y},{candidate.Position.Z}:" +
                            $"{candidate.RoomType}/{candidate.Targets}/d2={candidate.DistanceSquared:0.##}"
                        )
                );

        StillGreenhousesClientSystem.DebugLiteral(
            "[StillGreenhouses] ROOM WIND POSITION SNAPSHOT " +
            $"revision={revision}; " +
            $"registered={RegisteredPositions.Count}; " +
            $"valid={totalValid}; " +
            $"selected={selectedCount}/{MaxUploadedPositions}; " +
            $"matchStrategy=VanillaPlayerReferenceAbsoluteBlockAabb; " +
            $"debugVisualProof={StillGreenhousesClientSystem.Config?.DebugRoomWindVisualProof == true}; " +
            $"positions={positionPreview}"
        );

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
            totalValid
        );
    }

    private static List<PositionCandidate>
        SelectBudgetedPositions(
            IReadOnlyList<PositionCandidate> candidates
        )
    {
        List<PositionCandidate> selected =
            new(MaxUploadedPositions);

        HashSet<ManagedVegetationShaderPosition>
            selectedPositions = new();

        AddReservedPositions(
            candidates,
            ManagedRoomType.Greenhouse,
            GreenhouseReservedPositionBudget,
            selected,
            selectedPositions
        );

        AddReservedPositions(
            candidates,
            ManagedRoomType.Cellar,
            CellarReservedPositionBudget,
            selected,
            selectedPositions
        );

        AddReservedPositions(
            candidates,
            ManagedRoomType.Room,
            RoomReservedPositionBudget,
            selected,
            selectedPositions
        );

        // Redistribute every unused reserved slot to the globally nearest
        // remaining candidate, regardless of room type.
        foreach (
            PositionCandidate candidate
            in candidates
        )
        {
            if (selected.Count >= MaxUploadedPositions)
            {
                break;
            }

            if (selectedPositions.Add(candidate.Position))
            {
                selected.Add(candidate);
            }
        }

        return selected;
    }

    private static void AddReservedPositions(
        IReadOnlyList<PositionCandidate> candidates,
        ManagedRoomType roomType,
        int budget,
        List<PositionCandidate> selected,
        HashSet<ManagedVegetationShaderPosition> selectedPositions
    )
    {
        int added = 0;

        foreach (
            PositionCandidate candidate
            in candidates
        )
        {
            if (
                added >= budget
                || selected.Count >= MaxUploadedPositions
            )
            {
                return;
            }

            if (
                candidate.RoomType != roomType
                || !selectedPositions.Add(candidate.Position)
            )
            {
                continue;
            }

            selected.Add(candidate);
            added++;
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

                    string? positionsUniformName =
                        ResolveArrayUniformName(
                            program,
                            StillGreenhousesRoomWindShaderPatch
                                .PositionsUniformName
                        );

                    positionsFound =
                        positionsUniformName != null;

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
                            "positions-uniform-missing";
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

        int vegetationProgramCount =
            programBindings.Count(
                binding =>
                    IsVegetationProgram(
                        binding.Target
                    )
            );

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

        if (diagnosticsChanged)
        {
            api.Logger.Notification(
                "[StillGreenhouses] ROOM WIND UNIFORM PROGRAMS " +
                $"stage={verificationStage}; " +
                $"assetSourceHash={StillGreenhousesRoomWindShaderPatch.ResolvedOverrideSourceHash}; " +
                $"activePrograms={programBindings.Count}; " +
                $"targetPrograms={TargetProgramCount}; " +
                $"activeVegetationPrograms={vegetationProgramCount}/{VegetationTargetProgramCount}; " +
                $"activeLiquidPrograms={liquidProgramCount}/{LiquidTargetProgramCount}; " +
                $"topsoilWindConsumer=False; " +
                $"requiredChunkOpaqueBound={chunkOpaqueBound}; " +
                $"compiledBridgeVerified={chunkOpaqueBound}; " +
                $"shaderReloadAttempted={StillGreenhousesRoomWindShaderPatch.ShaderReloadAttempted}; " +
                $"shaderReloadSucceeded={StillGreenhousesRoomWindShaderPatch.ShaderReloadSucceeded}; " +
                $"maxUploadedPositions={MaxUploadedPositions}; " +
                $"roomTypeStates={UploadedRoomTypeStateCount}"
            );
        }
    }

    private static bool IsVegetationProgram(
        EnumShaderProgram target
    ) =>
        target
            is EnumShaderProgram.Chunkopaque
            or EnumShaderProgram.Chunktransparent
            or EnumShaderProgram.Chunkshadowmap
            or EnumShaderProgram.Chunkshadowmap_NoSSBOs;

    private static bool IsLiquidProgram(
        EnumShaderProgram target
    ) =>
        target
            is EnumShaderProgram.Chunkliquid
            or EnumShaderProgram.Chunkliquiddepth;

    private static string ResolveProgramRole(
        EnumShaderProgram target
    ) =>
        IsVegetationProgram(target)
            ? "Vegetation"
            : IsLiquidProgram(target)
                ? "Liquid"
                : "IncludeOnly";

    private static string? ResolveArrayUniformName(
        IShaderProgram program,
        string baseName
    )
    {
        if (program.HasUniform(baseName))
        {
            return baseName;
        }

        string arrayZeroName =
            baseName + "[0]";

        return program.HasUniform(arrayZeroName)
            ? arrayZeroName
            : null;
    }

    private void UploadRoomWindState()
    {
        float[] stateValues =
            BuildStateValues();

        bool anyUploadSucceeded = false;

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

                continue;
            }

            try
            {
                binding.Program.Use();

                if (
                    binding.LastUploadedTopologyRevision
                        != topology.Revision
                )
                {
                    binding.Program.Uniform(
                        StillGreenhousesRoomWindShaderPatch
                            .PositionCountUniformName,
                        topology.PositionCount
                    );

                    binding.Program.Uniform(
                        StillGreenhousesRoomWindShaderPatch
                            .StateCountUniformName,
                        UploadedRoomTypeStateCount
                    );

                    if (
                        topology.PositionCount
                            > 0
                    )
                    {
                        binding.Program.Uniforms4(
                            binding.PositionsUniformName,
                            topology.PositionCount,
                            topology.PositionValues
                        );
                    }

                    binding.LastUploadedTopologyRevision =
                        topology.Revision;
                }

                if (binding.DebugVisualProofUniformFound)
                {
                    binding.Program.Uniform(
                        StillGreenhousesRoomWindShaderPatch
                            .DebugVisualProofUniformName,
                        StillGreenhousesClientSystem.Config
                            ?.DebugRoomWindVisualProof == true
                                ? 1
                                : 0
                    );
                }

                binding.Program.Uniforms4(
                    binding.StatesUniformName,
                    UploadedRoomTypeStateCount,
                    stateValues
                );

                binding.Program.Stop();

                anyUploadSucceeded = true;
            }
            catch (Exception e)
            {
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

                programBindings.RemoveAt(i);
            }
        }

        bool chunkOpaqueBound =
            programBindings.Any(
                binding =>
                    binding.Target
                        == EnumShaderProgram.Chunkopaque
                    && !binding.Program.Disposed
            );

        int vegetationProgramCount =
            programBindings.Count(
                binding =>
                    IsVegetationProgram(
                        binding.Target
                    )
            );

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

        Volatile.Write(
            ref uploadedRoomStateCount,
            anyUploadSucceeded
                ? UploadedRoomTypeStateCount
                : 0
        );
    }

    private float[] BuildStateValues()
    {
        float[] values =
            new float[
                UploadedRoomTypeStateCount * 4
            ];

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

            values[offset + 1] =
                snapshot.WindWaveCounter;

            values[offset + 2] =
                snapshot.WindWaveCounterHighFreq;

            values[offset + 3] =
                snapshot.CurrentPercent;
        }

        return values;
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

        Volatile.Write(
            ref activeProgramCount,
            0
        );

        Volatile.Write(
            ref activeVegetationProgramCount,
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
    }

    private sealed class RoomWindRuntimeState
    {
        internal ManagedRoomType RoomType { get; }

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
            RoomWindRange range,
            float initialWindWaveCounter,
            float initialWindWaveCounterHighFreq,
            float phaseOffsetSeconds
        )
        {
            RoomType = roomType;
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

        internal void Advance(
            float scaledDt,
            float glitchStrength
        )
        {
            if (scaledDt <= 0f)
            {
                return;
            }

            AdvanceWindTarget(
                scaledDt
            );

            float windSpeed =
                WindSpeed;

            // Mirrors the surface-wind-driven portions of
            // DefaultShaderUniforms.Update(), with this room-type state's
            // current wind speed substituted for the global surface wind.
            windWaveCounter =
                (
                    windWaveCounter
                    + (
                        0.5f
                        + 2f
                        * windSpeed
                        * (1f - glitchStrength)
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
                        * (1f - glitchStrength)
                    )
                    * scaledDt
                )
                % 6000f;
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
        double DistanceSquared
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
        int TotalValid,
        ulong ContentHash,
        int Revision
    )
    {
        internal static RoomWindTopologySnapshot Empty { get; } =
            new(
                Array.Empty<float>(),
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
// 0.10.16a therefore keeps Vanilla's original WindMode/WindData switch intact,
// substitutes the room-local wind speed and counters inside applyVertexWarping,
// and wraps the active top-level call sites. The wrapper scales the complete
// Vanilla vertex-warp delta by the room's normalized wind speed. This also
// scales modes whose branch math hardcodes strength or sets strength to zero.
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

    internal const string StateCountUniformName =
        "stillGreenhousesRoomWindStateCount";

    internal const string StatesUniformName =
        "stillGreenhousesRoomWindStates";

    internal const string DebugVisualProofUniformName =
        "stillGreenhousesRoomWindDebugVisualProof";

    private const string TargetFunctionName =
        "applyVertexWarping";

    private const string PatchMarker =
        "StillGreenhouses Vanilla room wind delta scale bridge";

    private const string CallSiteWrapperFunctionName =
        "applyStillGreenhousesRoomWindScale";

    private const string CallSitePatchMarker =
        "StillGreenhouses Vanilla warp room amplitude callsite";

    private const string TargetLiquidFunctionName =
        "applyLiquidWarping";

    private const string LiquidPatchMarker =
        "StillGreenhouses room liquid low wind bridge";

    private const string OverrideDataFolder =
        "StillGreenhouses";

    private const string OverrideOriginFolder =
        "shader-origin-0.10.16a";

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
        "VanillaWarpThenRoomAmplitudeMix";

    internal static string AbsolutePositionStrategy =>
        "VanillaPlayerReferenceAbsoluteBlockAabb";

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
                + "topology=VanillaWarpThenRoomAmplitudeMix; "
                + "absolutePosition=worldPos+playerpos"
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
            + "strategy=vanilla-warp-then-room-amplitude-mix; "
            + "absolutePosition=worldPos+playerpos; "
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
            + $"maxUploadedPositions={StillGreenhousesRoomWindUniformRenderer.MaxUploadedPositions}; "
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

    internal static bool ReloadCompiledShaders(
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
                ref shaderReloadFailureReason,
                "shader-override-not-resolved"
            );

            api.Logger.Notification(
                "[StillGreenhouses] ROOM WIND SHADER RELOAD " +
                $"stage={stage}; " +
                "attempted=True; " +
                "success=False; " +
                $"originPrepared={OriginPrepared}; " +
                $"overrideResolved={OverrideResolved}; " +
                $"sourceHash={ResolvedOverrideSourceHash}; " +
                "reason=shader-override-not-resolved"
            );

            return false;
        }

        bool success = false;
        string reason = "<none>";

        try
        {
            success =
                api.Shader.ReloadShaders();

            if (!success)
            {
                reason =
                    "ReloadShaders-returned-false";
            }
        }
        catch (Exception e)
        {
            reason =
                $"{e.GetType().Name}:{Sanitize(e.Message)}";
        }

        Volatile.Write(
            ref shaderReloadSucceeded,
            success ? 1 : 0
        );

        Volatile.Write(
            ref shaderReloadFailureReason,
            reason
        );

        Volatile.Write(
            ref compiledVerificationPending,
            success ? 1 : 0
        );

        api.Logger.Notification(
            "[StillGreenhouses] ROOM WIND SHADER RELOAD " +
            $"stage={stage}; " +
            "attempted=True; " +
            $"success={success}; " +
            $"originPrepared={OriginPrepared}; " +
            $"overrideResolved={OverrideResolved}; " +
            $"sourceHash={ResolvedOverrideSourceHash}; " +
            $"reason={reason}"
        );

        return success;
    }

    internal static void RemoveAssetOrigin(
        ICoreAPI api
    )
    {
        string originPath =
            OverrideOriginPath;

        if (originPath == "<none>")
        {
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
            + "topology=VanillaWarpThenRoomAmplitudeMix";

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
            + "bool stillGreenhousesRoomWind = "
            + "stillGreenhousesResolveRoomWindState("
            + "worldPos, 1, stillGreenhousesRoomState);\n"
            + indent
            + "float stillGreenhousesWindSpeed = "
            + "stillGreenhousesRoomWind ? "
            + "stillGreenhousesRoomState.x : windSpeed;\n"
            + indent
            + "float stillGreenhousesWindWaveCounter = "
            + "stillGreenhousesRoomWind ? "
            + "stillGreenhousesRoomState.y : windWaveCounter;\n"
            + indent
            + "float stillGreenhousesWindWaveCounterHighFreq = "
            + "stillGreenhousesRoomWind ? "
            + "stillGreenhousesRoomState.z : windWaveCounterHighFreq;\n";

        string rewrittenFunction =
            functionSource.Insert(
                windDataStatement.EndExclusive,
                environmentBlock
            );

        int environmentEnd =
            windDataStatement.EndExclusive
            + environmentBlock.Length;

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

        string uniformBlock =
            "\n"
            + "// "
            + PatchMarker
            + " uniforms\n"
            + "uniform int "
            + PositionCountUniformName
            + ";\n"
            + "uniform vec4 "
            + PositionsUniformName
            + "["
            + StillGreenhousesRoomWindUniformRenderer
                .MaxUploadedPositions
            + "];\n"
            + "uniform int "
            + StateCountUniformName
            + ";\n"
            + "uniform vec4 "
            + StatesUniformName
            + "["
            + StillGreenhousesRoomWindEnvironment
                .RoomTypeStateCount
            + "];\n"
            + "uniform int "
            + DebugVisualProofUniformName
            + ";\n\n";

        string helperBlock =
            "\n"
            + "// "
            + PatchMarker
            + " helpers\n"
            + "bool stillGreenhousesResolveRoomWindState("
            + "vec4 worldPos, int requiredTargetFlag, out vec4 roomState) {\n"
            + "    roomState = vec4(0.0);\n"
            + "    vec3 absolutePos = worldPos.xyz + playerpos;\n"
            + "    bool found = false;\n"
            + "    float bestDistanceSquared = 1e30;\n"
            + "    for (int index = 0; "
            + "index < stillGreenhousesRoomWindPositionCount; index++) {\n"
            + "        vec4 entry = "
            + "stillGreenhousesRoomWindPositions[index];\n"
            + "        vec3 blockMin = entry.xyz - vec3(0.001);\n"
            + "        vec3 blockMax = entry.xyz + vec3(1.001);\n"
            + "        bool inside = all(greaterThanEqual("
            + "absolutePos, blockMin)) && "
            + "all(lessThanEqual(absolutePos, blockMax));\n"
            + "        if (!inside) continue;\n"
            + "        int packedTarget = int(entry.w + 0.5);\n"
            + "        int stateIndex = packedTarget % "
            + StillGreenhousesRoomWindEnvironment.RoomTypeStateCount
            + ";\n"
            + "        int targetFlags = packedTarget / "
            + StillGreenhousesRoomWindEnvironment.RoomTypeStateCount
            + ";\n"
            + "        bool compatibleTarget = "
            + "(targetFlags & requiredTargetFlag) != 0;\n"
            + "        bool validState = stateIndex >= 0 && "
            + "stateIndex < stillGreenhousesRoomWindStateCount;\n"
            + "        if (!compatibleTarget || !validState) continue;\n"
            + "        vec3 blockCenter = entry.xyz + vec3(0.5);\n"
            + "        vec3 centerDelta = absolutePos - blockCenter;\n"
            + "        float distanceSquared = dot(centerDelta, centerDelta);\n"
            + "        if (!found || distanceSquared < bestDistanceSquared) {\n"
            + "            roomState = "
            + "stillGreenhousesRoomWindStates[stateIndex];\n"
            + "            bestDistanceSquared = distanceSquared;\n"
            + "            found = true;\n"
            + "        }\n"
            + "    }\n"
            + "    return found;\n"
            + "}\n\n"
            + "vec4 "
            + CallSiteWrapperFunctionName
            + "(int renderFlags, vec4 basePos, vec4 warpedPos) {\n"
            + "    if ((renderFlags & WindModeBitMask) <= 0) "
            + "return warpedPos;\n"
            + "    int windMode = "
            + "(renderFlags >> WindModePosition) & 0xF;\n"
            + "    if (windMode == 6 || windMode == 12) "
            + "return warpedPos;\n"
            + "    vec4 roomState = vec4(0.0);\n"
            + "    if (!stillGreenhousesResolveRoomWindState("
            + "basePos, 1, roomState)) return warpedPos;\n"
            + "    float roomAmplitude = clamp(roomState.x, 0.0, 2.0);\n"
            + "    vec4 result = warpedPos;\n"
            + "    result.xyz = basePos.xyz + "
            + "(warpedPos.xyz - basePos.xyz) * roomAmplitude;\n"
            + "    if ("
            + DebugVisualProofUniformName
            + " != 0) {\n"
            + "        result.x += 0.35 * sin(roomState.y * 4.0);\n"
            + "    }\n"
            + "    return result;\n"
            + "}\n\n";

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
            + "strategy=vanilla-warp-room-state-plus-delta-scale; "
            + "absolutePosition=worldPos+playerpos";

        capturedFunctionSource =
            rewrittenFunction;

        return true;
    }

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
            + "strategy=room-type-liquid-amplitude";

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
    // the complete Vanilla liquid-warp delta by the room wind scalar. This
    // mirrors the vegetation wrapper's room-owned amplitude semantics.
    private static string BuildPatchedLiquidFunction() =>
        "vec4 applyLiquidWarping(bool windAffected, vec4 worldPos, float div) {\n"
        + "\t#if WAVINGSTUFF == 1\n"
        + "\t// " + LiquidPatchMarker + "\n"
        + "\tvec4 stillGreenhousesLiquidBasePos = worldPos;\n"
        + "\tbool stillGreenhousesRoomLiquid = false;\n"
        + "\tvec4 stillGreenhousesLiquidRoomState = vec4(0.0);\n"
        + "\tvec3 stillGreenhousesLiquidAbsPos = worldPos.xyz + playerpos;\n"
        + "\tfloat stillGreenhousesLiquidBestDistanceSquared = 1e30;\n"
        + "\tfor (int stillGreenhousesLiquidIndex = 0; "
        + "stillGreenhousesLiquidIndex < stillGreenhousesRoomWindPositionCount; "
        + "stillGreenhousesLiquidIndex++) {\n"
        + "\t\tvec4 stillGreenhousesLiquidEntry = "
        + "stillGreenhousesRoomWindPositions[stillGreenhousesLiquidIndex];\n"
        + "\t\tvec3 stillGreenhousesLiquidMin = "
        + "stillGreenhousesLiquidEntry.xyz - vec3(0.001);\n"
        + "\t\tvec3 stillGreenhousesLiquidMax = "
        + "stillGreenhousesLiquidEntry.xyz + vec3(1.001);\n"
        + "\t\tbool stillGreenhousesLiquidInside = "
        + "all(greaterThanEqual(stillGreenhousesLiquidAbsPos, "
        + "stillGreenhousesLiquidMin)) && "
        + "all(lessThanEqual(stillGreenhousesLiquidAbsPos, "
        + "stillGreenhousesLiquidMax));\n"
        + "\t\tif (!stillGreenhousesLiquidInside) continue;\n"
        + "\t\tint stillGreenhousesLiquidPackedTarget = "
        + "int(stillGreenhousesLiquidEntry.w + 0.5);\n"
        + "\t\tint stillGreenhousesLiquidStateIndex = "
        + "stillGreenhousesLiquidPackedTarget % "
        + StillGreenhousesRoomWindEnvironment.RoomTypeStateCount
        + ";\n"
        + "\t\tint stillGreenhousesLiquidTargetFlags = "
        + "stillGreenhousesLiquidPackedTarget / "
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
        + "\t\tvec3 stillGreenhousesLiquidCenter = "
        + "stillGreenhousesLiquidEntry.xyz + vec3(0.5);\n"
        + "\t\tvec3 stillGreenhousesLiquidCenterDelta = "
        + "stillGreenhousesLiquidAbsPos - stillGreenhousesLiquidCenter;\n"
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
