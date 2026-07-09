/*
version 0.10.16a
*/

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

    internal static string EffectiveGreenhouseWindTarget =>
        StillGreenhousesRoomWindEnvironment
            .EffectiveWindTarget;

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
        byte
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
        string,
        byte
    > KnownUnclassifiedWindBlocks = new();

    private static long playerChunkTickListenerId;
    private static long cachePruneListenerId;
    private static long debugSummaryListenerId;
    private static int suppressBlockChanged;

    private static CancellationTokenSource? workerCancellation;
    private static Task? discoveryWorkerTask;
    private static Task? snapshotPreparationWorkerTask;

    private static Channel<QueuedSnapshotPacket>?
        snapshotPreparationQueue;

    private static PlayerChunkSnapshot?
        latestPlayerChunkSnapshot;

    private static int clientWorldGeneration;
    private static int snapshotPacketsQueued;

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
            .RegisterMessageType<GreenhouseChunkSnapshot>();

        if (api is ICoreClientAPI clientApi)
        {
            lifecycleClientApi =
                clientApi;

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

        StillGreenhousesRoomWindShaderPatch
            .ReloadCompiledShaders(
                api,
                "BlockTexturesLoaded"
            );
    }

    public override void StartClientSide(
        ICoreClientAPI api
    )
    {
        Capi = api;

        Config = StillGreenhousesShared.LoadConfig(
            api,
            storeNormalizedConfig: false
        );

        clientChannel =
            api.Network
                .GetChannel(StillGreenhousesNetwork.ChannelName)
                .SetMessageHandler<GreenhouseChunkSnapshot>(
                    OnChunkSnapshot
                );

        harmony = new Harmony(HarmonyId);

        try
        {
            harmony.PatchAll(
                typeof(StillGreenhousesClientSystem).Assembly
            );

            api.Logger.Notification(
                "[StillGreenhouses] Client Harmony patches applied: " +
                "unified vegetation policy plus dedicated tall-plant and fruiting-bush mesh paths."
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
        api.Event.BlockChanged += OnBlockChanged;

        roomWindUniformRenderer =
            new StillGreenhousesRoomWindUniformRenderer(
                api
            );

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
            $"[StillGreenhouses] Client loaded v0.10.16a. " +
            $"Enabled={Config.Enabled}, " +
            $"ClientVisualEnabled={Config.ClientVisualEnabled}, " +
            $"VisualEffectEnabled={VisualEffectEnabled}, " +
            $"DebugLogging={Config.DebugLogging}, " +
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
            $"PlantMovementMode={PlantMovementMode}, " +
            $"GreenhouseWindRange={StillGreenhousesRoomWindEnvironment.DescribeRange(Config, ManagedRoomType.Greenhouse)}, " +
            $"CellarWindRange={StillGreenhousesRoomWindEnvironment.DescribeRange(Config, ManagedRoomType.Cellar)}, " +
            $"RoomWindRange={StillGreenhousesRoomWindEnvironment.DescribeRange(Config, ManagedRoomType.Room)}, " +
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

        ChunkKey chunkKey = ChunkKey.From(pos);

        RepresentativeVegetationByChunk.TryAdd(
            chunkKey,
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
            $"transport=SpatialPositionUniform"
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

                if (
                    config?.Enabled != true
                    || playerSnapshot == null
                    || QueuedDiscoveryChunks.IsEmpty
                )
                {
                    await Task.Delay(
                        50,
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

                List<ChunkKey> selected = new(
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
                    if (
                        ChunkSnapshots.ContainsKey(
                            chunkKey
                        )
                        || !PendingChunkRequests.TryAdd(
                            chunkKey,
                            0
                        )
                    )
                    {
                        continue;
                    }

                    selected.Add(chunkKey);
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

                requestBudget -=
                    selected.Count;

                PreparedRequestBatch batch = new(
                    selected.ToArray(),
                    playerSnapshot.Generation,
                    selectionElapsedMs
                );

                api.Event.EnqueueMainThreadTask(
                    () => SendPreparedRequestBatch(
                        batch
                    ),
                    "stillgreenhouses-send-request-batch"
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
                batch.Chunks,
                scheduleRetry: false
            );

            return;
        }

        if (channel?.Connected != true)
        {
            ReleasePendingRequestLeases(
                batch.Chunks,
                scheduleRetry: true
            );

            return;
        }

        GreenhouseChunkBatchRequest packet =
            new()
            {
                Chunks = batch.Chunks
                    .Select(chunk => chunk.ToRequest())
                    .ToList()
            };

        long sendStart =
            Stopwatch.GetTimestamp();

        try
        {
            channel.SendPacket(packet);

            double sendElapsedMs =
                GetElapsedMilliseconds(
                    sendStart
                );

            if (Config?.DebugLogging == true)
            {
                string keys = string.Join(
                    "|",
                    batch.Chunks.Select(chunk =>
                        $"{chunk.X},{chunk.Y},{chunk.Z},{chunk.Dimension}"
                    )
                );

                DebugLiteral(
                    "[StillGreenhouses] CLIENT REQUEST BATCH " +
                    $"chunks={batch.Chunks.Length}; " +
                    $"keys={keys}"
                );

                LogSlowClientOperation(
                    "select-discovery-batch",
                    batch.SelectionElapsedMs,
                    $"chunks={batch.Chunks.Length}; thread=worker"
                );

                LogSlowClientOperation(
                    "send-request-batch",
                    sendElapsedMs,
                    $"chunks={batch.Chunks.Length}; thread=main"
                );
            }
        }
        catch (Exception e)
        {
            ReleasePendingRequestLeases(
                batch.Chunks,
                scheduleRetry: true
            );

            WarningLiteral(
                "[StillGreenhouses] Failed to send room " +
                $"discovery batch. chunks={batch.Chunks.Length}. " +
                $"{e.GetType().Name}: {e.Message}"
            );
        }
    }

    private static void ReleasePendingRequestLeases(
        IEnumerable<ChunkKey> chunks,
        bool scheduleRetry
    )
    {
        foreach (ChunkKey chunkKey in chunks)
        {
            PendingChunkRequests.TryRemove(
                chunkKey,
                out _
            );

            if (scheduleRetry)
            {
                ScheduleRetry(chunkKey);
            }
        }
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
            Interlocked.Increment(
                ref snapshotPacketsQueued
            );

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

            Interlocked.Increment(
                ref snapshotPacketsQueued
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
                Interlocked.Decrement(
                    ref snapshotPacketsQueued
                );

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
                    region.GetIntersectingChunks()
                        .Contains(chunkKey)
                )
                .ToArray();

        ChunkSnapshots.TryGetValue(
            chunkKey,
            out ClientChunkSnapshot? oldSnapshot
        );

        RoomPolicySnapshot policy =
            RoomPolicySnapshot.From(Config);

        HashSet<GreenhouseMembershipPos> oldMembership =
            BuildConfiguredWindMembershipSet(
                oldSnapshot?.Greenhouses
                    ?? Array.Empty<GreenhouseRegion>(),
                policy
            );

        HashSet<GreenhouseMembershipPos> changedPositions =
            BuildConfiguredWindMembershipSet(
                regions,
                policy
            );

        changedPositions.SymmetricExceptWith(
            oldMembership
        );

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
            changedPositions.ToArray(),
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

        if (
            !packet.Complete
            && oldSnapshot?.Greenhouses.Length > 0
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
                    "reason=incomplete-positive-revalidation"
                );
            }

            return SnapshotCommitDisposition.Commit;
        }

        long commitStart =
            Stopwatch.GetTimestamp();

        bool configuredMembershipChanged =
            false;

        int checkedPositions = 0;

        foreach (
            GreenhouseMembershipPos position
            in prepared.ChangedConfiguredPositions
        )
        {
            checkedPositions++;

            Block block =
                api.World.BlockAccessor
                    .GetBlock(
                        position.ToBlockPos()
                    );

            if (
                StillGreenhousesShared
                    .IsVegetationCandidate(
                        block,
                        Config
                    )
            )
            {
                configuredMembershipChanged =
                    true;

                break;
            }
        }

        long oldVisualRevision =
            oldSnapshot?.VisualRevision
            ?? 0;

        bool visualRevisionChanged =
            oldSnapshot != null
            && oldVisualRevision
                != prepared.NewSnapshot.VisualRevision;

        bool contentChanged =
            prepared.ExpectedOldContentHash
            != prepared.NewSnapshot.ContentHash;

        int removedRoomWindRegistrations = 0;

        if (configuredMembershipChanged)
        {
            removedRoomWindRegistrations =
                StillGreenhousesRoomWindUniformRenderer
                    .RemovePositionsForChunk(chunkKey);
        }

        ChunkSnapshots[
            chunkKey
        ] = prepared.NewSnapshot;

        RegisterRoomWaterSources(
            api,
            prepared.NewSnapshot
        );

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

        if (configuredMembershipChanged)
        {
            MarkChunkDirty(
                api,
                chunkKey
            );
        }

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
                    $"visualRevisionChanged={visualRevisionChanged}; " +
                    $"configuredMembershipChanged={configuredMembershipChanged}; " +
                    $"removedRoomWindRegistrations={removedRoomWindRegistrations}; " +
                    $"redraw={configuredMembershipChanged}"
                );
            }

            LogSlowClientOperation(
                "prepare-snapshot",
                prepared.PreparationElapsedMs,
                $"regions={prepared.NewSnapshot.Greenhouses.Length}; " +
                $"changedPositions={prepared.ChangedConfiguredPositions.Length}; " +
                "thread=worker"
            );

            LogSlowClientOperation(
                "commit-snapshot",
                commitElapsedMs,
                $"checkedPositions={checkedPositions}; " +
                $"redraw={configuredMembershipChanged}; " +
                "thread=main"
            );
        }

        return SnapshotCommitDisposition.Commit;
    }

    private static HashSet<GreenhouseMembershipPos>
        BuildConfiguredWindMembershipSet(
            IEnumerable<GreenhouseRegion> regions,
            RoomPolicySnapshot policy
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
                in region.GetOccupiedPositions()
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

        api.Event.RegisterCallback(
            _elapsedSeconds =>
            {
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

    private static void RegisterRoomWaterSources(
        ICoreClientAPI api,
        ClientChunkSnapshot snapshot
    )
    {
        StillGreenhousesConfig? config =
            Config;

        if (
            config?.ApplyToWater != true
            || PlantMovementMode
                != RoomPlantMovementMode.VanillaLowWind
        )
        {
            return;
        }

        foreach (
            GreenhouseRegion region
            in snapshot.Greenhouses
        )
        {
            if (!StillGreenhousesShared.IsRoomTypeEnabled(
                    config,
                    region.RoomType
                ))
            {
                continue;
            }

            foreach (
                BlockPos pos
                in region.GetOccupiedPositions()
            )
            {
                if (!StillGreenhousesShared.IsWaterSurfaceSourceBlock(
                        api.World.BlockAccessor,
                        pos
                    ))
                {
                    continue;
                }

                StillGreenhousesRoomWindUniformRenderer
                    .RegisterWaterPosition(
                        pos,
                        region
                    );
            }
        }
    }

    private static void RefreshRoomWaterRegistrationAt(
        ICoreClientAPI api,
        BlockPos pos
    )
    {
        StillGreenhousesConfig? config =
            Config;

        if (
            config?.ApplyToWater != true
            || PlantMovementMode
                != RoomPlantMovementMode.VanillaLowWind
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
            else if (
                RepresentativeVegetationByChunk
                    .ContainsKey(chunkKey)
            )
            {
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

        foreach (ChunkKey chunkKey in candidates)
        {
            if (IsChunkWithinClientRetentionRadius(
                    api,
                    chunkKey
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

            Volatile.Write(
                ref latestPlayerChunkSnapshot,
                new PlayerChunkSnapshot(
                    playerChunk,
                    Volatile.Read(
                        ref clientWorldGeneration
                    )
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
            $"snapshotPacketsQueued={Math.Max(0, Volatile.Read(ref snapshotPacketsQueued))}; " +
            $"discoveryWorkerActive={discoveryWorkerTask?.IsCompleted == false}; " +
            $"snapshotWorkerActive={snapshotPreparationWorkerTask?.IsCompleted == false}; " +
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
            $"roomWindTransport=RoomTypeSharedVanillaStates; " +
            $"roomWindShaderTopology={StillGreenhousesRoomWindShaderPatch.ShaderTopology}; " +
            $"roomWindShaderSpatialMatch={StillGreenhousesRoomWindShaderPatch.AbsolutePositionStrategy}; " +
            $"roomWindDebugVisualProof={Config.DebugRoomWindVisualProof}; " +
            $"greenhouseWindRange={StillGreenhousesRoomWindEnvironment.DescribeRange(Config, ManagedRoomType.Greenhouse)}; " +
            $"greenhouseWindCurrentPercent={StillGreenhousesRoomWindUniformRenderer.GreenhouseCurrentPercent:0.###}; " +
            $"cellarWindRange={StillGreenhousesRoomWindEnvironment.DescribeRange(Config, ManagedRoomType.Cellar)}; " +
            $"cellarWindCurrentPercent={StillGreenhousesRoomWindUniformRenderer.CellarCurrentPercent:0.###}; " +
            $"roomWindRange={StillGreenhousesRoomWindEnvironment.DescribeRange(Config, ManagedRoomType.Room)}; " +
            $"roomWindCurrentPercent={StillGreenhousesRoomWindUniformRenderer.RoomCurrentPercent:0.###}; " +
            $"roomWindRegisteredPositions={StillGreenhousesRoomWindUniformRenderer.RegisteredPositionCount}; " +
            $"roomWindValidPositions={StillGreenhousesRoomWindUniformRenderer.ValidPositionCount}; " +
            $"roomWindUploadedPositions={StillGreenhousesRoomWindUniformRenderer.UploadedPositionCount}/{StillGreenhousesRoomWindUniformRenderer.MaxUploadedPositions}; " +
            $"roomWindPositionBudget=Greenhouse:{StillGreenhousesRoomWindUniformRenderer.GreenhouseReservedPositionBudget},Cellar:{StillGreenhousesRoomWindUniformRenderer.CellarReservedPositionBudget},Room:{StillGreenhousesRoomWindUniformRenderer.RoomReservedPositionBudget}+nearest-redistribution; " +
            $"roomWindUploadedPositionsGreenhouse={StillGreenhousesRoomWindUniformRenderer.UploadedGreenhousePositionCount}; " +
            $"roomWindUploadedPositionsCellar={StillGreenhousesRoomWindUniformRenderer.UploadedCellarPositionCount}; " +
            $"roomWindUploadedPositionsRoom={StillGreenhousesRoomWindUniformRenderer.UploadedRoomPositionCount}; " +
            $"roomWindUploadedStates={StillGreenhousesRoomWindUniformRenderer.UploadedRoomStateCount}/{StillGreenhousesRoomWindUniformRenderer.UploadedRoomTypeStateCount}; " +
            $"roomWindUniformPrograms={StillGreenhousesRoomWindUniformRenderer.ActiveProgramCount}/{StillGreenhousesRoomWindUniformRenderer.TargetProgramCount}; " +
            $"roomWindVegetationPrograms={StillGreenhousesRoomWindUniformRenderer.ActiveVegetationProgramCount}/{StillGreenhousesRoomWindUniformRenderer.VegetationTargetProgramCount}; " +
            $"roomWindLiquidPrograms={StillGreenhousesRoomWindUniformRenderer.ActiveLiquidProgramCount}/{StillGreenhousesRoomWindUniformRenderer.LiquidTargetProgramCount}; " +
            $"roomWindRequiredChunkOpaqueBound={StillGreenhousesRoomWindUniformRenderer.RequiredChunkOpaqueBound}; " +
            $"roomWindPositionSnapshotRevision={StillGreenhousesRoomWindUniformRenderer.SnapshotRevision}"
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
            () => api.Logger.Notification(
                "[StillGreenhouses] ConfigLib settings saved. " +
                "Restart the client for Still Greenhouses changes to take effect."
            ),
            "stillgreenhouses-configlib-restart-notice"
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

            MarkChunkDirty(
                api,
                entry.Key
            );

            redrawCount++;
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

        Volatile.Write(
            ref latestPlayerChunkSnapshot,
            null
        );

        ClearClientCache();
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
        KnownUnclassifiedWindBlocks.Clear();

        StillGreenhousesRoomWindUniformRenderer
            .ClearRegisteredPositions();

        Interlocked.Exchange(
            ref snapshotPacketsQueued,
            0
        );
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
            api.Event.BlockChanged -= OnBlockChanged;

            if (playerChunkTickListenerId != 0)
            {
                api.Event.UnregisterGameTickListener(
                    playerChunkTickListenerId
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
        ChunkKey[] Chunks,
        int Generation,
        double SelectionElapsedMs
    );

    private sealed record QueuedSnapshotPacket(
        GreenhouseChunkSnapshot Packet,
        int Generation
    );

    private sealed record PreparedChunkSnapshot(
        QueuedSnapshotPacket Queued,
        ClientChunkSnapshot NewSnapshot,
        bool ExpectedOldSnapshotExists,
        long ExpectedOldRevision,
        ulong ExpectedOldContentHash,
        RoomPolicySnapshot Policy,
        GreenhouseMembershipPos[] ChangedConfiguredPositions,
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

    private readonly record struct WindAdjustmentLogKey(
        GreenhouseKey Greenhouse,
        string BlockCode,
        RoomPlantMovementMode MovementMode
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
