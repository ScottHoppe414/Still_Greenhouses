/*
version 0.18.0
*/

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace StillGreenhouses;

public sealed partial class StillGreenhousesClientSystem : ModSystem
{
    private const string HarmonyId = "stillgreenhouses.patches";
    private const string ConfigLibSavedEvent =
        "configlib:stillgreenhouses:config-saved";

    // Background workers explicitly yield between main-thread handoffs. Debug
    // logging previously supplied this pacing accidentally, so release builds
    // could enqueue work in a different order than diagnostic builds.
    private const int WorkerHandoffDelayMs = 16;
    private const int BusyPipelineRetryDelayMs = 10;

    internal static ICoreClientAPI? Capi { get; private set; }
    internal static StillGreenhousesConfig? Config { get; private set; }

    internal static bool VisualEffectEnabled =>
        Config?.Enabled == true
        && Config.ClientVisualEnabled;

    internal static RoomPlantMovementMode PlantMovementMode =>
        Config == null
            ? RoomPlantMovementMode.VanillaLowWind
            : StillGreenhousesShared
                .ResolveRoomPlantMovementMode(Config);

    internal static bool RoomWindShaderOverrideReady =>
        StillGreenhousesRoomWindEnvironment
            .ShaderOverrideReady;

    internal static bool RoomWindShaderReloadAttempted =>
        StillGreenhousesRoomWindEnvironment
            .ShaderReloadAttempted;

    internal static bool RoomWindShaderReloadSucceeded =>
        StillGreenhousesRoomWindEnvironment
            .ShaderReloadSucceeded;

    internal static bool RoomWindCompiledBridgeVerified =>
        StillGreenhousesRoomWindEnvironment
            .CompiledBridgeVerified;

    internal static bool RoomWindUniformBridgeReady =>
        StillGreenhousesRoomWindEnvironment
            .UniformBridgeReady;

    internal static bool RoomWindEnvironmentActive =>
        StillGreenhousesRoomWindEnvironment
            .EnvironmentActive;

    internal static void DebugLiteral(string message)
    {
        if (Config?.DebugLogging != true)
        {
            return;
        }

        try
        {
            Capi?.Logger.Debug("{0}", message);
        }
        catch
        {
            // Diagnostics must never change greenhouse rendering behavior.
        }
    }

    internal static void WarningLiteral(string message)
    {
        try
        {
            Capi?.Logger.Warning("{0}", message);
        }
        catch
        {
            // Logging must never escape a visual-only safety path.
        }
    }

    private static IClientNetworkChannel? clientChannel;

    private static readonly ConcurrentDictionary<
        ChunkKey,
        ClientChunkSnapshot
    > ChunkSnapshots = new();

    private static readonly ConcurrentDictionary<
        ChunkKey,
        byte
    > QueuedDiscoveryChunks = new();

    private static readonly ConcurrentDictionary<
        ChunkKey,
        long
    > PendingChunkRequests = new();

    private static readonly ConcurrentDictionary<
        ChunkKey,
        byte
    > ScheduledRetries = new();

    private static readonly ConcurrentDictionary<
        ChunkKey,
        byte
    > IgnoreNextCompleteNegativeSnapshot = new();

    private static readonly ConcurrentDictionary<
        ChunkKey,
        ObservedVegetationPos
    > RepresentativeVegetationByChunk = new();

    private static readonly ConcurrentDictionary<
        RenderPathKey,
        byte
    > KnownRenderPaths = new();

    private static readonly ConcurrentDictionary<
        GreenhousePolicyLogKey,
        byte
    > KnownGreenhousePolicies = new();

    private static readonly ConcurrentDictionary<
        WindAdjustmentLogKey,
        byte
    > KnownWindAdjustments = new();

    private static readonly ConcurrentDictionary<
        FlowerMeshProcessLogKey,
        byte
    > KnownFlowerMeshProcesses = new();

    private static readonly ConcurrentDictionary<
        string,
        byte
    > KnownUnclassifiedWindBlocks = new();

    private static readonly ConcurrentDictionary<
        ContainerWindTargetLogKey,
        byte
    > KnownContainerWindTargets = new();

    private static readonly ConcurrentDictionary<
        string,
        byte
    > KnownWindMeshFallbackTargets = new();

    private static readonly ConcurrentQueue<
        PendingWaterScanBatch
    > PendingWaterScanBatches = new();

    private static readonly ConcurrentDictionary<
        ChunkKey,
        long
    > LatestQueuedWaterScanRevision = new();

    private static readonly ConcurrentQueue<
        ChunkKey
    > PendingChunkRedrawQueue = new();

    private static readonly ConcurrentDictionary<
        ChunkKey,
        string
    > PendingChunkRedraws = new();

    private static long playerChunkTickListenerId;
    private static long foregroundWorkTickListenerId;
    private static long cachePruneListenerId;
    private static long debugSummaryListenerId;
    private static int suppressBlockChanged;

    private static long waterScanPositionsChecked;
    private static long waterScanPositionsRegistered;
    private static long waterScanBatchesDropped;
    private static long chunkRedrawRequestsQueued;
    private static long chunkRedrawsProcessed;

    private static CancellationTokenSource? workerCancellation;
    private static Task? discoveryWorkerTask;
    private static Task? snapshotPreparationWorkerTask;

    private static Channel<QueuedSnapshotPacket>?
        snapshotPreparationQueue;

    private static PlayerChunkSnapshot?
        latestPlayerChunkSnapshot;

    private static int clientWorldGeneration;
    private static int snapshotPipelineInFlight;
    private static int requestBatchHandoffInFlight;
    private static long nextPendingRequestLeaseId;

    private Harmony? harmony;
    private ICoreClientAPI? lifecycleClientApi;
    private StillGreenhousesRoomWindUniformRenderer?
        roomWindUniformRenderer;

    public override bool ShouldLoad(EnumAppSide side) =>
        side == EnumAppSide.Client;

    public override void Start(ICoreAPI api)
    {
        api.Network
            .RegisterChannel(StillGreenhousesNetwork.ChannelName)
            .RegisterMessageType<GreenhouseChunkBatchRequest>()
            .RegisterMessageType<GreenhouseChunkSnapshot>()
            .RegisterMessageType<RoomInspectionRequest>()
            .RegisterMessageType<RoomInspectionResponse>();

        if (api is ICoreClientAPI clientApi)
        {
            lifecycleClientApi =
                clientApi;

            // Shader array capacity is a restart-only config choice, so load
            // it before the generated shader origin is prepared during the
            // early asset lifecycle.
            Config = StillGreenhousesShared.LoadConfig(
                clientApi,
                storeNormalizedConfig: false
            );

            // Generate and register the game-domain shader origin before normal
            // asset loading resolves shader includes. The source is structurally
            // patched from the exact base game vertexwarp.vsh available to this
            // client rather than shipping a hardcoded full shader copy.
            StillGreenhousesRoomWindShaderPatch
                .PrepareAssetOrigin(clientApi);

            clientApi.Event.BlockTexturesLoaded +=
                OnBlockTexturesLoaded;
        }
    }

    public override void AssetsLoaded(
        ICoreAPI api
    )
    {
        StillGreenhousesRoomWindShaderPatch
            .VerifyResolvedOverrideAsset(
                api,
                "AssetsLoaded"
            );
    }

    private void OnBlockTexturesLoaded()
    {
        ICoreClientAPI? api =
            lifecycleClientApi
            ?? Capi;

        if (api == null)
        {
            return;
        }

        bool assetReady =
            StillGreenhousesRoomWindShaderPatch
                .VerifyResolvedOverrideAsset(
                    api,
                    "BlockTexturesLoadedBeforeReload"
                );

        if (!assetReady)
        {
            WarningLiteral(
                "[StillGreenhouses] ROOM WIND SHADER LATE RELOAD SKIPPED " +
                "reason=shader-override-origin-not-resolved"
            );

            return;
        }

        // The override origin was registered before normal asset loading and
        // the resolved shader assets already match the prepared source set.
        // Do not globally reload every engine shader here. In a modded client,
        // ReloadShaders() can rebuild animated programs without their native
        // Animation UBO metadata and cause a crash when AnimatableRenderer
        // accesses prog.UBOs["Animation"].
        StillGreenhousesRoomWindShaderPatch
            .FinalizePreparedShaderLifecycle(
                api,
                "BlockTexturesLoaded"
            );
    }

    public override void StartClientSide(
        ICoreClientAPI api
    )
    {
        Capi = api;

        Config ??= StillGreenhousesShared.LoadConfig(
            api,
            storeNormalizedConfig: false
        );

        clientChannel =
            api.Network
                .GetChannel(StillGreenhousesNetwork.ChannelName)
                .SetMessageHandler<GreenhouseChunkSnapshot>(
                    OnChunkSnapshot
                )
                .SetMessageHandler<RoomInspectionResponse>(
                    OnRoomInspectionResponse
                );

        harmony = new Harmony(HarmonyId);

        try
        {
            harmony.PatchAll(
                typeof(StillGreenhousesClientSystem).Assembly
            );

            api.Logger.Notification(
                "[StillGreenhouses] Client Harmony patches applied: " +
                "unified vegetation policy, tall-plant/fruiting-bush paths, " +
                "and generic contained wind-mesh capture."
            );
        }
        catch (Exception e)
        {
            api.Logger.Error(
                $"[StillGreenhouses] Harmony patching FAILED. " +
                $"{e.GetType().Name}: {e.Message}\n{e}"
            );

            throw;
        }

        api.Event.LeaveWorld += OnLeaveWorld;
        api.Event.LevelFinalize += OnLevelFinalize;
        api.Event.BlockChanged += OnBlockChanged;

        // Room inspection is initialized after LevelFinalize instead of
        // during StartClientSide. Applying block highlights while the world
        // and animated renderers are still being initialized can expose
        // partially constructed engine shader programs.

        roomWindUniformRenderer =
            new StillGreenhousesRoomWindUniformRenderer(api);

        api.Event.RegisterRenderer(
            roomWindUniformRenderer,
            EnumRenderStage.Before,
            "stillgreenhouses-room-wind-uniforms"
        );

        UpdatePlayerChunkSnapshot(
            0f
        );

        playerChunkTickListenerId =
            api.Event.RegisterGameTickListener(
                UpdatePlayerChunkSnapshot,
                250
            );

        foregroundWorkTickListenerId =
            api.Event.RegisterGameTickListener(
                ProcessClientForegroundWork,
                Config.ClientForegroundWorkIntervalMs
            );

        StartClientWorkers(api);

        cachePruneListenerId =
            api.Event.RegisterGameTickListener(
                PruneClientCache,
                Config.ClientCachePruneIntervalMs
            );

        debugSummaryListenerId =
            api.Event.RegisterGameTickListener(
                LogClientCacheSummary,
                5000
            );

        api.Event.RegisterEventBusListener(
            OnConfigLibConfigSaved,
            filterByEventName: ConfigLibSavedEvent
        );

        api.Logger.Notification(
                $"[StillGreenhouses] Client loaded v0.18.0. " +
            $"Enabled={Config.Enabled}, " +
            $"ClientVisualEnabled={Config.ClientVisualEnabled}, " +
            $"ShowRoomInspectionOverlay={Config.ShowRoomInspectionOverlay}, " +
            $"VisualEffectEnabled={VisualEffectEnabled}, " +
            $"DebugLogging={Config.DebugLogging}, " +
            $"DebugRoomWindCallSiteProof={Config.DebugRoomWindCallSiteProof}, " +
            $"DebugRoomWindVisualProof={Config.DebugRoomWindVisualProof}, " +
            $"ApplyToGreenhouses={Config.ApplyToGreenhouses}, " +
            $"ApplyToCellars={Config.ApplyToCellars}, " +
            $"ApplyToRooms={Config.ApplyToRooms}, " +
            $"ApplyToCrops={Config.ApplyToCrops}, " +
            $"ApplyToFlowers={Config.ApplyToFlowers}, " +
            $"ApplyToHerbs={Config.ApplyToHerbs}, " +
            $"ApplyToBerryBushes={Config.ApplyToBerryBushes}, " +
            $"ApplyToTallPlants={Config.ApplyToTallPlants}, " +
            $"ApplyToVines={Config.ApplyToVines}, " +
            $"ApplyToFruitTreeLeaves={Config.ApplyToFruitTreeLeaves}, " +
            $"ApplyToOtherVegetation={Config.ApplyToOtherVegetation}, " +
            $"ApplyToGrass={Config.ApplyToGrass}, " +
            $"ApplyToWater={Config.ApplyToWater}, " +
            $"LegacyMaxAffectedPlantsIgnored={Config.MaxAffectedPlants}, " +
            $"LegacyExperimentalExtendedPositionCapacityIgnored={Config.ExperimentalExtendedPositionCapacity}, " +
            "RoomWindTransport=QuarterBlockTextureHash, " +
            $"ClientForegroundWorkIntervalMs={Config.ClientForegroundWorkIntervalMs}, " +
            $"MaxClientWaterChecksPerTick={Config.MaxClientWaterChecksPerTick}, " +
            $"MaxClientChunkRedrawsPerTick={Config.MaxClientChunkRedrawsPerTick}, " +
            $"PlantMovementMode={PlantMovementMode}, " +
            $"GreenhouseWindRange={StillGreenhousesRoomWindEnvironment.DescribeRange(Config, ManagedRoomType.Greenhouse)}, " +
            $"CellarWindRange={StillGreenhousesRoomWindEnvironment.DescribeRange(Config, ManagedRoomType.Cellar)}, " +
            $"RoomWindRange={StillGreenhousesRoomWindEnvironment.DescribeRange(Config, ManagedRoomType.Room)}, " +
            $"GreenhouseWaterWindRange={StillGreenhousesRoomWindEnvironment.DescribeRange(Config, ManagedRoomType.Greenhouse, RoomWindTargetKind.Water)}, " +
            $"CellarWaterWindRange={StillGreenhousesRoomWindEnvironment.DescribeRange(Config, ManagedRoomType.Cellar, RoomWindTargetKind.Water)}, " +
            $"RoomWaterWindRange={StillGreenhousesRoomWindEnvironment.DescribeRange(Config, ManagedRoomType.Room, RoomWindTargetKind.Water)}, " +
            $"RoomWindShaderOriginPrepared={StillGreenhousesRoomWindShaderPatch.OriginPrepared}, " +
            $"RoomWindShaderOriginPriorityIndex={StillGreenhousesRoomWindShaderPatch.OriginPriorityIndex}, " +
            $"RoomWindShaderOverrideReady={RoomWindShaderOverrideReady}, " +
            $"RoomWindShaderOverrideMarkerPresent={StillGreenhousesRoomWindShaderPatch.OverrideMarkerPresent}, " +
            $"RoomWindShaderOverrideSourceHash={StillGreenhousesRoomWindShaderPatch.ResolvedOverrideSourceHash}, " +
            $"RoomWindShaderTopology={StillGreenhousesRoomWindShaderPatch.ShaderTopology}, " +
            $"RoomWindShaderAbsolutePosition={StillGreenhousesRoomWindShaderPatch.AbsolutePositionStrategy}, " +
            $"RoomWindShaderOverrideAssets={StillGreenhousesRoomWindShaderPatch.OverrideAssetCount}/{StillGreenhousesRoomWindShaderPatch.RequiredOverrideAssets}, " +
            $"RoomWindShaderWrappedCalls={StillGreenhousesRoomWindShaderPatch.CallSiteCallsWrapped}, " +
            $"RoomWindShaderReloadAttempted={RoomWindShaderReloadAttempted}, " +
            $"RoomWindShaderReloadSucceeded={RoomWindShaderReloadSucceeded}, " +
            $"RoomWindCompiledBridgeVerified={RoomWindCompiledBridgeVerified}, " +
            $"RoomWindUniformBridgeReady={RoomWindUniformBridgeReady}, " +
            $"RoomWindEnvironmentActive={RoomWindEnvironmentActive}, " +
            $"DiscoveryRequestsPerSecond={Config.MaxClientDiscoveryRequestsPerSecond}, " +
            $"MaxChunkRequestsPerBatch={Config.MaxChunkRequestsPerBatch}, " +
            $"ClientDiscoveryRadiusChunks={Config.ClientDiscoveryRadiusChunks}, " +
            $"MaxQueuedDiscoveryChunks={Config.MaxQueuedDiscoveryChunks}, " +
            $"ClientCacheRadiusChunks={Config.ClientCacheRadiusChunks}."
        );
    }

    internal static bool TryGetCachedGreenhouse(
        BlockPos pos,
        [NotNullWhen(true)] out GreenhouseRegion? greenhouse
    ) =>
        TryGetCachedGreenhouse(
            pos,
            requestIfUnknown: true,
            out greenhouse
        );

    internal static bool TryGetCachedGreenhouse(
        BlockPos pos,
        bool requestIfUnknown,
        [NotNullWhen(true)] out GreenhouseRegion? greenhouse
    )
    {
        greenhouse = null;

        if (Config?.Enabled != true)
        {
            return false;
        }

        ChunkKey chunkKey = ChunkKey.From(pos);

        if (ChunkSnapshots.TryGetValue(
                chunkKey,
                out ClientChunkSnapshot? snapshot
            ))
        {
            foreach (
                GreenhouseRegion candidate
                in snapshot.Greenhouses
            )
            {
                if (
                    !StillGreenhousesShared
                        .IsRoomTypeEnabled(
                            Config,
                            candidate.RoomType
                        )
                    || !candidate.Contains(pos)
                )
                {
                    continue;
                }

                greenhouse = candidate;
                return true;
            }

            // Complete=false means the server owns a future authoritative
            // update for this subscribed chunk. Do not poll it from the
            // tessellation path.
            return false;
        }

        if (requestIfUnknown)
        {
            RequestChunk(chunkKey);
        }

        return false;
    }

    internal static bool TryGetCachedWindMeshRoom(
        BlockPos pos,
        bool requestIfUnknown,
        [NotNullWhen(true)] out GreenhouseRegion? room
    )
    {
        if (TryGetCachedGreenhouse(
                pos,
                requestIfUnknown: false,
                out room
            ))
        {
            return true;
        }

        // Container blocks such as flower pots may occupy the block at Pos
        // while their plant mesh extends into the room cell immediately above.
        // Probe that interior cell without hardcoding a container class.
        return TryGetCachedGreenhouse(
            pos.UpCopy(),
            requestIfUnknown,
            out room
        );
    }

    internal static bool HasCompleteCachedSnapshot(
        BlockPos pos
    )
    {
        if (Config?.Enabled != true)
        {
            return false;
        }

        return ChunkSnapshots.TryGetValue(
                   ChunkKey.From(pos),
                   out ClientChunkSnapshot? snapshot
               )
               && snapshot.Complete;
    }

    internal static void ObserveVegetation(
        BlockPos pos
    )
    {
        if (Config?.Enabled != true)
        {
            return;
        }

        RepresentativeVegetationByChunk.TryAdd(
            ChunkKey.From(pos),
            ObservedVegetationPos.From(pos)
        );
    }

    internal static void ObserveRenderPath(
        Block block,
        string patchSource,
        MeshData mesh
    )
    {
        StillGreenhousesConfig? config = Config;

        if (config?.Enabled != true
            || config.DebugLogging != true)
        {
            return;
        }

        WindBitSummary summary =
            SummarizeWindBits(mesh);

        RenderPathKey key = new(
            block.Code?.ToString() ?? "<null-code>",
            block.GetType().FullName
                ?? block.GetType().Name,
            patchSource
        );

        if (!KnownRenderPaths.TryAdd(key, 0))
        {
            return;
        }

        DebugLiteral(
            $"[StillGreenhouses] WIND PATH " +
            $"source={patchSource}; " +
            $"code={key.Code}; " +
            $"runtime={key.RuntimeType}; " +
            $"material={block.BlockMaterial}; " +
            $"vegetationIdentity={StillGreenhousesShared.DescribeVegetationIdentity(block)}; " +
            $"windModeVertices={summary.WindModeVertices}; " +
            $"windDataVertices={summary.WindDataVertices}; " +
            $"windBitVertices={summary.WindBitVertices}; " +
            $"combinedWindBits=0x{summary.CombinedWindBits:X8}"
        );
    }

    internal static void LogGreenhousePolicy(
        BlockPos pos,
        GreenhouseRegion region
    )
    {
        if (Config?.DebugLogging != true)
        {
            return;
        }

        ChunkKey chunkKey = ChunkKey.From(pos);

        GreenhousePolicyLogKey key = new(
            chunkKey,
            VisualEffectEnabled,
            PlantMovementMode
        );

        if (!KnownGreenhousePolicies.TryAdd(key, 0))
        {
            return;
        }

        DebugLiteral(
            "[StillGreenhouses] GREENHOUSE POLICY " +
            $"chunk={chunkKey.X},{chunkKey.Y},{chunkKey.Z}; " +
            $"dim={chunkKey.Dimension}; " +
            $"clientVisualEnabled={Config.ClientVisualEnabled}; " +
            $"effectiveEnabled={VisualEffectEnabled}; " +
            $"roomType={region.RoomType}; " +
            $"applyGreenhouses={Config.ApplyToGreenhouses}; " +
            $"applyCellars={Config.ApplyToCellars}; " +
            $"applyRooms={Config.ApplyToRooms}; " +
            $"applyCrops={Config.ApplyToCrops}; " +
            $"applyFlowers={Config.ApplyToFlowers}; " +
            $"applyHerbs={Config.ApplyToHerbs}; " +
            $"applyBerryBushes={Config.ApplyToBerryBushes}; " +
            $"applyTallPlants={Config.ApplyToTallPlants}; " +
            $"applyVines={Config.ApplyToVines}; " +
            $"applyFruitTreeLeaves={Config.ApplyToFruitTreeLeaves}; " +
            $"movementMode={PlantMovementMode}; " +
            $"effectiveWindTarget={StillGreenhousesRoomWindEnvironment.GetEffectiveWindTarget(region.RoomType)}; " +
            $"roomWindEnvironmentActive={RoomWindEnvironmentActive}"
        );
    }

    internal static void LogWindAdjustment(
        string source,
        Block block,
        BlockPos pos,
        GreenhouseKey greenhouseKey,
        MeshData sourceMesh,
        MeshData transformedMesh
    )
    {
        if (Config?.DebugLogging != true)
        {
            return;
        }

        WindAdjustmentLogKey logKey = new(
            greenhouseKey,
            block.Code?.ToString() ?? "<null-code>",
            PlantMovementMode
        );

        if (!KnownWindAdjustments.TryAdd(
                logKey,
                0
            ))
        {
            return;
        }

        WindBitSummary summary =
            SummarizeWindBits(sourceMesh);

        WindVertexProfile beforeProfile =
            BuildWindVertexProfile(
                sourceMesh
            );

        WindVertexProfile afterProfile =
            BuildWindVertexProfile(
                transformedMesh
            );

        bool spatialProfileAvailable =
            StillGreenhousesRoomWindUniformRenderer
                .TryMeasureWindVertexEnvelope(
                    sourceMesh,
                    out ManagedRoomWindEnvelope allVertexEnvelope,
                    out ManagedRoomWindEnvelope windVertexEnvelope,
                    out int measuredVertexCount,
                    out int measuredWindVertexCount
                );

        DebugLiteral(
            $"[StillGreenhouses] ROOM WIND TARGET " +
            $"source={source}; " +
            $"code={block.Code}; " +
            $"runtime={block.GetType().FullName ?? block.GetType().Name}; " +
            $"roomType={greenhouseKey.RoomType}; " +
            $"greenhouseDim={greenhouseKey.Dimension}; " +
            $"greenhouseBounds={greenhouseKey.X1},{greenhouseKey.Y1},{greenhouseKey.Z1}.." +
            $"{greenhouseKey.X2},{greenhouseKey.Y2},{greenhouseKey.Z2}; " +
            $"greenhouseShape=0x{greenhouseKey.ShapeHash:X16}; " +
            $"windBitVerticesBefore={summary.WindBitVertices}; " +
            $"movementMode={PlantMovementMode}; " +
            $"effectiveWindTarget={StillGreenhousesRoomWindEnvironment.GetEffectiveWindTarget(greenhouseKey.RoomType)}; " +
            $"roomWindEnvironmentActive={RoomWindEnvironmentActive}; " +
            $"pos={pos.X},{pos.Y},{pos.Z}; " +
            $"dim={pos.dimension}"
        );

        DebugLiteral(
            "[StillGreenhouses] ROOM WIND MESH PROFILE " +
            $"source={source}; " +
            $"code={block.Code}; " +
            $"vertices={beforeProfile.VertexCount}; " +
            $"windVertices={beforeProfile.WindVertices}; " +
            $"distinctWindModes={beforeProfile.DistinctWindModes}; " +
            $"mixedWindModes={beforeProfile.DistinctWindModes > 1}; " +
            $"beforeModes={beforeProfile.ModeHistogram}; " +
            $"afterModes={afterProfile.ModeHistogram}"
        );

        if (spatialProfileAvailable)
        {
            float maxEncodableHalfExtent =
                StillGreenhousesRoomWindUniformRenderer
                    .EnvelopeExtentMask
                * StillGreenhousesRoomWindUniformRenderer
                    .EnvelopeExtentQuantization;

            bool extentClampRisk =
                windVertexEnvelope.HalfExtentX
                    + StillGreenhousesRoomWindUniformRenderer.EnvelopeMeasurementPadding
                    > maxEncodableHalfExtent
                || windVertexEnvelope.HalfExtentY
                    + StillGreenhousesRoomWindUniformRenderer.EnvelopeMeasurementPadding
                    > maxEncodableHalfExtent
                || windVertexEnvelope.HalfExtentZ
                    + StillGreenhousesRoomWindUniformRenderer.EnvelopeMeasurementPadding
                    > maxEncodableHalfExtent;

            DebugLiteral(
                "[StillGreenhouses] ROOM WIND MESH SPATIAL PROFILE " +
                $"source={source}; " +
                $"code={block.Code}; " +
                $"pos={pos.X},{pos.Y},{pos.Z}; " +
                $"measuredVertices={measuredVertexCount}; " +
                $"windVertices={measuredWindVertexCount}; " +
                $"allLocalBounds=[{allVertexEnvelope.MinX:0.###},{allVertexEnvelope.MinY:0.###},{allVertexEnvelope.MinZ:0.###}.." +
                $"{allVertexEnvelope.MaxX:0.###},{allVertexEnvelope.MaxY:0.###},{allVertexEnvelope.MaxZ:0.###}]; " +
                $"windLocalBounds=[{windVertexEnvelope.MinX:0.###},{windVertexEnvelope.MinY:0.###},{windVertexEnvelope.MinZ:0.###}.." +
                $"{windVertexEnvelope.MaxX:0.###},{windVertexEnvelope.MaxY:0.###},{windVertexEnvelope.MaxZ:0.###}]; " +
                $"windCenter={windVertexEnvelope.CenterX:0.###},{windVertexEnvelope.CenterY:0.###},{windVertexEnvelope.CenterZ:0.###}; " +
                $"windHalfExtents={windVertexEnvelope.HalfExtentX:0.###},{windVertexEnvelope.HalfExtentY:0.###},{windVertexEnvelope.HalfExtentZ:0.###}; " +
                $"quantStep={StillGreenhousesRoomWindUniformRenderer.EnvelopeExtentQuantization:0.###}; " +
                $"padding={StillGreenhousesRoomWindUniformRenderer.EnvelopeMeasurementPadding:0.####}; " +
                $"maxEncodedHalfExtent={maxEncodableHalfExtent:0.###}; " +
                $"extentClampRisk={extentClampRisk}; " +
                "packing=CenterXYZ+Q6x3+Target4"
            );
        }
        else
        {
            DebugLiteral(
                "[StillGreenhouses] ROOM WIND MESH SPATIAL PROFILE " +
                $"source={source}; " +
                $"code={block.Code}; " +
                $"pos={pos.X},{pos.Y},{pos.Z}; " +
                "available=False; " +
                "reason=no-finite-wind-vertex-envelope"
            );
        }

        DebugLiteral(
            "[StillGreenhouses] ROOM WIND VERTEX PROFILE " +
            $"source={source}; " +
            $"code={block.Code}; " +
            $"beforeTuples={beforeProfile.TupleHistogram}; " +
            $"afterTuples={afterProfile.TupleHistogram}"
        );

        DebugLiteral(
            "[StillGreenhouses] ROOM WIND MODE PRESERVATION " +
            $"source={source}; " +
            $"code={block.Code}; " +
            $"before={beforeProfile.ModeHistogram}; " +
            $"after={afterProfile.ModeHistogram}; " +
            $"originalWindModePreserved={beforeProfile.ModeHistogram == afterProfile.ModeHistogram}; " +
            $"originalWindDataPreserved={beforeProfile.TupleHistogram == afterProfile.TupleHistogram}; " +
            $"transport=MeshWindVertexEnvelopeUniform"
        );
    }

    internal static bool HasActiveWindMode(
        MeshData? mesh
    )
    {
        if (mesh?.Flags == null)
        {
            return false;
        }

        int vertexCount = Math.Min(
            mesh.VerticesCount,
            mesh.Flags.Length
        );

        for (int i = 0; i < vertexCount; i++)
        {
            if (
                (
                    mesh.Flags[i]
                    & VertexFlags.WindModeBitsMask
                ) != 0
            )
            {
                return true;
            }
        }

        return false;
    }

    internal static void LogPatchFailure(
        string patchName,
        BlockPos pos,
        Exception e
    )
    {
        Exception actual = e;

        if (
            e is System.Reflection.TargetInvocationException
            && e.InnerException != null
        )
        {
            actual = e.InnerException;
        }

        WarningLiteral(
            $"[StillGreenhouses] {patchName} patch failed at " +
            $"{pos.X},{pos.Y},{pos.Z}. " +
            $"{actual.GetType().Name}: {actual.Message}"
        );
    }

    private static void RequestChunk(
        ChunkKey chunkKey
    )
    {
        PlayerChunkSnapshot? playerSnapshot =
            Volatile.Read(
                ref latestPlayerChunkSnapshot
            );

        if (
            playerSnapshot == null
            || Config?.Enabled != true
            || !IsChunkWithinDiscoveryRadius(
                playerSnapshot.Chunk,
                chunkKey
            )
            || ChunkSnapshots.ContainsKey(chunkKey)
            || PendingChunkRequests.ContainsKey(chunkKey)
            || QueuedDiscoveryChunks.ContainsKey(chunkKey)
        )
        {
            return;
        }

        TryQueueDiscoveryChunk(
            playerSnapshot.Chunk,
            chunkKey
        );
    }

    private static void TryQueueDiscoveryChunk(
        ChunkKey playerChunk,
        ChunkKey chunkKey
    )
    {
        StillGreenhousesConfig? config = Config;

        if (config == null)
        {
            return;
        }

        int maxQueued =
            config.MaxQueuedDiscoveryChunks;

        if (QueuedDiscoveryChunks.Count < maxQueued)
        {
            QueuedDiscoveryChunks.TryAdd(
                chunkKey,
                0
            );

            return;
        }

        long newPriority =
            GetChunkDistanceSquared(
                chunkKey,
                playerChunk
            );

        bool foundFarthest = false;
        ChunkKey farthestChunk = default;
        long farthestPriority = long.MinValue;

        foreach (
            ChunkKey candidate
            in QueuedDiscoveryChunks.Keys
        )
        {
            if (!IsChunkWithinDiscoveryRadius(
                    playerChunk,
                    candidate
                ))
            {
                QueuedDiscoveryChunks.TryRemove(
                    candidate,
                    out _
                );

                continue;
            }

            long priority =
                GetChunkDistanceSquared(
                    candidate,
                    playerChunk
                );

            if (
                foundFarthest
                && priority <= farthestPriority
            )
            {
                continue;
            }

            foundFarthest = true;
            farthestChunk = candidate;
            farthestPriority = priority;
        }

        if (QueuedDiscoveryChunks.Count < maxQueued)
        {
            QueuedDiscoveryChunks.TryAdd(
                chunkKey,
                0
            );

            return;
        }

        if (
            !foundFarthest
            || newPriority >= farthestPriority
        )
        {
            return;
        }

        if (
            QueuedDiscoveryChunks.TryRemove(
                farthestChunk,
                out _
            )
        )
        {
            QueuedDiscoveryChunks.TryAdd(
                chunkKey,
                0
            );
        }
    }

    private static void StartClientWorkers(
        ICoreClientAPI api
    )
    {
        StopClientWorkers();

        CancellationTokenSource cancellation =
            new();

        Channel<QueuedSnapshotPacket> snapshotQueue =
            Channel.CreateBounded<QueuedSnapshotPacket>(
                new BoundedChannelOptions(128)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    AllowSynchronousContinuations = false
                }
            );

        workerCancellation = cancellation;
        snapshotPreparationQueue = snapshotQueue;

        discoveryWorkerTask =
            Task.Run(
                () => RunDiscoveryWorkerAsync(
                    api,
                    cancellation.Token
                ),
                cancellation.Token
            );

        snapshotPreparationWorkerTask =
            Task.Run(
                () => RunSnapshotPreparationWorkerAsync(
                    api,
                    snapshotQueue.Reader,
                    cancellation.Token
                ),
                cancellation.Token
            );
    }

    private static void StopClientWorkers()
    {
        CancellationTokenSource? cancellation =
            workerCancellation;

        workerCancellation = null;

        try
        {
            cancellation?.Cancel();
        }
        catch
        {
            // Worker shutdown must never interrupt client disposal.
        }

        try
        {
            snapshotPreparationQueue?
                .Writer
                .TryComplete();
        }
        catch
        {
            // Queue shutdown is best-effort.
        }

        snapshotPreparationQueue = null;
        discoveryWorkerTask = null;
        snapshotPreparationWorkerTask = null;

        cancellation?.Dispose();
    }

    private static async Task RunDiscoveryWorkerAsync(
        ICoreClientAPI api,
        CancellationToken cancellationToken
    )
    {
        double requestBudget = 0d;
        long lastTimestamp =
            Stopwatch.GetTimestamp();

        try
        {
            while (
                !cancellationToken
                    .IsCancellationRequested
            )
            {
                long now =
                    Stopwatch.GetTimestamp();

                double elapsedSeconds =
                    (double)(
                        now - lastTimestamp
                    )
                    / Stopwatch.Frequency;

                lastTimestamp = now;

                StillGreenhousesConfig? config =
                    Config;

                PlayerChunkSnapshot? playerSnapshot =
                    Volatile.Read(
                        ref latestPlayerChunkSnapshot
                    );

                bool pipelineBusy =
                    IsSnapshotPipelineBusy()
                    || Volatile.Read(ref requestBatchHandoffInFlight) != 0;

                if (
                    config?.Enabled != true
                    || playerSnapshot == null
                    || QueuedDiscoveryChunks.IsEmpty
                    || pipelineBusy
                )
                {
                    await Task.Delay(
                        pipelineBusy
                            ? BusyPipelineRetryDelayMs
                            : 50,
                        cancellationToken
                    );

                    continue;
                }

                requestBudget = Math.Min(
                    config.MaxClientDiscoveryRequestsPerSecond
                        * 2d,
                    requestBudget
                    + config.MaxClientDiscoveryRequestsPerSecond
                        * elapsedSeconds
                );

                int availableTokens =
                    (int)Math.Floor(
                        requestBudget
                    );

                if (availableTokens <= 0)
                {
                    await Task.Delay(
                        25,
                        cancellationToken
                    );

                    continue;
                }

                int batchLimit = Math.Min(
                    availableTokens,
                    config.MaxChunkRequestsPerBatch
                );

                if (
                    batchLimit
                        < config.MaxChunkRequestsPerBatch
                    && QueuedDiscoveryChunks.Count
                        >= config.MaxChunkRequestsPerBatch
                )
                {
                    await Task.Delay(
                        25,
                        cancellationToken
                    );

                    continue;
                }

                long selectionStart =
                    Stopwatch.GetTimestamp();

                List<PendingChunkRequest> selected = new(
                    batchLimit
                );

                while (
                    selected.Count < batchLimit
                    && TryTakeNearestDiscoveryChunk(
                        playerSnapshot.Chunk,
                        out ChunkKey chunkKey
                    )
                )
                {
                    long leaseId = Interlocked.Increment(
                        ref nextPendingRequestLeaseId
                    );

                    if (
                        ChunkSnapshots.ContainsKey(chunkKey)
                        || !PendingChunkRequests.TryAdd(
                            chunkKey,
                            leaseId
                        )
                    )
                    {
                        continue;
                    }

                    selected.Add(
                        new PendingChunkRequest(
                            chunkKey,
                            leaseId
                        )
                    );
                }

                double selectionElapsedMs =
                    GetElapsedMilliseconds(
                        selectionStart
                    );

                if (selected.Count == 0)
                {
                    await Task.Delay(
                        25,
                        cancellationToken
                    );

                    continue;
                }

                PreparedRequestBatch batch = new(
                    selected.ToArray(),
                    playerSnapshot.Generation,
                    selectionElapsedMs
                );

                if (
                    batch.Generation
                        != Volatile.Read(
                            ref clientWorldGeneration
                        )
                    || IsSnapshotPipelineBusy()
                )
                {
                    RequeuePreparedRequestBatch(batch);

                    await Task.Delay(
                        BusyPipelineRetryDelayMs,
                        cancellationToken
                    );

                    continue;
                }

                int handoffMarker =
                    GetRequestBatchHandoffMarker(
                        batch.Generation
                    );

                if (
                    Interlocked.CompareExchange(
                        ref requestBatchHandoffInFlight,
                        handoffMarker,
                        0
                    ) != 0
                )
                {
                    RequeuePreparedRequestBatch(batch);

                    await Task.Delay(
                        BusyPipelineRetryDelayMs,
                        cancellationToken
                    );

                    continue;
                }

                requestBudget -=
                    selected.Count;

                try
                {
                    api.Event.EnqueueMainThreadTask(
                        () => CompletePreparedRequestBatchHandoff(
                            batch
                        ),
                        "stillgreenhouses-send-request-batch"
                    );
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(
                        ref requestBatchHandoffInFlight,
                        0,
                        handoffMarker
                    );

                    RequeuePreparedRequestBatch(batch);

                    QueueWorkerWarning(
                        api,
                        "Request batch handoff failed",
                        e
                    );

                    await Task.Delay(
                        BusyPipelineRetryDelayMs,
                        cancellationToken
                    );

                    continue;
                }

                await Task.Delay(
                    WorkerHandoffDelayMs,
                    cancellationToken
                );
            }
        }
        catch (OperationCanceledException)
        {
            // Normal worker shutdown.
        }
        catch (Exception e)
        {
            QueueWorkerWarning(
                api,
                "Discovery worker failed",
                e
            );
        }
    }

    private static bool TryTakeNearestDiscoveryChunk(
        ChunkKey playerChunk,
        out ChunkKey chunkKey
    )
    {
        chunkKey = default;

        bool found = false;
        long bestPriority = long.MaxValue;

        foreach (
            ChunkKey candidate
            in QueuedDiscoveryChunks.Keys
        )
        {
            if (!IsChunkWithinDiscoveryRadius(
                    playerChunk,
                    candidate
                ))
            {
                QueuedDiscoveryChunks.TryRemove(
                    candidate,
                    out _
                );

                continue;
            }

            long priority =
                GetChunkDistanceSquared(
                    candidate,
                    playerChunk
                );

            if (priority >= bestPriority)
            {
                continue;
            }

            bestPriority = priority;
            chunkKey = candidate;
            found = true;
        }

        return found
               && QueuedDiscoveryChunks.TryRemove(
                   chunkKey,
                   out _
               );
    }

    private static void SendPreparedRequestBatch(
        PreparedRequestBatch batch
    )
    {
        IClientNetworkChannel? channel =
            clientChannel;

        if (
            batch.Generation
                != Volatile.Read(
                    ref clientWorldGeneration
                )
        )
        {
            ReleasePendingRequestLeases(
                batch.Requests,
                scheduleRetry: false
            );

            return;
        }

        if (channel?.Connected != true)
        {
            ReleasePendingRequestLeases(
                batch.Requests,
                scheduleRetry: true
            );

            return;
        }

        GreenhouseChunkBatchRequest packet =
            new()
            {
                Chunks = batch.Requests
                    .Select(request => request.Chunk.ToRequest())
                    .ToList()
            };

        long sendStart =
            Stopwatch.GetTimestamp();

        try
        {
            channel.SendPacket(packet);

            ScheduleResponseTimeout(batch);

            double sendElapsedMs =
                GetElapsedMilliseconds(
                    sendStart
                );

            if (Config?.DebugLogging == true)
            {
                string keys = string.Join(
                    "|",
                    batch.Requests.Select(request =>
                        $"{request.Chunk.X},{request.Chunk.Y},{request.Chunk.Z},{request.Chunk.Dimension}"
                    )
                );

                DebugLiteral(
                    "[StillGreenhouses] CLIENT REQUEST BATCH " +
                    $"chunks={batch.Requests.Length}; " +
                    $"keys={keys}"
                );

                LogSlowClientOperation(
                    "select-discovery-batch",
                    batch.SelectionElapsedMs,
                    $"chunks={batch.Requests.Length}; thread=worker"
                );

                LogSlowClientOperation(
                    "send-request-batch",
                    sendElapsedMs,
                    $"chunks={batch.Requests.Length}; thread=main"
                );
            }
        }
        catch (Exception e)
        {
            ReleasePendingRequestLeases(
                batch.Requests,
                scheduleRetry: true
            );

            WarningLiteral(
                "[StillGreenhouses] Failed to send room " +
                $"discovery batch. chunks={batch.Requests.Length}. " +
                $"{e.GetType().Name}: {e.Message}"
            );
        }
    }

    private static void CompletePreparedRequestBatchHandoff(
        PreparedRequestBatch batch
    )
    {
        int handoffMarker =
            GetRequestBatchHandoffMarker(
                batch.Generation
            );

        try
        {
            if (IsSnapshotPipelineBusy())
            {
                RequeuePreparedRequestBatch(batch);
            }
            else
            {
                SendPreparedRequestBatch(batch);
            }
        }
        finally
        {
            Interlocked.CompareExchange(
                ref requestBatchHandoffInFlight,
                0,
                handoffMarker
            );
        }
    }

    private static int GetRequestBatchHandoffMarker(
        int generation
    )
    {
        int marker = unchecked(generation + 1);

        return marker == 0
            ? int.MinValue
            : marker;
    }

    private static void RequeuePreparedRequestBatch(
        PreparedRequestBatch batch
    )
    {
        ReleasePendingRequestLeases(
            batch.Requests,
            scheduleRetry: false
        );

        if (
            batch.Generation
                != Volatile.Read(
                    ref clientWorldGeneration
                )
        )
        {
            return;
        }

        foreach (PendingChunkRequest request in batch.Requests)
        {
            if (!ChunkSnapshots.ContainsKey(request.Chunk))
            {
                QueuedDiscoveryChunks.TryAdd(
                    request.Chunk,
                    0
                );
            }
        }
    }

    private static void ReleasePendingRequestLeases(
        IEnumerable<PendingChunkRequest> requests,
        bool scheduleRetry
    )
    {
        foreach (PendingChunkRequest request in requests)
        {
            bool released = TryReleasePendingRequestLease(request);

            if (released && scheduleRetry)
            {
                ScheduleRetry(request.Chunk);
            }
        }
    }

    private static bool TryReleasePendingRequestLease(
        PendingChunkRequest request
    ) =>
        PendingChunkRequests.TryRemove(
            new KeyValuePair<ChunkKey, long>(
                request.Chunk,
                request.LeaseId
            )
        );

    private static void ScheduleResponseTimeout(
        PreparedRequestBatch batch
    )
    {
        ICoreClientAPI? api = Capi;
        StillGreenhousesConfig? config = Config;

        if (api == null || config?.Enabled != true)
        {
            return;
        }

        api.Event.RegisterCallback(
            _elapsedSeconds =>
            {
                if (
                    batch.Generation
                        != Volatile.Read(
                            ref clientWorldGeneration
                        )
                )
                {
                    return;
                }

                foreach (
                    PendingChunkRequest request
                    in batch.Requests
                )
                {
                    if (!TryReleasePendingRequestLease(request))
                    {
                        continue;
                    }

                    if (!ChunkSnapshots.ContainsKey(request.Chunk))
                    {
                        RequestChunk(request.Chunk);
                    }
                }
            },
            config.ClientIncompleteRetryMs
        );
    }

    private static void OnChunkSnapshot(
        GreenhouseChunkSnapshot packet
    )
    {
        Channel<QueuedSnapshotPacket>? queue =
            snapshotPreparationQueue;

        CancellationTokenSource? cancellation =
            workerCancellation;

        if (
            queue == null
            || cancellation == null
            || cancellation.IsCancellationRequested
        )
        {
            return;
        }

        QueuedSnapshotPacket queued = new(
            packet,
            Volatile.Read(
                ref clientWorldGeneration
            )
        );

        if (queue.Writer.TryWrite(queued))
        {
            return;
        }

        _ = QueueSnapshotPacketAsync(
            queue.Writer,
            queued,
            cancellation.Token
        );
    }

    private static async Task QueueSnapshotPacketAsync(
        ChannelWriter<QueuedSnapshotPacket> writer,
        QueuedSnapshotPacket queued,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await writer.WriteAsync(
                queued,
                cancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            // Normal worker shutdown.
        }
        catch (ChannelClosedException)
        {
            // Normal worker shutdown.
        }
        catch (Exception e)
        {
            ICoreClientAPI? api = Capi;

            if (api != null)
            {
                QueueWorkerWarning(
                    api,
                    "Snapshot queue write failed",
                    e
                );
            }
        }
    }

    private static int GetQueuedSnapshotPacketCount()
    {
        Channel<QueuedSnapshotPacket>? queue =
            snapshotPreparationQueue;

        if (queue == null)
        {
            return 0;
        }

        try
        {
            return queue.Reader.CanCount
                ? queue.Reader.Count
                : queue.Reader.TryPeek(out _)
                    ? 1
                    : 0;
        }
        catch
        {
            // A queue can be replaced during a world transition. Treat the old
            // reader as empty; its generation checks will discard stale work.
            return 0;
        }
    }

    private static bool IsSnapshotPipelineBusy() =>
        GetQueuedSnapshotPacketCount() > 0
        || Volatile.Read(
            ref snapshotPipelineInFlight
        ) != 0;

    private static async Task RunSnapshotPreparationWorkerAsync(
        ICoreClientAPI api,
        ChannelReader<QueuedSnapshotPacket> reader,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await foreach (
                QueuedSnapshotPacket queued
                in reader.ReadAllAsync(
                    cancellationToken
                )
            )
            {
                Interlocked.Increment(
                    ref snapshotPipelineInFlight
                );

                try
                {
                    SnapshotCommitDisposition disposition;

                    do
                    {
                        PreparedChunkSnapshot prepared =
                            PrepareChunkSnapshot(
                                queued
                            );

                        TaskCompletionSource<
                            SnapshotCommitDisposition
                        > completion = new(
                            TaskCreationOptions
                                .RunContinuationsAsynchronously
                        );

                        api.Event.EnqueueMainThreadTask(
                            () =>
                            {
                                try
                                {
                                    completion.TrySetResult(
                                        CommitPreparedSnapshot(
                                            prepared
                                        )
                                    );
                                }
                                catch (Exception e)
                                {
                                    WarningLiteral(
                                        "[StillGreenhouses] Prepared snapshot " +
                                        $"commit failed. {e.GetType().Name}: " +
                                        $"{e.Message}"
                                    );

                                    completion.TrySetResult(
                                        SnapshotCommitDisposition.Drop
                                    );
                                }
                            },
                            "stillgreenhouses-commit-prepared-snapshot"
                        );

                        disposition =
                            await completion
                                .Task
                                .WaitAsync(
                                    cancellationToken
                                );
                    }
                    while (
                        disposition
                        == SnapshotCommitDisposition.Reprepare
                        && queued.Generation
                            == Volatile.Read(
                                ref clientWorldGeneration
                            )
                    );

                    // Keep the pipeline marked busy through one short handoff
                    // window. Diagnostic logging used to provide this pacing by
                    // accident; making it explicit prevents discovery sends from
                    // overtaking a snapshot commit in release builds.
                    await Task.Delay(
                        WorkerHandoffDelayMs,
                        cancellationToken
                    );
                }
                finally
                {
                    Interlocked.Decrement(
                        ref snapshotPipelineInFlight
                    );
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal worker shutdown.
        }
        catch (Exception e)
        {
            QueueWorkerWarning(
                api,
                "Snapshot preparation worker failed",
                e
            );
        }
    }

    private static PreparedChunkSnapshot PrepareChunkSnapshot(
        QueuedSnapshotPacket queued
    )
    {
        long preparationStart =
            Stopwatch.GetTimestamp();

        GreenhouseChunkSnapshot packet =
            queued.Packet;

        ChunkKey chunkKey =
            ChunkKey.From(packet);

        GreenhouseRegion[] regions =
            packet.Greenhouses
                .Select(GreenhouseRegion.FromPacket)
                .Where(region =>
                    region.IntersectsChunk(chunkKey)
                )
                .ToArray();

        ChunkSnapshots.TryGetValue(
            chunkKey,
            out ClientChunkSnapshot? oldSnapshot
        );

        RoomPolicySnapshot policy =
            RoomPolicySnapshot.From(Config);

        GreenhouseMembershipPos[] newConfiguredPositions;
        int changedConfiguredPositionCount;
        int addedConfiguredPositionCount;
        bool changedTouchesChunkMinY;

        if (
            oldSnapshot != null
            && oldSnapshot.ContentHash == packet.ContentHash
        )
        {
            // A visual-only revision carries the same authoritative room
            // membership. Avoid rebuilding two room-sized hash sets and a
            // symmetric difference that cannot contain any positions.
            newConfiguredPositions =
                Array.Empty<GreenhouseMembershipPos>();

            changedConfiguredPositionCount = 0;
            addedConfiguredPositionCount = 0;
            changedTouchesChunkMinY = false;
        }
        else
        {
            HashSet<GreenhouseMembershipPos> oldMembership =
                BuildConfiguredWindMembershipSet(
                    oldSnapshot?.Greenhouses
                        ?? Array.Empty<GreenhouseRegion>(),
                    policy,
                    chunkKey
                );

            HashSet<GreenhouseMembershipPos> newMembership =
                BuildConfiguredWindMembershipSet(
                    regions,
                    policy,
                    chunkKey
                );

            HashSet<GreenhouseMembershipPos> changedPositions =
                new(newMembership);

            changedPositions.SymmetricExceptWith(
                oldMembership
            );

            newConfiguredPositions =
                newMembership.ToArray();

            changedConfiguredPositionCount =
                changedPositions.Count;

            addedConfiguredPositionCount = 0;

            foreach (
                GreenhouseMembershipPos position
                in newMembership
            )
            {
                if (!oldMembership.Contains(position))
                {
                    addedConfiguredPositionCount++;
                }
            }

            int chunkMinY =
                chunkKey.Y
                * StillGreenhousesShared.ChunkSize;

            changedTouchesChunkMinY =
                changedPositions.Any(
                    position => position.Y == chunkMinY
                );
        }

        ClientChunkSnapshot newSnapshot = new(
            packet.Revision,
            packet.VisualRevision,
            packet.Complete,
            packet.ContentHash,
            regions
        );

        return new PreparedChunkSnapshot(
            queued,
            newSnapshot,
            oldSnapshot != null,
            oldSnapshot?.Revision ?? 0,
            oldSnapshot?.ContentHash
                ?? StillGreenhousesShared.ComputeRegionSetHash(
                    Array.Empty<GreenhouseRegion>()
                ),
            policy,
            newConfiguredPositions,
            changedConfiguredPositionCount,
            addedConfiguredPositionCount,
            changedTouchesChunkMinY,
            GetElapsedMilliseconds(
                preparationStart
            )
        );
    }

    private static SnapshotCommitDisposition
        CommitPreparedSnapshot(
            PreparedChunkSnapshot prepared
        )
    {
        ICoreClientAPI? api = Capi;

        if (
            api == null
            || prepared.Queued.Generation
                != Volatile.Read(
                    ref clientWorldGeneration
                )
        )
        {
            return SnapshotCommitDisposition.Drop;
        }

        GreenhouseChunkSnapshot packet =
            prepared.Queued.Packet;

        ChunkKey chunkKey =
            ChunkKey.From(packet);

        PlayerChunkSnapshot? playerSnapshot =
            Volatile.Read(
                ref latestPlayerChunkSnapshot
            );

        StillGreenhousesConfig? config =
            Config;

        if (
            playerSnapshot == null
            || config == null
            || !IsChunkWithinClientRetentionRadius(
                playerSnapshot.Chunk,
                chunkKey,
                config.ClientCacheRadiusChunks
            )
        )
        {
            QueuedDiscoveryChunks.TryRemove(
                chunkKey,
                out _
            );

            PendingChunkRequests.TryRemove(
                chunkKey,
                out _
            );

            return SnapshotCommitDisposition.Drop;
        }

        if (
            packet.Complete
            && prepared.NewSnapshot.Greenhouses.Length == 0
            && IgnoreNextCompleteNegativeSnapshot.TryRemove(
                chunkKey,
                out _
            )
        )
        {
            QueuedDiscoveryChunks.TryRemove(
                chunkKey,
                out _
            );

            PendingChunkRequests.TryRemove(
                chunkKey,
                out _
            );

            if (Config?.DebugLogging == true)
            {
                DebugLiteral(
                    "[StillGreenhouses] CLIENT STALE NEGATIVE DISCARDED " +
                    $"chunk={chunkKey.X},{chunkKey.Y},{chunkKey.Z}; " +
                    $"dim={chunkKey.Dimension}; " +
                    $"revision={packet.Revision}; " +
                    $"visualRevision={packet.VisualRevision}"
                );
            }

            RequestChunk(chunkKey);

            return SnapshotCommitDisposition.Commit;
        }

        bool currentExists =
            ChunkSnapshots.TryGetValue(
                chunkKey,
                out ClientChunkSnapshot? oldSnapshot
            );

        if (
            currentExists
                != prepared.ExpectedOldSnapshotExists
            || (
                currentExists
                && oldSnapshot!.Revision
                    != prepared.ExpectedOldRevision
            )
            || RoomPolicySnapshot.From(Config)
                != prepared.Policy
        )
        {
            return SnapshotCommitDisposition.Reprepare;
        }

        if (
            oldSnapshot != null
            && oldSnapshot.Revision > packet.Revision
        )
        {
            PendingChunkRequests.TryRemove(
                chunkKey,
                out _
            );

            return SnapshotCommitDisposition.Drop;
        }

        bool authoritativeVisualTransition =
            oldSnapshot != null
            && packet.VisualRevision
                > oldSnapshot.VisualRevision;

        if (
            !packet.Complete
            && oldSnapshot?.Greenhouses.Length > 0
            && !authoritativeVisualTransition
        )
        {
            QueuedDiscoveryChunks.TryRemove(
                chunkKey,
                out _
            );

            PendingChunkRequests.TryRemove(
                chunkKey,
                out _
            );

            if (Config?.DebugLogging == true)
            {
                DebugLiteral(
                    "[StillGreenhouses] CLIENT SNAPSHOT PRESERVED " +
                    $"chunk={chunkKey.X},{chunkKey.Y},{chunkKey.Z}; " +
                    $"dim={chunkKey.Dimension}; " +
                    $"incomingRevision={packet.Revision}; " +
                    $"incomingVisualRevision={packet.VisualRevision}; " +
                    $"cachedVisualRevision={oldSnapshot.VisualRevision}; " +
                    "reason=incomplete-positive-revalidation"
                );
            }

            return SnapshotCommitDisposition.Commit;
        }

        long commitStart =
            Stopwatch.GetTimestamp();

        bool roomMembershipChanged =
            prepared.ChangedConfiguredPositionCount > 0;

        bool configuredMembershipChanged =
            roomMembershipChanged
            && (
                RepresentativeVegetationByChunk.ContainsKey(
                    chunkKey
                )
                || StillGreenhousesRoomWindUniformRenderer
                    .HasRegisteredVegetationPositionsForChunk(
                        chunkKey
                    )
            );

        bool contentChanged =
            prepared.ExpectedOldContentHash
            != prepared.NewSnapshot.ContentHash;

        ChunkSnapshots[
            chunkKey
        ] = prepared.NewSnapshot;

        RoomWindRegistrationReconcileResult
            registrationReconcile = default;

        if (roomMembershipChanged)
        {
            registrationReconcile =
                StillGreenhousesRoomWindUniformRenderer
                    .ReconcilePositionsForChunk(chunkKey);
        }

        bool belowChunkRedrawQueued = false;

        if (prepared.ChangedTouchesChunkMinY)
        {
            ChunkKey belowChunk = new(
                chunkKey.X,
                chunkKey.Y - 1,
                chunkKey.Z,
                chunkKey.Dimension
            );

            if (StillGreenhousesRoomWindUniformRenderer
                    .HasRegisteredVegetationPositionsForChunk(
                        belowChunk
                    ))
            {
                StillGreenhousesRoomWindUniformRenderer
                    .ReconcilePositionsForChunk(
                        belowChunk
                    );

                if (prepared.AddedConfiguredPositionCount > 0)
                {
                    belowChunkRedrawQueued = QueueChunkRedraw(
                        belowChunk,
                        "snapshot-membership-addition-above"
                    );
                }
            }
        }

        bool waterScanQueued =
            contentChanged
            || !prepared.ExpectedOldSnapshotExists
            || roomMembershipChanged;

        if (waterScanQueued)
        {
            QueueRoomWaterSourceScan(
                chunkKey,
                prepared.NewSnapshot,
                prepared.NewConfiguredPositions,
                prepared.Queued.Generation
            );
        }

        IgnoreNextCompleteNegativeSnapshot.TryRemove(
            chunkKey,
            out _
        );

        QueuedDiscoveryChunks.TryRemove(
            chunkKey,
            out _
        );

        PendingChunkRequests.TryRemove(
            chunkKey,
            out _
        );

        bool redrawRequested =
            configuredMembershipChanged
            && prepared.AddedConfiguredPositionCount > 0;

        bool redrawQueued =
            redrawRequested
            && QueueChunkRedraw(
                chunkKey,
                "snapshot-membership-addition"
            );

        double commitElapsedMs =
            GetElapsedMilliseconds(
                commitStart
            );

        if (Config?.DebugLogging == true)
        {
            if (
                prepared.NewSnapshot.Greenhouses.Length > 0
                || contentChanged
                || configuredMembershipChanged
            )
            {
                DebugLiteral(
                    "[StillGreenhouses] CLIENT SNAPSHOT " +
                    $"chunk={chunkKey.X},{chunkKey.Y},{chunkKey.Z}; " +
                    $"dim={chunkKey.Dimension}; " +
                    $"revision={packet.Revision}; " +
                    $"visualRevision={packet.VisualRevision}; " +
                    $"complete={packet.Complete}; " +
                    $"greenhouses={prepared.NewSnapshot.Greenhouses.Length}; " +
                    $"contentHash=0x{packet.ContentHash:X16}; " +
                    $"contentChanged={contentChanged}; " +
                    $"visualRevisionChanged={oldSnapshot != null && oldSnapshot.VisualRevision != prepared.NewSnapshot.VisualRevision}; " +
                    $"authoritativeVisualTransition={authoritativeVisualTransition}; " +
                    $"configuredMembershipChanged={configuredMembershipChanged}; " +
                    $"addedConfiguredPositions={prepared.AddedConfiguredPositionCount}; " +
                    $"roomWindRegisteredBefore={registrationReconcile.RegisteredBefore}; " +
                    $"roomWindRetained={registrationReconcile.Retained}; " +
                    $"roomWindRoomTypeUpdated={registrationReconcile.RoomTypeUpdated}; " +
                    $"roomWindRoomIdentityUpdated={registrationReconcile.RoomIdentityUpdated}; " +
                    $"removedRoomWindRegistrations={registrationReconcile.Removed}; " +
                    $"removedRoomWindWaterTargets={registrationReconcile.WaterTargetsRemoved}; " +
                    $"waterScanQueued={waterScanQueued}; " +
                    $"waterScanQueuedPositions={(waterScanQueued ? prepared.NewConfiguredPositions.Length : 0)}; " +
                    $"redrawRequested={redrawRequested}; " +
                    $"redrawQueued={redrawQueued}; " +
                    $"belowChunkRedrawQueued={belowChunkRedrawQueued}"
                );
            }

            LogSlowClientOperation(
                "prepare-snapshot",
                prepared.PreparationElapsedMs,
                $"regions={prepared.NewSnapshot.Greenhouses.Length}; " +
                $"changedPositions={prepared.ChangedConfiguredPositionCount}; " +
                "thread=worker"
            );

            LogSlowClientOperation(
                "commit-snapshot",
                commitElapsedMs,
                "foregroundBlockQueries=0; " +
                $"changedMembershipPositions={prepared.ChangedConfiguredPositionCount}; " +
                $"redrawEvidence=observed-or-registered-vegetation; " +
                $"waterScanQueued={waterScanQueued}; " +
                $"redrawQueued={redrawQueued}; " +
                $"belowChunkRedrawQueued={belowChunkRedrawQueued}; " +
                "thread=main"
            );
        }

        return SnapshotCommitDisposition.Commit;
    }

    private static HashSet<GreenhouseMembershipPos>
        BuildConfiguredWindMembershipSet(
            IEnumerable<GreenhouseRegion> regions,
            RoomPolicySnapshot policy,
            ChunkKey chunkKey
        )
    {
        HashSet<GreenhouseMembershipPos> result =
            new();

        foreach (
            GreenhouseRegion region
            in regions
        )
        {
            if (!policy.IsEnabled(region.RoomType))
            {
                continue;
            }

            foreach (
                BlockPos pos
                in region.GetOccupiedPositionsInChunk(
                    chunkKey
                )
            )
            {
                result.Add(
                    GreenhouseMembershipPos.From(pos)
                );
            }
        }

        return result;
    }

    private static void ScheduleRetry(
        ChunkKey chunkKey
    )
    {
        ICoreClientAPI? api = Capi;
        StillGreenhousesConfig? config = Config;

        if (api == null
            || config?.Enabled != true
            || !ScheduledRetries.TryAdd(
                chunkKey,
                0
            ))
        {
            return;
        }

        int generation = Volatile.Read(
            ref clientWorldGeneration
        );

        api.Event.RegisterCallback(
            _elapsedSeconds =>
            {
                if (
                    generation
                        != Volatile.Read(
                            ref clientWorldGeneration
                        )
                )
                {
                    return;
                }

                ScheduledRetries.TryRemove(
                    chunkKey,
                    out _
                );

                if (!ChunkSnapshots.ContainsKey(chunkKey))
                {
                    RequestChunk(chunkKey);
                }
            },
            config.ClientIncompleteRetryMs
        );
    }

    private static void MarkChunkDirty(
        ICoreClientAPI api,
        ChunkKey chunkKey
    )
    {
        bool hasObservedVegetation =
            RepresentativeVegetationByChunk.TryGetValue(
                chunkKey,
                out ObservedVegetationPos observedPos
            );

        BlockPos redrawPos = hasObservedVegetation
            ? observedPos.ToBlockPos()
            : chunkKey.ToRepresentativeBlockPos();

        Interlocked.Increment(
            ref suppressBlockChanged
        );

        try
        {
            api.World.BlockAccessor.MarkBlockDirty(
                redrawPos
            );
        }
        finally
        {
            Interlocked.Decrement(
                ref suppressBlockChanged
            );
        }

        if (Config?.DebugLogging == true)
        {
            DebugLiteral(
                "[StillGreenhouses] CLIENT REDRAW " +
                $"chunk={chunkKey.X},{chunkKey.Y},{chunkKey.Z}; " +
                $"chunkDim={chunkKey.Dimension}; " +
                $"source={(hasObservedVegetation ? "observed-vegetation" : "chunk-center-fallback")}; " +
                $"pos={redrawPos.X},{redrawPos.Y},{redrawPos.Z}; " +
                $"dim={redrawPos.dimension}"
            );
        }
    }

    internal static void LogUnclassifiedWindBlockOnce(
        Block block,
        BlockPos pos,
        GreenhouseRegion room
    )
    {
        if (Config?.DebugLogging != true)
        {
            return;
        }

        string code =
            block.Code?.ToString()
            ?? "<null-code>";

        if (!KnownUnclassifiedWindBlocks.TryAdd(
                code,
                0
            ))
        {
            return;
        }

        DebugLiteral(
            "[StillGreenhouses] UNCLASSIFIED WIND BLOCK " +
            $"code={code}; " +
            $"runtime={block.GetType().FullName ?? block.GetType().Name}; " +
            $"material={block.BlockMaterial}; " +
            $"vegetationIdentity={StillGreenhousesShared.DescribeVegetationIdentity(block)}; " +
            $"roomType={room.RoomType}; " +
            $"pos={pos.X},{pos.Y},{pos.Z}; " +
            $"dim={pos.dimension}"
        );
    }

    internal static void LogWindMeshFallbackTargetOnce(
        Block block,
        BlockPos pos,
        GreenhouseRegion room
    )
    {
        if (Config?.DebugLogging != true)
        {
            return;
        }

        string code =
            block.Code?.ToString()
            ?? "<null-code>";

        if (!KnownWindMeshFallbackTargets.TryAdd(code, 0))
        {
            return;
        }

        DebugLiteral(
            "[StillGreenhouses] ROOM WIND WIND-MESH FALLBACK TARGET " +
            $"code={code}; " +
            $"runtime={block.GetType().FullName ?? block.GetType().Name}; " +
            $"material={block.BlockMaterial}; " +
            $"pos={pos.X},{pos.Y},{pos.Z}; " +
            $"dim={pos.dimension}; " +
            $"roomType={room.RoomType}; " +
            "reason=active-native-windmode+ApplyToOtherVegetation"
        );
    }

    internal static void LogContainerWindTargetOnce(
        string source,
        BlockEntity blockEntity,
        GreenhouseRegion room,
        MeshData mesh,
        ManagedRoomWindEnvelope envelope,
        bool transformedByMeshPoolMatrix
    )
    {
        if (Config?.DebugLogging != true)
        {
            return;
        }

        Block block = blockEntity.Block;

        ContainerWindTargetLogKey key = new(
            block.Code?.ToString() ?? "<null-code>",
            blockEntity.GetType().FullName
                ?? blockEntity.GetType().Name,
            source
        );

        if (!KnownContainerWindTargets.TryAdd(key, 0))
        {
            return;
        }

        WindVertexProfile profile =
            BuildWindVertexProfile(mesh);

        DebugLiteral(
            "[StillGreenhouses] ROOM WIND CONTAINED VEGETATION TARGET " +
            $"source={source}; " +
            $"containerCode={key.ContainerCode}; " +
            $"containerRuntime={key.ContainerRuntime}; " +
            $"pos={blockEntity.Pos.X},{blockEntity.Pos.Y},{blockEntity.Pos.Z}; " +
            $"dim={blockEntity.Pos.dimension}; " +
            $"roomType={room.RoomType}; " +
            $"meshIdentity={RuntimeHelpers.GetHashCode(mesh)}; " +
            $"vertices={profile.VertexCount}; " +
            $"windVertices={profile.WindVertices}; " +
            $"windModes={profile.ModeHistogram}; " +
            $"transformedByMeshPoolMatrix={transformedByMeshPoolMatrix}; " +
            $"windEnvelope={FormatEnvelopeForLog(envelope)}; " +
            "eligibility=ActiveVanillaWindMode+ApplyToOtherVegetation"
        );
    }

    internal static void LogFlowerMeshProcess(
        string source,
        Block block,
        BlockPos pos,
        MeshData mesh,
        ManagedRoomWindEnvelope measuredEnvelope,
        ManagedRoomWindEnvelope registrationEnvelope
    )
    {
        if (
            Config?.DebugLogging != true
            || block.Code?.Path?.StartsWith(
                "flower-",
                StringComparison.Ordinal
            ) != true
        )
        {
            return;
        }

        int meshIdentity =
            RuntimeHelpers.GetHashCode(mesh);

        FlowerMeshProcessLogKey key = new(
            block.Code?.ToString() ?? "<null-code>",
            source,
            pos.X,
            pos.Y,
            pos.Z,
            pos.dimension,
            meshIdentity
        );

        if (!KnownFlowerMeshProcesses.TryAdd(key, 0))
        {
            return;
        }

        DebugLiteral(
            "[StillGreenhouses] ROOM WIND FLOWER MESH PROCESS " +
            $"source={source}; " +
            $"code={block.Code}; " +
            $"pos={pos.X},{pos.Y},{pos.Z}; " +
            $"dim={pos.dimension}; " +
            $"meshIdentity={meshIdentity}; " +
            $"vertices={mesh.VerticesCount}; " +
            $"randomizeRotations={block.RandomizeRotations}; " +
            $"randomDrawOffset={block.RandomDrawOffset}; " +
            $"measuredWindEnvelope={FormatEnvelopeForLog(measuredEnvelope)}; " +
            $"registeredWindEnvelope={FormatEnvelopeForLog(registrationEnvelope)}; " +
            $"snapshotKnown={ChunkSnapshots.ContainsKey(ChunkKey.From(pos))}; " +
            $"thread={Environment.CurrentManagedThreadId}"
        );
    }

    private static string FormatEnvelopeForLog(
        ManagedRoomWindEnvelope envelope
    ) =>
        FormattableString.Invariant(
            $"[{envelope.MinX:0.###},{envelope.MinY:0.###},{envelope.MinZ:0.###}..{envelope.MaxX:0.###},{envelope.MaxY:0.###},{envelope.MaxZ:0.###}]"
        );

    private static void QueueRoomWaterSourceScan(
        ChunkKey chunkKey,
        ClientChunkSnapshot snapshot,
        GreenhouseMembershipPos[] configuredPositions,
        int generation
    )
    {
        StillGreenhousesConfig? config =
            Config;

        if (
            config?.ApplyToWater != true
            || !StillGreenhousesRoomWindEnvironment
                .IsShaderDrivenMode(PlantMovementMode)
            || configuredPositions.Length == 0
        )
        {
            return;
        }

        LatestQueuedWaterScanRevision[
            chunkKey
        ] = snapshot.Revision;

        PendingWaterScanBatches.Enqueue(
            new PendingWaterScanBatch(
                chunkKey,
                snapshot.Revision,
                configuredPositions,
                generation
            )
        );
    }

    private static void ProcessClientForegroundWork(
        float dt
    )
    {
        ICoreClientAPI? api = Capi;
        StillGreenhousesConfig? config = Config;

        if (
            api == null
            || config?.Enabled != true
        )
        {
            return;
        }

        if (
            PendingWaterScanBatches.IsEmpty
            && PendingChunkRedrawQueue.IsEmpty
        )
        {
            return;
        }

        bool measurePerformance =
            config.DebugLogging;

        long startTimestamp =
            measurePerformance
                ? Stopwatch.GetTimestamp()
                : 0;

        int waterChecked = 0;
        int waterRegistered = 0;
        int waterBatchesDropped = 0;
        int redrawsProcessed = 0;
        int waterBatchAttempts = 0;
        int redrawAttempts = 0;

        int maxWaterBatchAttempts = Math.Clamp(
            config.MaxClientWaterChecksPerTick,
            16,
            256
        );

        int maxRedrawAttempts = Math.Clamp(
            config.MaxClientChunkRedrawsPerTick * 4,
            16,
            256
        );

        while (
            waterChecked
                < config.MaxClientWaterChecksPerTick
            && waterBatchAttempts
                < maxWaterBatchAttempts
            && PendingWaterScanBatches.TryPeek(
                out PendingWaterScanBatch? batch
            )
        )
        {
            waterBatchAttempts++;

            bool stale =
                batch.Generation
                    != Volatile.Read(
                        ref clientWorldGeneration
                    )
                || !LatestQueuedWaterScanRevision.TryGetValue(
                    batch.Chunk,
                    out long latestRevision
                )
                || latestRevision != batch.Revision
                || !ChunkSnapshots.TryGetValue(
                    batch.Chunk,
                    out ClientChunkSnapshot? currentSnapshot
                )
                || currentSnapshot.Revision
                    != batch.Revision;

            if (stale)
            {
                PendingWaterScanBatches.TryDequeue(
                    out _
                );

                waterBatchesDropped++;

                continue;
            }

            while (
                batch.NextIndex
                    < batch.Positions.Length
                && waterChecked
                    < config.MaxClientWaterChecksPerTick
            )
            {
                BlockPos pos =
                    batch.Positions[
                        batch.NextIndex++
                    ].ToBlockPos();

                waterChecked++;

                if (
                    !StillGreenhousesShared
                        .IsWaterSurfaceSourceBlock(
                            api.World.BlockAccessor,
                            pos
                        )
                    || !TryGetCachedGreenhouse(
                        pos,
                        requestIfUnknown: false,
                        out GreenhouseRegion? room
                    )
                    || !StillGreenhousesShared
                        .IsRoomTypeEnabled(
                            config,
                            room.RoomType
                        )
                )
                {
                    StillGreenhousesRoomWindUniformRenderer
                        .RemoveWaterPosition(pos);

                    continue;
                }

                StillGreenhousesRoomWindUniformRenderer
                    .RegisterWaterPosition(
                        pos,
                        room
                    );

                waterRegistered++;
            }

            if (
                batch.NextIndex
                    < batch.Positions.Length
            )
            {
                break;
            }

            PendingWaterScanBatches.TryDequeue(
                out _
            );

            if (
                LatestQueuedWaterScanRevision.TryGetValue(
                    batch.Chunk,
                    out long completedRevision
                )
                && completedRevision
                    == batch.Revision
            )
            {
                LatestQueuedWaterScanRevision.TryRemove(
                    batch.Chunk,
                    out _
                );
            }
        }

        while (
            redrawsProcessed
                < config.MaxClientChunkRedrawsPerTick
            && redrawAttempts
                < maxRedrawAttempts
            && PendingChunkRedrawQueue.TryDequeue(
                out ChunkKey chunkKey
            )
        )
        {
            redrawAttempts++;

            if (!PendingChunkRedraws.TryRemove(
                    chunkKey,
                    out _
                ))
            {
                continue;
            }

            MarkChunkDirty(
                api,
                chunkKey
            );

            redrawsProcessed++;
        }

        Interlocked.Add(
            ref waterScanPositionsChecked,
            waterChecked
        );

        Interlocked.Add(
            ref waterScanPositionsRegistered,
            waterRegistered
        );

        Interlocked.Add(
            ref waterScanBatchesDropped,
            waterBatchesDropped
        );

        Interlocked.Add(
            ref chunkRedrawsProcessed,
            redrawsProcessed
        );

        if (measurePerformance)
        {
            LogSlowClientOperation(
                "client-foreground-work",
                GetElapsedMilliseconds(
                    startTimestamp
                ),
                $"waterChecked={waterChecked}; " +
                $"waterRegistered={waterRegistered}; " +
                $"waterBatchesDropped={waterBatchesDropped}; " +
                $"waterBatchAttempts={waterBatchAttempts}; " +
                $"pendingWaterBatches={PendingWaterScanBatches.Count}; " +
                $"redrawsProcessed={redrawsProcessed}; " +
                $"redrawAttempts={redrawAttempts}; " +
                $"pendingRedraws={PendingChunkRedraws.Count}; " +
                "thread=main"
            );
        }
    }

    private static bool QueueChunkRedraw(
        ChunkKey chunkKey,
        string reason
    )
    {
        if (!PendingChunkRedraws.TryAdd(
                chunkKey,
                reason
            ))
        {
            return false;
        }

        PendingChunkRedrawQueue.Enqueue(
            chunkKey
        );

        Interlocked.Increment(
            ref chunkRedrawRequestsQueued
        );

        return true;
    }

    private static void RefreshRoomWaterRegistrationAt(
        ICoreClientAPI api,
        BlockPos pos
    )
    {
        StillGreenhousesConfig? config =
            Config;

        // A change at this block can remove, submerge, or expose a source at
        // this exact coordinate. Clear only the liquid target first; a plant
        // registration sharing the coordinate must survive the recheck.
        StillGreenhousesRoomWindUniformRenderer
            .RemoveWaterPosition(pos);

        if (
            config?.ApplyToWater != true
            || !StillGreenhousesRoomWindEnvironment
                .IsShaderDrivenMode(PlantMovementMode)
            || !StillGreenhousesShared.IsWaterSurfaceSourceBlock(
                api.World.BlockAccessor,
                pos
            )
            || !TryGetCachedGreenhouse(
                pos,
                requestIfUnknown: false,
                out GreenhouseRegion? room
            )
            || !StillGreenhousesShared.IsRoomTypeEnabled(
                config,
                room.RoomType
            )
        )
        {
            return;
        }

        StillGreenhousesRoomWindUniformRenderer
            .RegisterWaterPosition(
                pos,
                room
            );
    }

    private void OnBlockChanged(
        BlockPos pos,
        Block? oldBlock
    )
    {
        ICoreClientAPI? api = Capi;

        if (
            api == null
            || Config?.Enabled != true
            || Volatile.Read(
                ref suppressBlockChanged
            ) > 0
        )
        {
            return;
        }

        QueueRoomInspectionRefreshForBlockChange(
            api,
            pos
        );

        StillGreenhousesRoomWindUniformRenderer
            .RemovePosition(pos);

        Block newBlock =
            api.World.BlockAccessor.GetBlock(pos);

        RefreshRoomWaterRegistrationAt(
            api,
            pos
        );

        RefreshRoomWaterRegistrationAt(
            api,
            pos.DownCopy()
        );

        if (
            StillGreenhousesShared.IsRoomInteriorPassThroughBlock(
                oldBlock
            )
            && StillGreenhousesShared.IsRoomInteriorPassThroughBlock(
                newBlock
            )
        )
        {
            return;
        }

        foreach (
            ChunkKey chunkKey
            in StillGreenhousesShared
                .GetBoundaryAffectedChunks(pos)
        )
        {
            if (
                !ChunkSnapshots.TryGetValue(
                    chunkKey,
                    out ClientChunkSnapshot? snapshot
                )
                || !snapshot.Complete
                || snapshot.Greenhouses.Length > 0
            )
            {
                continue;
            }

            ChunkSnapshots.TryRemove(
                chunkKey,
                out _
            );

            bool requestInFlight =
                PendingChunkRequests.ContainsKey(
                    chunkKey
                );

            if (requestInFlight)
            {
                // The existing packet may have been sent before the matching
                // server-side negative cache was invalidated. Keep the active
                // request lease so another BlockChanged cannot send a duplicate.
                // If that request returns one complete-negative snapshot, drop
                // it once and issue one fresh post-invalidation discovery.
                IgnoreNextCompleteNegativeSnapshot[
                    chunkKey
                ] = 0;
            }
            else
            {
                // The cached complete-negative snapshot proves this chunk was
                // previously relevant. Do not require its representative
                // vegetation entry to survive before asking the authoritative
                // server to revalidate a structural room change.
                RequestChunk(chunkKey);
            }
        }
    }

    private static void PruneClientCache(
        float dt
    )
    {
        ICoreClientAPI? api = Capi;

        if (api == null)
        {
            return;
        }

        StillGreenhousesConfig? config =
            Config;

        if (config == null)
        {
            return;
        }

        ChunkKey playerChunk =
            GetPlayerChunk(api);

        int retentionRadius =
            config.ClientCacheRadiusChunks;

        HashSet<ChunkKey> candidates = new(
            ChunkSnapshots.Keys
        );

        candidates.UnionWith(
            RepresentativeVegetationByChunk.Keys
        );

        candidates.UnionWith(
            QueuedDiscoveryChunks.Keys
        );

        candidates.UnionWith(
            PendingChunkRequests.Keys
        );

        candidates.UnionWith(
            LatestQueuedWaterScanRevision.Keys
        );

        candidates.UnionWith(
            PendingChunkRedraws.Keys
        );

        foreach (ChunkKey chunkKey in candidates)
        {
            if (IsChunkWithinClientRetentionRadius(
                    playerChunk,
                    chunkKey,
                    retentionRadius
                ))
            {
                continue;
            }

            ChunkSnapshots.TryRemove(
                chunkKey,
                out _
            );

            int removedRoomWindRegistrations =
                StillGreenhousesRoomWindUniformRenderer
                    .RemovePositionsForChunk(chunkKey);

            if (
                removedRoomWindRegistrations > 0
                && Config?.DebugLogging == true
            )
            {
                DebugLiteral(
                    "[StillGreenhouses] CLIENT ROOM WIND REGISTRATIONS PRUNED " +
                    $"chunk={chunkKey.X},{chunkKey.Y},{chunkKey.Z}; " +
                    $"dim={chunkKey.Dimension}; " +
                    $"positions={removedRoomWindRegistrations}; " +
                    "reason=client-cache-retention"
                );
            }

            RepresentativeVegetationByChunk.TryRemove(
                chunkKey,
                out _
            );

            QueuedDiscoveryChunks.TryRemove(
                chunkKey,
                out _
            );

            PendingChunkRequests.TryRemove(
                chunkKey,
                out _
            );

            ScheduledRetries.TryRemove(
                chunkKey,
                out _
            );

            IgnoreNextCompleteNegativeSnapshot.TryRemove(
                chunkKey,
                out _
            );

            LatestQueuedWaterScanRevision.TryRemove(
                chunkKey,
                out _
            );

            PendingChunkRedraws.TryRemove(
                chunkKey,
                out _
            );
        }
    }

    private static void UpdatePlayerChunkSnapshot(
        float dt
    )
    {
        ICoreClientAPI? api = Capi;

        if (api == null)
        {
            return;
        }

        try
        {
            IClientPlayer? player =
                api.World.Player;

            if (player?.Entity == null)
            {
                Volatile.Write(
                    ref latestPlayerChunkSnapshot,
                    null
                );

                return;
            }

            ChunkKey playerChunk =
                GetPlayerChunk(api);

            int generation =
                Volatile.Read(
                    ref clientWorldGeneration
                );

            PlayerChunkSnapshot? currentSnapshot =
                Volatile.Read(
                    ref latestPlayerChunkSnapshot
                );

            if (
                currentSnapshot?.Chunk == playerChunk
                && currentSnapshot.Generation == generation
            )
            {
                return;
            }

            Volatile.Write(
                ref latestPlayerChunkSnapshot,
                new PlayerChunkSnapshot(
                    playerChunk,
                    generation
                )
            );
        }
        catch
        {
            Volatile.Write(
                ref latestPlayerChunkSnapshot,
                null
            );
        }
    }

    private static bool IsChunkWithinDiscoveryRadius(
        ChunkKey playerChunk,
        ChunkKey chunkKey
    )
    {
        StillGreenhousesConfig? config =
            Config;

        if (config == null)
        {
            return false;
        }

        if (
            chunkKey.Dimension
            != playerChunk.Dimension
        )
        {
            return false;
        }

        int radius =
            config.ClientDiscoveryRadiusChunks;

        return Math.Abs(
                   chunkKey.X - playerChunk.X
               ) <= radius
               && Math.Abs(
                   chunkKey.Z - playerChunk.Z
               ) <= radius
               && Math.Abs(
                   chunkKey.Y - playerChunk.Y
               ) <= 8;
    }

    private static ChunkKey GetPlayerChunk(
        ICoreClientAPI api
    )
    {
        BlockPos playerPos =
            new BlockPos(
                api.World.Player.Entity.Pos.Dimension
            )
            .Set(api.World.Player.Entity.Pos);

        return ChunkKey.From(playerPos);
    }

    private static bool IsChunkWithinClientRetentionRadius(
        ICoreClientAPI api,
        ChunkKey chunkKey
    )
    {
        StillGreenhousesConfig? config =
            Config;

        if (config == null)
        {
            return false;
        }

        return IsChunkWithinClientRetentionRadius(
            GetPlayerChunk(api),
            chunkKey,
            config.ClientCacheRadiusChunks
        );
    }

    private static bool IsChunkWithinClientRetentionRadius(
        ChunkKey playerChunk,
        ChunkKey chunkKey,
        int radius
    )
    {
        if (
            chunkKey.Dimension
            != playerChunk.Dimension
        )
        {
            return false;
        }

        return Math.Abs(
                   chunkKey.X - playerChunk.X
               ) <= radius
               && Math.Abs(
                   chunkKey.Z - playerChunk.Z
               ) <= radius
               && Math.Abs(
                   chunkKey.Y - playerChunk.Y
               ) <= 8;
    }

    private static long GetChunkDistanceSquared(
        ChunkKey chunkKey,
        ChunkKey playerChunk
    )
    {
        if (
            chunkKey.Dimension
            != playerChunk.Dimension
        )
        {
            return long.MaxValue;
        }

        long dx =
            chunkKey.X - playerChunk.X;

        long dy =
            chunkKey.Y - playerChunk.Y;

        long dz =
            chunkKey.Z - playerChunk.Z;

        return dx * dx
               + dy * dy
               + dz * dz;
    }

    private static double GetElapsedMilliseconds(
        long startTimestamp
    ) =>
        (
            Stopwatch.GetTimestamp()
            - startTimestamp
        )
        * 1000d
        / Stopwatch.Frequency;

    private static void LogSlowClientOperation(
        string operation,
        double elapsedMs,
        string details
    )
    {
        if (
            Config?.DebugLogging != true
            || elapsedMs < 2d
        )
        {
            return;
        }

        DebugLiteral(
            "[StillGreenhouses] CLIENT PERF " +
            $"operation={operation}; " +
            $"elapsedMs={elapsedMs:F3}; " +
            details
        );
    }

    private static void QueueWorkerWarning(
        ICoreClientAPI api,
        string operation,
        Exception e
    )
    {
        api.Event.EnqueueMainThreadTask(
            () => WarningLiteral(
                "[StillGreenhouses] " +
                $"{operation}. " +
                $"{e.GetType().Name}: {e.Message}"
            ),
            "stillgreenhouses-worker-warning"
        );
    }

    private static void LogClientCacheSummary(
        float dt
    )
    {
        if (Config?.DebugLogging != true)
        {
            return;
        }

        DebugLiteral(
            "[StillGreenhouses] CLIENT CACHE SUMMARY " +
            $"queuedDiscovery={QueuedDiscoveryChunks.Count}/{Config.MaxQueuedDiscoveryChunks}; " +
            $"pendingRequests={PendingChunkRequests.Count}; " +
            $"staleNegativeGuards={IgnoreNextCompleteNegativeSnapshot.Count}; " +
            $"snapshotPacketsQueued={GetQueuedSnapshotPacketCount()}; " +
            $"snapshotPipelineInFlight={Volatile.Read(ref snapshotPipelineInFlight)}; " +
            $"requestBatchHandoffInFlight={Volatile.Read(ref requestBatchHandoffInFlight)}; " +
            $"discoveryWorkerActive={discoveryWorkerTask?.IsCompleted == false}; " +
            $"snapshotWorkerActive={snapshotPreparationWorkerTask?.IsCompleted == false}; " +
            $"pendingWaterBatches={PendingWaterScanBatches.Count}; " +
            $"pendingRedraws={PendingChunkRedraws.Count}; " +
            $"waterScanPositionsChecked={Interlocked.Read(ref waterScanPositionsChecked)}; " +
            $"waterScanPositionsRegistered={Interlocked.Read(ref waterScanPositionsRegistered)}; " +
            $"waterScanBatchesDropped={Interlocked.Read(ref waterScanBatchesDropped)}; " +
            $"chunkRedrawRequestsQueued={Interlocked.Read(ref chunkRedrawRequestsQueued)}; " +
            $"chunkRedrawsProcessed={Interlocked.Read(ref chunkRedrawsProcessed)}; " +
            $"snapshots={ChunkSnapshots.Count}; " +
            $"observedChunks={RepresentativeVegetationByChunk.Count}; " +
            $"roomWindShaderOriginPrepared={StillGreenhousesRoomWindShaderPatch.OriginPrepared}; " +
            $"roomWindShaderOriginPath={StillGreenhousesRoomWindShaderPatch.OverrideOriginPath}; " +
            $"roomWindShaderOriginPriorityIndex={StillGreenhousesRoomWindShaderPatch.OriginPriorityIndex}; " +
            $"roomWindShaderBaseSourceHash={StillGreenhousesRoomWindShaderPatch.BaseSourceHash}; " +
            $"roomWindShaderPreparedOverrideHash={StillGreenhousesRoomWindShaderPatch.PreparedOverrideSourceHash}; " +
            $"roomWindShaderResolvedOriginPath={StillGreenhousesRoomWindShaderPatch.ResolvedAssetOriginPath}; " +
            $"roomWindShaderOverrideReady={RoomWindShaderOverrideReady}; " +
            $"roomWindShaderOverrideMarkerPresent={StillGreenhousesRoomWindShaderPatch.OverrideMarkerPresent}; " +
            $"roomWindShaderOverrideMatchesPreparedHash={StillGreenhousesRoomWindShaderPatch.OverrideMatchesPreparedHash}; " +
            $"roomWindShaderResolvedOverrideHash={StillGreenhousesRoomWindShaderPatch.ResolvedOverrideSourceHash}; " +
            $"roomWindShaderReloadAttempted={RoomWindShaderReloadAttempted}; " +
            $"roomWindShaderReloadSucceeded={RoomWindShaderReloadSucceeded}; " +
            $"roomWindShaderReloadFailure={StillGreenhousesRoomWindShaderPatch.ShaderReloadFailureReason}; " +
            $"roomWindCompiledVerificationPending={StillGreenhousesRoomWindShaderPatch.CompiledVerificationPending}; " +
            $"roomWindCompiledBridgeVerified={RoomWindCompiledBridgeVerified}; " +
            $"roomWindUniformBridgeReady={RoomWindUniformBridgeReady}; " +
            $"roomWindEnvironmentActive={RoomWindEnvironmentActive}; " +
            $"roomWindShaderWindSpeedUses={StillGreenhousesRoomWindShaderPatch.WindSpeedReplacements}; " +
            $"roomWindShaderWindWaveUses={StillGreenhousesRoomWindShaderPatch.WindWaveCounterReplacements}; " +
            $"roomWindShaderHighFreqUses={StillGreenhousesRoomWindShaderPatch.HighFreqCounterReplacements}; " +
            $"roomWindShaderOverrideAssets={StillGreenhousesRoomWindShaderPatch.OverrideAssetCount}/{StillGreenhousesRoomWindShaderPatch.RequiredOverrideAssets}; " +
            $"roomWindShaderCallSiteAssets={StillGreenhousesRoomWindShaderPatch.CallSiteAssetsPatched}/{StillGreenhousesRoomWindShaderPatch.RequiredCallSiteAssets}; " +
            $"roomWindShaderWrappedCalls={StillGreenhousesRoomWindShaderPatch.CallSiteCallsWrapped}; " +
            $"roomWindTopsoilActiveWarpCalls={StillGreenhousesRoomWindShaderPatch.TopsoilActiveVertexWarpCalls}; " +
            $"roomWindTopsoilCommentedWarpDetected={StillGreenhousesRoomWindShaderPatch.TopsoilCommentedVertexWarpDetected}; " +
            $"roomWindShaderFunctionSourceChunksLogged={StillGreenhousesRoomWindShaderPatch.FunctionSourceChunksLogged}; " +
            $"roomWindShaderLastFailure={StillGreenhousesRoomWindShaderPatch.LastFailureReason}; " +
            $"roomWindTransport=QuarterBlockTextureHash+RoomTypeSharedVanillaStates; " +
            $"roomWindShaderTopology={StillGreenhousesRoomWindShaderPatch.ShaderTopology}; " +
            $"roomWindShaderSpatialMatch={StillGreenhousesRoomWindShaderPatch.AbsolutePositionStrategy}; " +
            $"roomWindDebugCallSiteProof={Config.DebugRoomWindCallSiteProof}; " +
            $"roomWindDebugVisualProof={Config.DebugRoomWindVisualProof}; " +
            $"greenhouseWindRange={StillGreenhousesRoomWindEnvironment.DescribeRange(Config, ManagedRoomType.Greenhouse)}; " +
            $"greenhouseWindCurrentPercent={StillGreenhousesRoomWindUniformRenderer.GreenhouseCurrentPercent:0.###}; " +
            $"cellarWindRange={StillGreenhousesRoomWindEnvironment.DescribeRange(Config, ManagedRoomType.Cellar)}; " +
            $"cellarWindCurrentPercent={StillGreenhousesRoomWindUniformRenderer.CellarCurrentPercent:0.###}; " +
            $"roomWindRange={StillGreenhousesRoomWindEnvironment.DescribeRange(Config, ManagedRoomType.Room)}; " +
            $"roomWindCurrentPercent={StillGreenhousesRoomWindUniformRenderer.RoomCurrentPercent:0.###}; " +
            $"greenhouseWaterWindRange={StillGreenhousesRoomWindEnvironment.DescribeRange(Config, ManagedRoomType.Greenhouse, RoomWindTargetKind.Water)}; " +
            $"greenhouseWaterWindCurrentPercent={StillGreenhousesRoomWindUniformRenderer.GreenhouseWaterCurrentPercent:0.###}; " +
            $"cellarWaterWindRange={StillGreenhousesRoomWindEnvironment.DescribeRange(Config, ManagedRoomType.Cellar, RoomWindTargetKind.Water)}; " +
            $"cellarWaterWindCurrentPercent={StillGreenhousesRoomWindUniformRenderer.CellarWaterCurrentPercent:0.###}; " +
            $"roomWaterWindRange={StillGreenhousesRoomWindEnvironment.DescribeRange(Config, ManagedRoomType.Room, RoomWindTargetKind.Water)}; " +
            $"roomWaterWindCurrentPercent={StillGreenhousesRoomWindUniformRenderer.RoomWaterCurrentPercent:0.###}; " +
            $"roomWindRegisteredPositions={StillGreenhousesRoomWindUniformRenderer.RegisteredPositionCount}; " +
            $"roomWindValidPositions={StillGreenhousesRoomWindUniformRenderer.ValidPositionCount}; " +
            $"roomWindCompactedEnvelopes={StillGreenhousesRoomWindUniformRenderer.CompactedEnvelopeCount}; " +
            $"roomWindHashCells={StillGreenhousesRoomWindUniformRenderer.UploadedPositionCount}; " +
            $"roomWindHashCoveredCells={StillGreenhousesRoomWindUniformRenderer.UploadedCoveredPositionCount}; " +
            $"roomWindLegacyPositionBudgetIgnored={StillGreenhousesRoomWindUniformRenderer.ConfiguredPositionBudget}; " +
            $"roomWindUploadedPositionsGreenhouse={StillGreenhousesRoomWindUniformRenderer.UploadedGreenhousePositionCount}; " +
            $"roomWindUploadedPositionsCellar={StillGreenhousesRoomWindUniformRenderer.UploadedCellarPositionCount}; " +
            $"roomWindUploadedPositionsRoom={StillGreenhousesRoomWindUniformRenderer.UploadedRoomPositionCount}; " +
            $"roomWindUploadedStates={StillGreenhousesRoomWindUniformRenderer.UploadedRoomStateCount}/{StillGreenhousesRoomWindUniformRenderer.UploadedRoomTypeStateCount}; " +
            $"roomWindUniformPrograms={StillGreenhousesRoomWindUniformRenderer.ActiveProgramCount}/{StillGreenhousesRoomWindUniformRenderer.TargetProgramCount}; " +
            $"roomWindVegetationPrograms={StillGreenhousesRoomWindUniformRenderer.ActiveVegetationProgramCount}/{StillGreenhousesRoomWindUniformRenderer.VegetationTargetProgramCount}; " +
            $"roomWindTerrainVegetationPrograms={StillGreenhousesRoomWindUniformRenderer.ActiveTerrainVegetationProgramCount}/{StillGreenhousesRoomWindUniformRenderer.TerrainVegetationTargetProgramCount}; " +
            $"roomWindAuxiliaryVegetationPrograms={StillGreenhousesRoomWindUniformRenderer.ActiveAuxiliaryVegetationProgramCount}/{StillGreenhousesRoomWindUniformRenderer.AuxiliaryVegetationTargetProgramCount}; " +
            $"roomWindLiquidPrograms={StillGreenhousesRoomWindUniformRenderer.ActiveLiquidProgramCount}/{StillGreenhousesRoomWindUniformRenderer.LiquidTargetProgramCount}; " +
            $"roomWindRequiredChunkOpaqueBound={StillGreenhousesRoomWindUniformRenderer.RequiredChunkOpaqueBound}; " +
            $"roomWindLastUniformUploadFailure={StillGreenhousesRoomWindUniformRenderer.LastUniformUploadFailure}; " +
            $"roomWindPositionSnapshotRevision={StillGreenhousesRoomWindUniformRenderer.SnapshotRevision}; " +
            $"roomWindRegistrationRevision={StillGreenhousesRoomWindUniformRenderer.RegistrationRevision}; " +
            $"roomWindTopologyRefreshRuns={StillGreenhousesRoomWindUniformRenderer.TopologyRefreshRuns}; " +
            $"roomWindTopologyRefreshSkips={StillGreenhousesRoomWindUniformRenderer.TopologyRefreshSkips}; " +
            $"roomWindTopologyCandidatesEvaluated={StillGreenhousesRoomWindUniformRenderer.TopologyCandidatesEvaluated}; " +
            $"roomWindTopologyLastMs={StillGreenhousesRoomWindUniformRenderer.LastTopologyRefreshMs:F3}; " +
            $"roomWindTopologyMaxMs={StillGreenhousesRoomWindUniformRenderer.MaxTopologyRefreshMs:F3}; " +
            $"roomWindProgramDiscoveryRuns={StillGreenhousesRoomWindUniformRenderer.ProgramDiscoveryRuns}; " +
            $"roomWindProgramDiscoveryLastMs={StillGreenhousesRoomWindUniformRenderer.LastProgramDiscoveryMs:F3}; " +
            $"roomWindProgramDiscoveryMaxMs={StillGreenhousesRoomWindUniformRenderer.MaxProgramDiscoveryMs:F3}; " +
            $"roomWindUniformUploadRuns={StillGreenhousesRoomWindUniformRenderer.UniformUploadRuns}; " +
            $"roomWindUniformProgramBindOperations={StillGreenhousesRoomWindUniformRenderer.UniformProgramBindOperations}; " +
            $"roomWindUniformUploadLastMs={StillGreenhousesRoomWindUniformRenderer.LastUniformUploadMs:F3}; " +
            $"roomWindUniformUploadMaxMs={StillGreenhousesRoomWindUniformRenderer.MaxUniformUploadMs:F3}"
        );

        LogRoomWindShaderUniforms();
    }

    private static void LogRoomWindShaderUniforms()
    {
        ICoreClientAPI? api = Capi;

        if (
            Config?.DebugLogging != true
            || api == null
        )
        {
            return;
        }

        try
        {
            DefaultShaderUniforms uniforms =
                api.Render.ShaderUniforms;

            DebugLiteral(
                "[StillGreenhouses] ROOM WIND SHADER UNIFORMS " +
                    $"timeCounter={uniforms.TimeCounter:0.######}; " +
                $"windSpeed={uniforms.WindSpeed:0.######}; " +
                $"windWaveCounter={uniforms.WindWaveCounter:0.######}; " +
                $"windWaveCounterHighFreq={uniforms.WindWaveCounterHighFreq:0.######}; " +
                $"windWaveIntensity={uniforms.WindWaveIntensity:0.######}; " +
                $"glitchStrength={uniforms.GlitchStrength:0.######}; " +
                $"glitchWaviness={uniforms.GlitchWaviness:0.######}; " +
                $"globalWorldWarp={uniforms.GlobalWorldWarp:0.######}"
            );
        }
        catch (Exception e)
        {
            DebugLiteral(
                "[StillGreenhouses] ROOM WIND SHADER UNIFORMS FAILED " +
                $"error={e.GetType().Name}:{e.Message}"
            );
        }
    }

    private void OnConfigLibConfigSaved(
        string eventName,
        ref EnumHandling handling,
        Vintagestory.API.Datastructures.IAttribute data
    )
    {
        ICoreClientAPI? api = Capi;

        if (api == null)
        {
            return;
        }

        api.Event.EnqueueMainThreadTask(
            () =>
            {
                ApplyRoomInspectionConfigSaved(api);

                api.Logger.Notification(
                    "[StillGreenhouses] ConfigLib settings saved. " +
                    "The room inspection overlay setting was applied immediately; " +
                    "restart the client for other Still Greenhouses changes."
                );
            },
            "stillgreenhouses-configlib-saved"
        );
    }

    private static int RedrawCachedGreenhouseChunks(
        ICoreClientAPI api
    )
    {
        int redrawCount = 0;

        foreach (
            KeyValuePair<
                ChunkKey,
                ClientChunkSnapshot
            > entry
            in ChunkSnapshots
        )
        {
            if (entry.Value.Greenhouses.Length == 0)
            {
                continue;
            }

            if (QueueChunkRedraw(
                    entry.Key,
                    "shader-bridge-state-change"
                ))
            {
                redrawCount++;
            }
        }

        return redrawCount;
    }

    internal static int RedrawCachedManagedRoomChunksForShaderChange()
    {
        ICoreClientAPI? api = Capi;

        return api == null
            ? 0
            : RedrawCachedGreenhouseChunks(api);
    }

    private void OnLeaveWorld()
    {
        Interlocked.Increment(
            ref clientWorldGeneration
        );

        // Invalidate any accepted request handoff from the world being left.
        // Generation-tagged callbacks cannot clear a newer world's marker.
        Interlocked.Exchange(
            ref requestBatchHandoffInFlight,
            0
        );

        Volatile.Write(
            ref latestPlayerChunkSnapshot,
            null
        );

        roomWindUniformRenderer
            ?.PrepareForWorldTransition();

        ResetRoomInspectionState(
            Capi,
            clearHighlight: true
        );

        ClearClientCache();
    }

    private void OnLevelFinalize()
    {
        roomWindUniformRenderer
            ?.PrepareForWorldTransition();

        ICoreClientAPI? api = Capi;

        ResetRoomInspectionState(
            api,
            clearHighlight: true
        );

        if (api != null)
        {
            int generation = Volatile.Read(
                ref clientWorldGeneration
            );

            // Wait until the first normal world-render initialization has had
            // time to complete before creating the client highlight state.
            // This prevents the overlay from participating in login-time
            // renderer construction.
            api.Event.RegisterCallback(
                _elapsedSeconds =>
                {
                    if (
                        generation
                            != Volatile.Read(
                                ref clientWorldGeneration
                            )
                        || Capi != api
                    )
                    {
                        return;
                    }

                    InitializeRoomInspection(api);
                },
                1000
            );
        }

        UpdatePlayerChunkSnapshot(0f);
    }

    private static void ClearClientCache()
    {
        ChunkSnapshots.Clear();
        QueuedDiscoveryChunks.Clear();
        PendingChunkRequests.Clear();
        ScheduledRetries.Clear();
        IgnoreNextCompleteNegativeSnapshot.Clear();
        RepresentativeVegetationByChunk.Clear();
        KnownRenderPaths.Clear();
        KnownGreenhousePolicies.Clear();
        KnownWindAdjustments.Clear();
        KnownFlowerMeshProcesses.Clear();
        KnownUnclassifiedWindBlocks.Clear();
        KnownContainerWindTargets.Clear();
        KnownWindMeshFallbackTargets.Clear();

        PendingWaterScanBatches.Clear();
        LatestQueuedWaterScanRevision.Clear();
        PendingChunkRedrawQueue.Clear();
        PendingChunkRedraws.Clear();

        StillGreenhousesRoomWindUniformRenderer
            .ClearRegisteredPositions();

    }

    private static WindBitSummary SummarizeWindBits(
        MeshData mesh
    )
    {
        if (mesh.Flags == null)
        {
            return default;
        }

        int vertexCount = Math.Min(
            mesh.VerticesCount,
            mesh.Flags.Length
        );

        int windModeVertices = 0;
        int windDataVertices = 0;
        int windBitVertices = 0;
        int combinedWindBits = 0;

        for (int i = 0; i < vertexCount; i++)
        {
            int flags = mesh.Flags[i];

            if (
                (
                    flags
                    & VertexFlags.WindModeBitsMask
                ) != 0
            )
            {
                windModeVertices++;
            }

            if (
                (
                    flags
                    & VertexFlags.WindDataBitsMask
                ) != 0
            )
            {
                windDataVertices++;
            }

            int windBits =
                flags & VertexFlags.WindBitsMask;

            if (windBits != 0)
            {
                windBitVertices++;
                combinedWindBits |= windBits;
            }
        }

        return new WindBitSummary(
            vertexCount,
            windModeVertices,
            windDataVertices,
            windBitVertices,
            combinedWindBits
        );
    }

    private static WindVertexProfile BuildWindVertexProfile(
        MeshData mesh
    )
    {
        int[] modeCounts = new int[16];
        int[,] tupleCounts = new int[16, 8];

        if (mesh.Flags == null)
        {
            return new WindVertexProfile(
                mesh.VerticesCount,
                0,
                0,
                "<none>",
                "<none>"
            );
        }

        int vertexCount = Math.Min(
            mesh.VerticesCount,
            mesh.Flags.Length
        );

        int windVertices = 0;

        for (int i = 0; i < vertexCount; i++)
        {
            int flags = mesh.Flags[i];

            int windMode =
                (
                    flags
                    & VertexFlags.WindModeBitsMask
                )
                >> VertexFlags.WindModeBitsPos;

            if (windMode == 0)
            {
                continue;
            }

            int windData =
                (
                    flags
                    & VertexFlags.WindDataBitsMask
                )
                >> VertexFlags.WindDataBitsPos;

            windVertices++;
            modeCounts[windMode]++;
            tupleCounts[windMode, windData]++;
        }

        int distinctModes =
            modeCounts.Count(
                count => count > 0
            );

        return new WindVertexProfile(
            vertexCount,
            windVertices,
            distinctModes,
            FormatModeHistogram(modeCounts),
            FormatTupleHistogram(tupleCounts)
        );
    }

    private static string FormatModeHistogram(
        IReadOnlyList<int> modeCounts
    )
    {
        StringBuilder builder = new();

        for (
            int mode = 0;
            mode < modeCounts.Count;
            mode++
        )
        {
            int count = modeCounts[mode];

            if (count == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(',');
            }

            builder.Append("mode");
            builder.Append(mode);
            builder.Append('=');
            builder.Append(count);
        }

        return builder.Length == 0
            ? "<none>"
            : builder.ToString();
    }

    private static string FormatTupleHistogram(
        int[,] tupleCounts
    )
    {
        StringBuilder builder = new();

        for (int mode = 0; mode < 16; mode++)
        {
            for (int data = 0; data < 8; data++)
            {
                int count =
                    tupleCounts[mode, data];

                if (count == 0)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(',');
                }

                builder.Append("(mode");
                builder.Append(mode);
                builder.Append(",data");
                builder.Append(data);
                builder.Append(")=");
                builder.Append(count);
            }
        }

        return builder.Length == 0
            ? "<none>"
            : builder.ToString();
    }

    public override void Dispose()
    {
        ICoreClientAPI? api = Capi;
        ICoreClientAPI? lifecycleApi =
            lifecycleClientApi;

        if (lifecycleApi != null)
        {
            lifecycleApi.Event.BlockTexturesLoaded -=
                OnBlockTexturesLoaded;

            StillGreenhousesRoomWindShaderPatch
                .RemoveAssetOrigin(lifecycleApi);
        }

        if (api != null)
        {
            api.Event.LeaveWorld -= OnLeaveWorld;
            api.Event.LevelFinalize -= OnLevelFinalize;
            api.Event.BlockChanged -= OnBlockChanged;

            if (playerChunkTickListenerId != 0)
            {
                api.Event.UnregisterGameTickListener(
                    playerChunkTickListenerId
                );
            }

            if (foregroundWorkTickListenerId != 0)
            {
                api.Event.UnregisterGameTickListener(
                    foregroundWorkTickListenerId
                );
            }

            if (cachePruneListenerId != 0)
            {
                api.Event.UnregisterGameTickListener(
                    cachePruneListenerId
                );
            }

            if (debugSummaryListenerId != 0)
            {
                api.Event.UnregisterGameTickListener(
                    debugSummaryListenerId
                );
            }

            api.Event.UnregisterEventBusListener(
                OnConfigLibConfigSaved
            );

            DisposeRoomInspection(api);

            if (roomWindUniformRenderer != null)
            {
                api.Event.UnregisterRenderer(
                    roomWindUniformRenderer,
                    EnumRenderStage.Before
                );

                roomWindUniformRenderer.Dispose();
            }
        }

        roomWindUniformRenderer = null;

        StopClientWorkers();

        harmony?.UnpatchAll(HarmonyId);
        harmony = null;

        ClearClientCache();

        playerChunkTickListenerId = 0;
        cachePruneListenerId = 0;
        debugSummaryListenerId = 0;
        suppressBlockChanged = 0;

        Volatile.Write(
            ref latestPlayerChunkSnapshot,
            null
        );

        clientChannel = null;
        lifecycleClientApi = null;
        Config = null;
        Capi = null;

        base.Dispose();
    }

    private sealed record PlayerChunkSnapshot(
        ChunkKey Chunk,
        int Generation
    );

    private sealed record PreparedRequestBatch(
        PendingChunkRequest[] Requests,
        int Generation,
        double SelectionElapsedMs
    );

    private readonly record struct PendingChunkRequest(
        ChunkKey Chunk,
        long LeaseId
    );

    private sealed record QueuedSnapshotPacket(
        GreenhouseChunkSnapshot Packet,
        int Generation
    );

    private sealed class PendingWaterScanBatch
    {
        internal ChunkKey Chunk { get; }

        internal long Revision { get; }

        internal GreenhouseMembershipPos[] Positions { get; }

        internal int Generation { get; }

        internal int NextIndex { get; set; }

        internal PendingWaterScanBatch(
            ChunkKey chunk,
            long revision,
            GreenhouseMembershipPos[] positions,
            int generation
        )
        {
            Chunk = chunk;
            Revision = revision;
            Positions = positions;
            Generation = generation;
        }
    }

    private sealed record PreparedChunkSnapshot(
        QueuedSnapshotPacket Queued,
        ClientChunkSnapshot NewSnapshot,
        bool ExpectedOldSnapshotExists,
        long ExpectedOldRevision,
        ulong ExpectedOldContentHash,
        RoomPolicySnapshot Policy,
        GreenhouseMembershipPos[] NewConfiguredPositions,
        int ChangedConfiguredPositionCount,
        int AddedConfiguredPositionCount,
        bool ChangedTouchesChunkMinY,
        double PreparationElapsedMs
    );

    private readonly record struct RoomPolicySnapshot(
        bool ApplyToGreenhouses,
        bool ApplyToCellars,
        bool ApplyToRooms
    )
    {
        internal static RoomPolicySnapshot From(
            StillGreenhousesConfig? config
        ) =>
            config == null
                ? default
                : new(
                    config.ApplyToGreenhouses,
                    config.ApplyToCellars,
                    config.ApplyToRooms
                );

        internal bool IsEnabled(
            ManagedRoomType roomType
        ) =>
            roomType switch
            {
                ManagedRoomType.Greenhouse =>
                    ApplyToGreenhouses,

                ManagedRoomType.Cellar =>
                    ApplyToCellars,

                ManagedRoomType.Room =>
                    ApplyToRooms,

                _ => false
            };
    }

    private readonly record struct ContainerWindTargetLogKey(
        string ContainerCode,
        string ContainerRuntime,
        string Source
    );

    private enum SnapshotCommitDisposition
    {
        Commit,
        Reprepare,
        Drop
    }

    private sealed record ClientChunkSnapshot(
        long Revision,
        long VisualRevision,
        bool Complete,
        ulong ContentHash,
        GreenhouseRegion[] Greenhouses
    );

    private readonly record struct GreenhousePolicyLogKey(
        ChunkKey Chunk,
        bool VisualEnabled,
        RoomPlantMovementMode MovementMode
    );

    private readonly record struct GreenhouseMembershipPos(
        int X,
        int Y,
        int Z,
        int Dimension
    )
    {
        internal static GreenhouseMembershipPos From(
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

    private readonly record struct ObservedVegetationPos(
        int X,
        int Y,
        int Z,
        int Dimension
    )
    {
        internal static ObservedVegetationPos From(
            BlockPos pos
        ) =>
            new(
                pos.X,
                pos.Y,
                pos.Z,
                pos.dimension
            );

        internal BlockPos ToBlockPos() =>
            new(X, Y, Z, Dimension);
    }

    private readonly record struct WindAdjustmentLogKey(
        GreenhouseKey Greenhouse,
        string BlockCode,
        RoomPlantMovementMode MovementMode
    );

    private readonly record struct FlowerMeshProcessLogKey(
        string BlockCode,
        string Source,
        int X,
        int Y,
        int Z,
        int Dimension,
        int MeshIdentity
    );

    private readonly record struct RenderPathKey(
        string Code,
        string RuntimeType,
        string PatchSource
    );

    private readonly record struct WindBitSummary(
        int VertexCount,
        int WindModeVertices,
        int WindDataVertices,
        int WindBitVertices,
        int CombinedWindBits
    );

    private sealed record WindVertexProfile(
        int VertexCount,
        int WindVertices,
        int DistinctWindModes,
        string ModeHistogram,
        string TupleHistogram
    );
}
