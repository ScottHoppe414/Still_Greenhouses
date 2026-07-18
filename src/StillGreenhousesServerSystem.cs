/*
version 0.18.0
*/

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace StillGreenhouses;

public sealed partial class StillGreenhousesServerSystem : ModSystem
{
    private const string ServerHarmonyId =
        "stillgreenhouses.server.butterflyweather";

    private const int MaxAutomaticIncompleteChunkRetries = 4;
    private const int DiscoveryBudgetCheckStride = 16;
    private const double DiscoverySliceSafetyFactor = 0.5d;
    private const double SlowServerOperationLogThresholdMs = 8d;

    private static StillGreenhousesServerSystem? activeInstance;

    private ICoreServerAPI? sapi;
    private IServerNetworkChannel? serverChannel;
    private RoomRegistry? roomRegistry;
    private StillGreenhousesConfig? config;
    private Harmony? serverHarmony;

    private readonly ConcurrentDictionary<
        ChunkKey,
        byte
    > PendingMaintenanceScans = new();

    private readonly ConcurrentDictionary<
        ChunkKey,
        byte
    > PendingDiscoveryScans = new();

    private readonly ConcurrentDictionary<
        ChunkKey,
        ChunkDiscoveryScan
    > ActiveChunkDiscoveries = new();

    // The disappearance confirmation path must use the actual result of its
    // forced structural refresh. Reading the live greenhouse index here would
    // reintroduce the stale room that the refresh is trying to disprove.
    private readonly ConcurrentDictionary<
        ChunkKey,
        ChunkDiscoveryResult
    > LatestChunkDiscoveryResults = new();

    private readonly ConcurrentDictionary<
        ChunkKey,
        byte
    > ScheduledChunkScans = new();

    private readonly ConcurrentDictionary<
        StructuralRoomProbeKey,
        StructuralRoomProbeBatch
    > ScheduledStructuralRoomProbes = new();

    // Managed room regions that disappeared completely from the live room
    // graph. The retained geometry is a structural recovery anchor: later wall
    // changes within one block of the old room can trigger an authoritative
    // delayed rescan even when the containing chunk still has unrelated rooms.
    private readonly ConcurrentDictionary<
        GreenhouseKey,
        GreenhouseRegion
    > FormerManagedRooms = new();

    private readonly ConcurrentDictionary<
        ChunkKey,
        ConcurrentDictionary<GreenhouseKey, byte>
    > FormerManagedRoomChunkIndex = new();

    private readonly ConcurrentDictionary<
        GreenhouseKey,
        byte
    > ScheduledFormerManagedRoomScans = new();

    private readonly ConcurrentDictionary<
        GreenhouseKey,
        byte
    > PendingGreenhouseRevalidations = new();

    private readonly ConcurrentDictionary<
        GreenhouseKey,
        byte
    > ScheduledGreenhouseRevalidations = new();

    private readonly ConcurrentDictionary<
        GreenhouseKey,
        PendingRoomDisappearance
    > PendingRoomDisappearances = new();

    private readonly ConcurrentDictionary<
        GreenhouseKey,
        byte
    > ScheduledDisappearanceGraceChecks = new();

    private readonly ConcurrentDictionary<
        GreenhouseKey,
        byte
    > PendingDisappearanceDiscoveryRefreshes = new();

    private readonly ConcurrentDictionary<
        ChunkKey,
        int
    > IncompleteChunkRetryAttempts = new();

    private readonly ConcurrentDictionary<
        ChunkKey,
        byte
    > ScheduledIncompleteChunkRetries = new();

    private readonly ConcurrentDictionary<
        int,
        bool
    > DiscoveryAnchorBlockIdentityCache = new();

    private readonly ConcurrentDictionary<
        GreenhouseKey,
        GreenhouseRegion
    > Greenhouses = new();

    private readonly ConcurrentDictionary<
        ChunkKey,
        ConcurrentDictionary<GreenhouseKey, byte>
    > ChunkGreenhouseIndex = new();

    private readonly ConcurrentDictionary<
        ChunkKey,
        ServerChunkState
    > ChunkStates = new();

    private readonly ConcurrentDictionary<
        ChunkKey,
        ConcurrentDictionary<string, byte>
    > ChunkSubscribers = new();

    private readonly ConcurrentDictionary<
        string,
        ConcurrentDictionary<ChunkKey, byte>
    > PlayerSubscriptions = new();

    private long chunkScanListenerId;
    private long subscriptionPruneListenerId;
    private long debugSummaryListenerId;

    private long discoveryAnchorPositionsObserved;
    private long discoveryRoomRegistryQueriesObserved;
    private long discoveryCoveredRoomPositionsSkipped;
    private long discoveryAnchorRoomInstancesObserved;
    private long discoveryIncompleteAnchorRoomsObserved;
    private long roomViabilityChecks;
    private long roomsSkippedTooSmall;
    private long roomsSkippedWithoutDiscoveryAnchor;
    private long formerRoomsSkippedTooSmall;
    private long formerRoomsSkippedWithoutDiscoveryAnchor;
    private long roomDisappearanceGraceStarted;
    private long roomDisappearanceZeroObservations;
    private long roomDisappearanceGraceCleared;
    private long roomDisappearanceGraceConfirmed;
    private long roomDisappearanceFinalChecksIncomplete;
    private long roomDisappearanceIncompletePositiveRetained;
    private long roomDisappearanceIncompleteRetentionExtended;
    private long seedRoomRegistryObservationRuns;
    private long incompleteChunkRetriesScheduled;
    private long pendingChunkAcknowledgementsPublished;
    private long duplicateChunkScanAdmissionsSuppressed;
    private long staleChunkScanQueueEntriesRemoved;

    private long serverForegroundBudgetYields;
    private long serverScanOperations;
    private long serverRevalidationOperations;
    private long discoveryOperations;
    private long discoveryPositionsVisited;
    private long butterflyWeatherLookups;
    private long butterflyShelteredWeatherLookups;
    private long butterflyWanderBoostLookups;
    private long butterflyShelteredWanderBoosts;
    private long lastServerScanMicroseconds;
    private long maxServerScanMicroseconds;
    private long lastServerRevalidationMicroseconds;
    private long maxServerRevalidationMicroseconds;
    private long lastDiscoveryMicroseconds;
    private long maxDiscoveryMicroseconds;

    private enum ManagedRoomViability
    {
        Viable,
        TooSmall,
        NoDiscoveryAnchor
    }

    private void DebugLiteral(string message)
    {
        if (config?.DebugLogging != true)
        {
            return;
        }

        try
        {
            sapi?.Logger.Debug("{0}", message);
        }
        catch
        {
            // Diagnostics must never interrupt authoritative cache work.
        }
    }

    private void WarningLiteral(string message)
    {
        try
        {
            sapi?.Logger.Warning("{0}", message);
        }
        catch
        {
            // Logging must never interrupt authoritative cache work.
        }
    }

    public override bool ShouldLoad(EnumAppSide side) =>
        side == EnumAppSide.Server;

    public override void Start(ICoreAPI api)
    {
        api.Network
            .RegisterChannel(StillGreenhousesNetwork.ChannelName)
            .RegisterMessageType<GreenhouseChunkBatchRequest>()
            .RegisterMessageType<GreenhouseChunkSnapshot>()
            .RegisterMessageType<RoomInspectionRequest>()
            .RegisterMessageType<RoomInspectionResponse>();
    }

    public override void StartServerSide(
        ICoreServerAPI api
    )
    {
        sapi = api;

        config = StillGreenhousesShared.LoadConfig(
            api,
            storeNormalizedConfig: true
        );

        serverHarmony = new Harmony(ServerHarmonyId);

        try
        {
            StillGreenhousesButterflyWeatherPatch.Apply(
                serverHarmony
            );

            api.Logger.Notification(
                "[StillGreenhouses] Server butterfly room-weather patch applied."
            );
        }
        catch (Exception e)
        {
            serverHarmony = null;

            api.Logger.Error(
                "[StillGreenhouses] Could not patch butterfly room-weather AI. " +
                "Any successfully installed process-wide hooks remain safe and " +
                $"uninstalled hooks retain Vanilla behavior. Error={e}"
            );
        }

        roomRegistry =
            api.ModLoader.GetModSystem<RoomRegistry>();

        if (roomRegistry == null)
        {
            api.Logger.Error(
                "[StillGreenhouses] Server RoomRegistry was not found. " +
                "Authoritative room detection cannot run."
            );
        }
        else
        {
            api.Logger.Notification(
                "[StillGreenhouses] Server RoomRegistry found."
            );
        }

        serverChannel =
            api.Network
                .GetChannel(StillGreenhousesNetwork.ChannelName)
                .SetMessageHandler<GreenhouseChunkBatchRequest>(
                    OnChunkBatchRequest
                )
                .SetMessageHandler<RoomInspectionRequest>(
                    OnRoomInspectionRequest
                );

        api.Event.DidPlaceBlock += OnDidPlaceBlock;
        api.Event.DidBreakBlock += OnDidBreakBlock;
        api.Event.DidUseBlock += OnDidUseBlock;
        api.Event.ChunkDirty += OnChunkDirty;
        api.Event.PlayerDisconnect += OnPlayerDisconnect;

        chunkScanListenerId =
            api.Event.RegisterGameTickListener(
                ProcessPendingServerScans,
                250
            );

        subscriptionPruneListenerId =
            api.Event.RegisterGameTickListener(
                PruneSubscriptionsAndCache,
                60000
            );

        debugSummaryListenerId =
            api.Event.RegisterGameTickListener(
                LogServerCacheSummary,
                5000
            );

        // Publish only after the server-side room cache and event pipeline are
        // fully initialized. Butterfly AI falls back to Vanilla behavior until
        // this instance is ready.
        Volatile.Write(
            ref activeInstance,
            this
        );

        api.Logger.Notification(
                $"[StillGreenhouses] Server loaded v0.18.0. " +
            $"Enabled={config.Enabled}, " +
            $"DebugLogging={config.DebugLogging}, " +
            $"MinimumManagedRoomInteriorPositions={config.MinimumManagedRoomInteriorPositions}, " +
            $"MaxServerChunkScansPerTick={config.MaxServerChunkScansPerTick}, " +
            $"ServerForegroundWorkBudgetMs={config.ServerForegroundWorkBudgetMs:0.###}, " +
            $"ServerRoomDisappearanceGraceMs={config.ServerRoomDisappearanceGraceMs}, " +
            $"ServerIncompleteRoomDisappearanceRetentionMs={config.ServerIncompleteRoomDisappearanceRetentionMs}, " +
            $"ServerSubscriptionRadiusChunks={config.ServerSubscriptionRadiusChunks}, " +
            $"AllowClientRoomInspectionOverlay={config.AllowClientRoomInspectionOverlay}, " +
            $"RoomInspectionFailureRadius={config.RoomInspectionFailureRadius}."
        );
    }

    internal static bool TryResolveShelteredButterflyRoom(
        EntityAgent entity,
        Vec3d weatherPosition,
        bool countAsWeatherLookup = true
    )
    {
        StillGreenhousesServerSystem? instance =
            Volatile.Read(
                ref activeInstance
            );

        if (
            instance?.config?.Enabled != true
            || entity.Pos == null
        )
        {
            return false;
        }

        if (countAsWeatherLookup)
        {
            Interlocked.Increment(
                ref instance.butterflyWeatherLookups
            );
        }
        else
        {
            Interlocked.Increment(
                ref instance.butterflyWanderBoostLookups
            );
        }

        BlockPos blockPos = new(
            (int)Math.Floor(weatherPosition.X),
            (int)Math.Floor(weatherPosition.Y),
            (int)Math.Floor(weatherPosition.Z),
            entity.Pos.Dimension
        );

        ChunkKey chunkKey =
            ChunkKey.From(blockPos);

        if (!instance.ChunkGreenhouseIndex.TryGetValue(
                chunkKey,
                out ConcurrentDictionary<
                    GreenhouseKey,
                    byte
                >? index
            ))
        {
            return false;
        }

        foreach (
            GreenhouseKey greenhouseKey
            in index.Keys
        )
        {
            if (
                instance.Greenhouses.TryGetValue(
                    greenhouseKey,
                    out GreenhouseRegion? greenhouse
                )
                && greenhouse.Contains(blockPos)
            )
            {
                if (countAsWeatherLookup)
                {
                    Interlocked.Increment(
                        ref instance
                            .butterflyShelteredWeatherLookups
                    );
                }
                else
                {
                    Interlocked.Increment(
                        ref instance
                            .butterflyShelteredWanderBoosts
                    );
                }

                return true;
            }
        }

        return false;
    }

    private void OnChunkBatchRequest(
        IServerPlayer fromPlayer,
        GreenhouseChunkBatchRequest packet
    )
    {
        ICoreServerAPI? api = sapi;

        if (
            api == null
            || config?.Enabled != true
            || packet.Chunks == null
            || packet.Chunks.Count == 0
        )
        {
            return;
        }

        string playerUid =
            fromPlayer.PlayerUID;

        ChunkKey[] requestedChunks =
            packet.Chunks
                .Take(32)
                .Select(ChunkKey.From)
                .Distinct()
                .ToArray();

        if (requestedChunks.Length == 0)
        {
            return;
        }

        api.Event.EnqueueMainThreadTask(
            () =>
            {
                IServerPlayer? player =
                    api.World.PlayerByUid(playerUid)
                    as IServerPlayer;

                if (player == null)
                {
                    return;
                }

                foreach (ChunkKey chunkKey in requestedChunks)
                {
                    HandleChunkRequest(
                        player,
                        chunkKey
                    );
                }
            },
            "stillgreenhouses-server-request-batch"
        );
    }

    private void HandleChunkRequest(
        IServerPlayer player,
        ChunkKey chunkKey
    )
    {
        if (!IsRequestReasonable(
                player,
                chunkKey
            ))
        {
            return;
        }

        SubscribePlayer(
            player.PlayerUID,
            chunkKey
        );

        if (
            ChunkStates.TryGetValue(
                chunkKey,
                out ServerChunkState? state
            )
        )
        {
            SendSnapshotToPlayer(
                chunkKey,
                player
            );

            if (
                !state.Complete
                && !IsChunkScanQueuedOrActive(chunkKey)
                && !ScheduledIncompleteChunkRetries
                    .ContainsKey(chunkKey)
            )
            {
                // An explicit subscriber request is a fresh owner for an
                // incomplete state. A previous exhausted/orphaned attempt
                // counter must not strand this client on Complete=false.
                ResetIncompleteChunkRetry(chunkKey);

                PendingMaintenanceScans.TryAdd(
                    chunkKey,
                    0
                );
            }

            return;
        }

        if (IsChunkScanQueuedOrActive(chunkKey))
        {
            Interlocked.Increment(
                ref duplicateChunkScanAdmissionsSuppressed
            );

            return;
        }

        if (!PendingDiscoveryScans.TryAdd(chunkKey, 0))
        {
            Interlocked.Increment(
                ref duplicateChunkScanAdmissionsSuppressed
            );

            return;
        }

        // Publish an authoritative pending acknowledgement immediately. The
        // client treats Complete=false as server-owned work and stops its
        // one-second transport timeout from resubmitting this chunk while a
        // multi-slice discovery scan is still running.
        SetChunkStateAndPublish(
            chunkKey,
            complete: false
        );

        Interlocked.Increment(
            ref pendingChunkAcknowledgementsPublished
        );
    }

    private bool IsChunkScanQueuedOrActive(
        ChunkKey chunkKey
    ) =>
        ActiveChunkDiscoveries.ContainsKey(chunkKey)
        || PendingMaintenanceScans.ContainsKey(chunkKey)
        || PendingDiscoveryScans.ContainsKey(chunkKey)
        || ScheduledChunkScans.ContainsKey(chunkKey);

    private void ProcessPendingServerScans(
        float dt
    )
    {
        ICoreServerAPI? api = sapi;
        StillGreenhousesConfig? currentConfig =
            config;

        if (
            api == null
            || currentConfig?.Enabled != true
            || roomRegistry == null
        )
        {
            return;
        }

        if (
            PendingGreenhouseRevalidations.IsEmpty
            && PendingMaintenanceScans.IsEmpty
            && PendingDiscoveryScans.IsEmpty
        )
        {
            return;
        }

        long schedulerStart =
            Stopwatch.GetTimestamp();

        Dictionary<string, ChunkKey> playerChunks =
            BuildPlayerChunkCache(api);

        int processed = 0;

        while (
            processed
            < currentConfig.MaxServerChunkScansPerTick
        )
        {
            if (
                processed > 0
                && GetElapsedMilliseconds(
                    schedulerStart
                ) >= currentConfig
                    .ServerForegroundWorkBudgetMs
            )
            {
                Interlocked.Increment(
                    ref serverForegroundBudgetYields
                );

                break;
            }

            if (TryTakeNearestPendingGreenhouse(
                    playerChunks,
                    out GreenhouseKey greenhouseKey
                ))
            {
                processed++;

                long revalidationStart =
                    Stopwatch.GetTimestamp();

                try
                {
                    RevalidateGreenhouse(
                        api,
                        greenhouseKey
                    );
                }
                catch (Exception e)
                {
                    WarningLiteral(
                        "[StillGreenhouses] Server greenhouse revalidation failed. " +
                        $"greenhouseDim={greenhouseKey.Dimension}; " +
                        $"bounds={greenhouseKey.X1},{greenhouseKey.Y1},{greenhouseKey.Z1}.." +
                        $"{greenhouseKey.X2},{greenhouseKey.Y2},{greenhouseKey.Z2}. " +
                        $"{e.GetType().Name}: {e.Message}"
                    );

                    ScheduleGreenhouseRevalidation(
                        greenhouseKey
                    );
                }
                finally
                {
                    RecordServerOperationPerformance(
                        "greenhouse-revalidation",
                        revalidationStart,
                        ref serverRevalidationOperations,
                        ref lastServerRevalidationMicroseconds,
                        ref maxServerRevalidationMicroseconds,
                        currentConfig.DebugLogging
                            ? $"greenhouseDim={greenhouseKey.Dimension}; " +
                              $"bounds={greenhouseKey.X1},{greenhouseKey.Y1},{greenhouseKey.Z1}.." +
                              $"{greenhouseKey.X2},{greenhouseKey.Y2},{greenhouseKey.Z2}; " +
                              "thread=server-main"
                            : string.Empty
                    );
                }

                continue;
            }

            bool found =
                TryTakeNearestPendingChunk(
                    PendingMaintenanceScans,
                    playerChunks,
                    out ChunkKey chunkKey
                )
                || TryTakeNearestPendingChunk(
                    PendingDiscoveryScans,
                    playerChunks,
                    out chunkKey
                );

            if (!found)
            {
                break;
            }

            // A timed-out client retry could previously leave the same chunk
            // in both queues. Whichever queue won selection owns this slice;
            // clear the other entry before scanning so completion cannot be
            // followed by an accidental second full discovery.
            int staleQueueEntriesRemoved = 0;

            if (PendingMaintenanceScans.TryRemove(
                    chunkKey,
                    out _
                ))
            {
                staleQueueEntriesRemoved++;
            }

            if (PendingDiscoveryScans.TryRemove(
                    chunkKey,
                    out _
                ))
            {
                staleQueueEntriesRemoved++;
            }

            if (staleQueueEntriesRemoved > 0)
            {
                Interlocked.Add(
                    ref staleChunkScanQueueEntriesRemoved,
                    staleQueueEntriesRemoved
                );
            }

            processed++;

            long operationStart =
                Stopwatch.GetTimestamp();

            bool scanCompleted = false;

            try
            {
                scanCompleted = ScanChunk(
                    api,
                    chunkKey,
                    GetEffectiveDiscoveryWorkBudgetMs(
                        currentConfig
                            .ServerForegroundWorkBudgetMs
                    )
                );

                if (!scanCompleted)
                {
                    PendingMaintenanceScans[
                        chunkKey
                    ] = 0;

                    Interlocked.Increment(
                        ref serverForegroundBudgetYields
                    );
                }
            }
            catch (Exception e)
            {
                ActiveChunkDiscoveries.TryRemove(
                    chunkKey,
                    out _
                );

                WarningLiteral(
                    "[StillGreenhouses] Server greenhouse scan failed for " +
                    $"chunk={chunkKey.X},{chunkKey.Y},{chunkKey.Z}; " +
                    $"dim={chunkKey.Dimension}. " +
                    $"{e.GetType().Name}: {e.Message}"
                );

                LatestChunkDiscoveryResults[
                    chunkKey
                ] = ChunkDiscoveryResult.Unavailable(
                    chunkKey
                );

                SetChunkStateAndPublish(
                    chunkKey,
                    complete: false
                );

                ScheduleChunkScan(chunkKey);
            }
            finally
            {
                RecordServerOperationPerformance(
                    "chunk-scan",
                    operationStart,
                    ref serverScanOperations,
                    ref lastServerScanMicroseconds,
                    ref maxServerScanMicroseconds,
                    currentConfig.DebugLogging
                        ? $"chunk={chunkKey.X},{chunkKey.Y},{chunkKey.Z}; " +
                          $"dim={chunkKey.Dimension}; " +
                          $"completed={scanCompleted}; " +
                          "thread=server-main"
                        : string.Empty
                );
            }
        }

        if (currentConfig.DebugLogging)
        {
            LogSlowServerOperation(
                "scan-scheduler",
                GetElapsedMilliseconds(
                    schedulerStart
                ),
                $"processed={processed}; " +
                $"maxPerTick={currentConfig.MaxServerChunkScansPerTick}; " +
                $"foregroundBudgetMs={currentConfig.ServerForegroundWorkBudgetMs:0.###}; " +
                $"effectiveSliceBudgetMs={GetEffectiveDiscoveryWorkBudgetMs(currentConfig.ServerForegroundWorkBudgetMs):0.###}; " +
                $"greenhouseRevalidationsPending={PendingGreenhouseRevalidations.Count}; " +
                $"maintenancePending={PendingMaintenanceScans.Count}; " +
                $"discoveryPending={PendingDiscoveryScans.Count}; " +
                "thread=server-main"
            );
        }
    }

    private void RecordServerOperationPerformance(
        string operation,
        long startTimestamp,
        ref long operationCounter,
        ref long lastMicroseconds,
        ref long maxMicroseconds,
        string details
    )
    {
        long elapsedMicroseconds =
            GetElapsedMicroseconds(
                startTimestamp
            );

        Interlocked.Increment(
            ref operationCounter
        );

        Interlocked.Exchange(
            ref lastMicroseconds,
            elapsedMicroseconds
        );

        UpdateMaximum(
            ref maxMicroseconds,
            elapsedMicroseconds
        );

        LogSlowServerOperation(
            operation,
            elapsedMicroseconds / 1000d,
            details
        );
    }

    private void LogSlowServerOperation(
        string operation,
        double elapsedMs,
        string details
    )
    {
        if (
            config?.DebugLogging != true
            || elapsedMs
               < SlowServerOperationLogThresholdMs
        )
        {
            return;
        }

        DebugLiteral(
            "[StillGreenhouses] SERVER PERF " +
            $"operation={operation}; " +
            $"elapsedMs={elapsedMs:F3}; " +
            details
        );
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

    private static double GetEffectiveDiscoveryWorkBudgetMs(
        double configuredBudgetMs
    ) =>
        Math.Max(
            0.25d,
            configuredBudgetMs
            * DiscoverySliceSafetyFactor
        );

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

    private Dictionary<string, ChunkKey> BuildPlayerChunkCache(
        ICoreServerAPI api
    )
    {
        Dictionary<string, ChunkKey> result = new();

        foreach (
            KeyValuePair<
                string,
                ConcurrentDictionary<ChunkKey, byte>
            > subscription
            in PlayerSubscriptions
        )
        {
            string playerUid =
                subscription.Key;

            if (
                api.World.PlayerByUid(playerUid)
                is not IServerPlayer player
            )
            {
                continue;
            }

            BlockPos playerPos =
                new BlockPos(
                    player.Entity.Pos.Dimension
                )
                .Set(player.Entity.Pos);

            result[playerUid] =
                ChunkKey.From(playerPos);
        }

        return result;
    }

    private bool TryTakeNearestPendingGreenhouse(
        IReadOnlyDictionary<string, ChunkKey> playerChunks,
        out GreenhouseKey greenhouseKey
    )
    {
        greenhouseKey = default;

        bool found = false;
        long bestPriority = long.MaxValue;

        foreach (
            KeyValuePair<GreenhouseKey, byte> pending
            in PendingGreenhouseRevalidations
        )
        {
            GreenhouseKey candidate =
                pending.Key;

            if (
                !Greenhouses.TryGetValue(
                    candidate,
                    out GreenhouseRegion? greenhouse
                )
            )
            {
                PendingGreenhouseRevalidations.TryRemove(
                    candidate,
                    out _
                );

                continue;
            }

            long priority =
                GetGreenhouseRevalidationPriority(
                    greenhouse,
                    playerChunks
                );

            if (
                found
                && priority >= bestPriority
            )
            {
                continue;
            }

            found = true;
            bestPriority = priority;
            greenhouseKey = candidate;
        }

        return found
               && PendingGreenhouseRevalidations.TryRemove(
                   greenhouseKey,
                   out _
               );
    }

    private long GetGreenhouseRevalidationPriority(
        GreenhouseRegion greenhouse,
        IReadOnlyDictionary<string, ChunkKey> playerChunks
    )
    {
        ChunkKey centerChunk = new(
            StillGreenhousesShared.FloorDiv(
                greenhouse.X1 + (greenhouse.X2 - greenhouse.X1) / 2,
                StillGreenhousesShared.ChunkSize
            ),
            StillGreenhousesShared.FloorDiv(
                greenhouse.Y1 + (greenhouse.Y2 - greenhouse.Y1) / 2,
                StillGreenhousesShared.ChunkSize
            ),
            StillGreenhousesShared.FloorDiv(
                greenhouse.Z1 + (greenhouse.Z2 - greenhouse.Z1) / 2,
                StillGreenhousesShared.ChunkSize
            ),
            greenhouse.Dimension
        );

        long bestPriority = long.MaxValue;

        foreach (ChunkKey playerChunk in playerChunks.Values)
        {
            if (
                playerChunk.Dimension
                != centerChunk.Dimension
            )
            {
                continue;
            }

            long dx =
                centerChunk.X - playerChunk.X;

            long dy =
                centerChunk.Y - playerChunk.Y;

            long dz =
                centerChunk.Z - playerChunk.Z;

            long priority =
                dx * dx
                + dy * dy
                + dz * dz;

            if (priority < bestPriority)
            {
                bestPriority = priority;
            }
        }

        return bestPriority;
    }

    private bool TryTakeNearestPendingChunk(
        ConcurrentDictionary<ChunkKey, byte> queue,
        IReadOnlyDictionary<string, ChunkKey> playerChunks,
        out ChunkKey chunkKey
    )
    {
        chunkKey = default;

        bool found = false;
        long bestPriority = long.MaxValue;

        foreach (
            KeyValuePair<ChunkKey, byte> pending
            in queue
        )
        {
            ChunkKey candidate =
                pending.Key;

            long priority =
                GetChunkScanPriority(
                    candidate,
                    playerChunks
                );

            if (
                found
                && priority >= bestPriority
            )
            {
                continue;
            }

            bestPriority = priority;
            chunkKey = candidate;
            found = true;
        }

        return found
               && queue.TryRemove(
                   chunkKey,
                   out _
               );
    }

    private long GetChunkScanPriority(
        ChunkKey chunkKey,
        IReadOnlyDictionary<string, ChunkKey> playerChunks
    )
    {
        if (
            !ChunkSubscribers.TryGetValue(
                chunkKey,
                out ConcurrentDictionary<
                    string,
                    byte
                >? subscribers
            )
        )
        {
            return long.MaxValue;
        }

        long bestPriority = long.MaxValue;

        foreach (
            KeyValuePair<string, byte> subscriber
            in subscribers
        )
        {
            string playerUid =
                subscriber.Key;

            if (
                !playerChunks.TryGetValue(
                    playerUid,
                    out ChunkKey playerChunk
                )
                || playerChunk.Dimension
                    != chunkKey.Dimension
            )
            {
                continue;
            }

            long dx =
                chunkKey.X - playerChunk.X;

            long dy =
                chunkKey.Y - playerChunk.Y;

            long dz =
                chunkKey.Z - playerChunk.Z;

            long priority =
                dx * dx
                + dy * dy
                + dz * dz;

            if (priority < bestPriority)
            {
                bestPriority = priority;
            }
        }

        return bestPriority;
    }

    private bool ScanChunk(
        ICoreServerAPI api,
        ChunkKey chunkKey,
        double workBudgetMs
    )
    {
        ChunkDiscoveryScan scan;

        if (
            !ActiveChunkDiscoveries.TryGetValue(
                chunkKey,
                out ChunkDiscoveryScan? activeScan
            )
        )
        {
            if (!TryCreateChunkDiscoveryScan(
                    api,
                    chunkKey,
                    out scan
                ))
            {
                CommitChunkDiscovery(
                    ChunkDiscoveryResult.Unavailable(
                        chunkKey
                    )
                );

                return true;
            }

            ActiveChunkDiscoveries[
                chunkKey
            ] = scan;
        }
        else
        {
            scan = activeScan!;
        }

        if (!AdvanceChunkDiscoveryScan(
                api,
                scan,
                Math.Max(0.25d, workBudgetMs)
            ))
        {
            return false;
        }

        ActiveChunkDiscoveries.TryRemove(
            chunkKey,
            out _
        );

        PendingMaintenanceScans.TryRemove(
            chunkKey,
            out _
        );

        PendingDiscoveryScans.TryRemove(
            chunkKey,
            out _
        );

        CommitChunkDiscovery(
            CompleteChunkDiscoveryScan(scan)
        );

        return true;
    }

    private bool TryCreateChunkDiscoveryScan(
        ICoreServerAPI api,
        ChunkKey chunkKey,
        out ChunkDiscoveryScan scan
    )
    {
        RoomRegistry? registry =
            roomRegistry;

        if (registry == null)
        {
            scan = null!;
            return false;
        }

        BlockPos representative =
            chunkKey.ToRepresentativeBlockPos();

        IWorldChunk? loadedChunk =
            api.World.BlockAccessor
                .GetChunkAtBlockPos(representative);

        if (loadedChunk == null)
        {
            scan = null!;
            return false;
        }

        loadedChunk.Unpack_ReadOnly();

        scan = new ChunkDiscoveryScan(
            chunkKey,
            registry,
            loadedChunk
        );

        return true;
    }

    private bool AdvanceChunkDiscoveryScan(
        ICoreServerAPI api,
        ChunkDiscoveryScan scan,
        double workBudgetMs
    )
    {
        long sliceStart =
            Stopwatch.GetTimestamp();

        scan.SliceCount++;

        try
        {
            while (
                scan.NextLocalIndex
                < scan.CoveredRoomPositions.Length
            )
            {
                int anchorLocalIndex = -1;
                bool budgetReached = false;

                if (scan.SourceChunk.Disposed)
                {
                    throw new InvalidOperationException(
                        "Source chunk was disposed during room discovery."
                    );
                }

                // The API explicitly supports reliable read-only bulk access
                // through IWorldChunk.Data. This avoids resolving and locking
                // the same chunk through BlockAccessor for all 32^3 positions.
                // Release the chunk-data lock before consulting RoomRegistry,
                // which may inspect this or neighboring chunks itself.
                scan.ChunkData.TakeBulkReadLock();

                try
                {
                    while (
                        scan.NextLocalIndex
                        < scan.CoveredRoomPositions.Length
                    )
                    {
                        int localIndex =
                            scan.NextLocalIndex++;

                        if (scan.CoveredRoomPositions[localIndex])
                        {
                            scan.CoveredRoomPositionsSkipped++;
                        }
                        else
                        {
                            int blockId =
                                scan.ChunkData.GetBlockIdUnsafe(
                                    localIndex
                                );

                            Block? block =
                                blockId >= 0
                                && blockId < api.World.Blocks.Count
                                    ? api.World.Blocks[blockId]
                                    : null;

                            if (IsDiscoveryAnchorBlock(block))
                            {
                                anchorLocalIndex = localIndex;
                                break;
                            }
                        }

                        if (
                            !double.IsPositiveInfinity(
                                workBudgetMs
                            )
                            && scan.NextLocalIndex
                               % DiscoveryBudgetCheckStride == 0
                            && GetElapsedMilliseconds(
                                sliceStart
                            ) >= workBudgetMs
                        )
                        {
                            budgetReached = true;
                            break;
                        }
                    }
                }
                finally
                {
                    scan.ChunkData.ReleaseBulkReadLock();
                }

                if (anchorLocalIndex >= 0)
                {
                    int localX =
                        anchorLocalIndex % scan.ChunkSize;

                    int localYZ =
                        anchorLocalIndex / scan.ChunkSize;

                    int localZ =
                        localYZ % scan.ChunkSize;

                    int localY =
                        localYZ / scan.ChunkSize;

                    scan.Probe.Set(
                        scan.StartX + localX,
                        scan.StartY + localY,
                        scan.StartZ + localZ
                    );

                    ObserveDiscoveryAnchor(scan);
                }

                if (
                    !double.IsPositiveInfinity(workBudgetMs)
                    && (
                        budgetReached
                        || GetElapsedMilliseconds(sliceStart)
                            >= workBudgetMs
                    )
                    && scan.NextLocalIndex
                        < scan.CoveredRoomPositions.Length
                )
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            long sliceMicroseconds =
                GetElapsedMicroseconds(
                    sliceStart
                );

            scan.ProcessingMicroseconds +=
                sliceMicroseconds;

            scan.MaxSliceMicroseconds =
                Math.Max(
                    scan.MaxSliceMicroseconds,
                    sliceMicroseconds
                );
        }
    }

    private void ObserveDiscoveryAnchor(
        ChunkDiscoveryScan scan
    )
    {
        scan.DiscoveryAnchorPositions++;
        scan.RoomRegistryQueries++;

        Room? room =
            scan.Registry.GetRoomForPosition(
                scan.Probe
            );

        if (room == null)
        {
            return;
        }

        MarkRoomCoveredInChunk(
            room,
            scan.ChunkKey,
            scan.CoveredRoomPositions
        );

        if (!scan.SeenRoomInstances.Add(room))
        {
            return;
        }

        scan.AnchorRoomInstances++;

        if (room.AnyChunkUnloaded > 0)
        {
            scan.IncompleteAnchorRooms++;

            scan.IncompleteRoomBounds.Add(
                RoomBounds.From(room)
            );

            return;
        }

        if (
            !StillGreenhousesShared.TryClassifyRoom(
                room,
                out ManagedRoomType roomType
            )
        )
        {
            return;
        }

        scan.ClassifiedManagedRooms++;

        Interlocked.Increment(
            ref roomViabilityChecks
        );

        int occupiedPositionCount =
            StillGreenhousesShared
                .CountRoomOccupiedPositions(room);

        switch (occupiedPositionCount)
        {
            case <= 2:
                scan.RoomSizes1To2++;
                break;

            case <= 6:
                scan.RoomSizes3To6++;
                break;

            case <= 15:
                scan.RoomSizes7To15++;
                break;

            case <= 31:
                scan.RoomSizes16To31++;
                break;

            default:
                scan.RoomSizes32Plus++;
                break;
        }

        if (
            occupiedPositionCount
            < (
                config
                    ?.MinimumManagedRoomInteriorPositions
                ?? 7
            )
        )
        {
            scan.SkippedTooSmallRooms++;

            Interlocked.Increment(
                ref roomsSkippedTooSmall
            );

            return;
        }

        RoomRevalidationKey roomKey =
            RoomRevalidationKey.From(room);

        if (!scan.SeenRoomKeys.Add(roomKey))
        {
            return;
        }

        GreenhouseRegion greenhouse =
            GreenhouseRegion.FromRoom(
                room,
                scan.ChunkKey.Dimension,
                roomType
            );

        scan.DiscoveredRooms[
            greenhouse.Key
        ] = greenhouse;

        scan.ViableManagedRooms++;
    }

    private ChunkDiscoveryResult CompleteChunkDiscoveryScan(
        ChunkDiscoveryScan scan
    )
    {
        Interlocked.Add(
            ref discoveryAnchorPositionsObserved,
            scan.DiscoveryAnchorPositions
        );

        Interlocked.Add(
            ref discoveryRoomRegistryQueriesObserved,
            scan.RoomRegistryQueries
        );

        Interlocked.Add(
            ref discoveryCoveredRoomPositionsSkipped,
            scan.CoveredRoomPositionsSkipped
        );

        Interlocked.Add(
            ref discoveryAnchorRoomInstancesObserved,
            scan.AnchorRoomInstances
        );

        Interlocked.Add(
            ref discoveryIncompleteAnchorRoomsObserved,
            scan.IncompleteAnchorRooms
        );

        Interlocked.Increment(
            ref discoveryOperations
        );

        Interlocked.Add(
            ref discoveryPositionsVisited,
            scan.NextLocalIndex
        );

        Interlocked.Exchange(
            ref lastDiscoveryMicroseconds,
            scan.MaxSliceMicroseconds
        );

        UpdateMaximum(
            ref maxDiscoveryMicroseconds,
            scan.MaxSliceMicroseconds
        );

        if (config?.DebugLogging == true)
        {
            LogSlowServerOperation(
                "discover-managed-rooms",
                scan.MaxSliceMicroseconds / 1000d,
                $"chunk={scan.ChunkKey.X},{scan.ChunkKey.Y},{scan.ChunkKey.Z}; " +
                $"dim={scan.ChunkKey.Dimension}; " +
                $"positionsVisited={scan.NextLocalIndex}; " +
                $"slices={scan.SliceCount}; " +
                $"processingMs={scan.ProcessingMicroseconds / 1000d:F3}; " +
                $"maxSliceMs={scan.MaxSliceMicroseconds / 1000d:F3}; " +
                $"anchorPositions={scan.DiscoveryAnchorPositions}; " +
                $"roomRegistryQueries={scan.RoomRegistryQueries}; " +
                $"coveredSkipped={scan.CoveredRoomPositionsSkipped}; " +
                $"managedRooms={scan.DiscoveredRooms.Count}; " +
                $"complete={scan.IncompleteAnchorRooms == 0}; " +
                "thread=server-main"
            );
        }

        return new ChunkDiscoveryResult(
            ChunkKey: scan.ChunkKey,
            Loaded: true,
            Complete: scan.IncompleteAnchorRooms == 0,
            ManagedRooms: scan.DiscoveredRooms,
            IncompleteRoomBounds: scan.IncompleteRoomBounds,
            DiscoveryAnchorPositions:
                scan.DiscoveryAnchorPositions,
            RoomRegistryQueries:
                scan.RoomRegistryQueries,
            CoveredRoomPositionsSkipped:
                scan.CoveredRoomPositionsSkipped,
            AnchorRoomInstances:
                scan.AnchorRoomInstances,
            IncompleteAnchorRooms:
                scan.IncompleteAnchorRooms,
            ClassifiedManagedRooms:
                scan.ClassifiedManagedRooms,
            ViableManagedRooms:
                scan.ViableManagedRooms,
            SkippedTooSmallRooms:
                scan.SkippedTooSmallRooms,
            RoomSizes1To2: scan.RoomSizes1To2,
            RoomSizes3To6: scan.RoomSizes3To6,
            RoomSizes7To15: scan.RoomSizes7To15,
            RoomSizes16To31: scan.RoomSizes16To31,
            RoomSizes32Plus: scan.RoomSizes32Plus
        );
    }

    private void CommitChunkDiscovery(
        ChunkDiscoveryResult discovery
    )
    {
        ChunkKey chunkKey =
            discovery.ChunkKey;

        LatestChunkDiscoveryResults[
            chunkKey
        ] = discovery;

        if (!discovery.Loaded)
        {
            SetChunkStateAndPublish(
                chunkKey,
                complete: false
            );

            ScheduleIncompleteChunkRetry(
                chunkKey,
                reason: "chunk-unavailable"
            );

            return;
        }

        HashSet<ChunkKey> regionAffectedChunks =
            new();

        foreach (
            GreenhouseRegion greenhouse
            in discovery.ManagedRooms.Values
        )
        {
            foreach (
                ChunkKey affectedChunk
                in InstallGreenhouse(greenhouse)
            )
            {
                regionAffectedChunks.Add(
                    affectedChunk
                );
            }
        }

        foreach (
            ChunkKey affectedChunk
            in regionAffectedChunks
        )
        {
            if (affectedChunk == chunkKey)
            {
                continue;
            }

            bool affectedComplete =
                ChunkStates.TryGetValue(
                    affectedChunk,
                    out ServerChunkState? state
                )
                && state.Complete;

            SetChunkStateAndPublish(
                affectedChunk,
                affectedComplete
            );
        }

        SetChunkStateAndPublish(
            chunkKey,
            complete: discovery.Complete
        );

        if (discovery.Complete)
        {
            ResetIncompleteChunkRetry(chunkKey);
        }
        else
        {
            ScheduleIncompleteChunkRetry(
                chunkKey,
                reason: "incomplete-anchor-room"
            );
        }

        List<GreenhouseRegion> managedRooms =
            GetGreenhousesForChunk(chunkKey);

        if (
            config?.DebugLogging == true
            && (
                managedRooms.Count > 0
                || discovery.DiscoveryAnchorPositions > 0
                || discovery.SkippedTooSmallRooms > 0
                || !discovery.Complete
            )
        )
        {
            string roomTypes =
                managedRooms.Count == 0
                    ? "<none>"
                    : string.Join(
                        ",",
                        managedRooms
                            .GroupBy(region => region.RoomType)
                            .OrderBy(group => group.Key)
                            .Select(group =>
                                $"{group.Key}:{group.Count()}"
                            )
                    );

            DebugLiteral(
                "[StillGreenhouses] SERVER CHUNK SCAN " +
                $"chunk={chunkKey.X},{chunkKey.Y},{chunkKey.Z}; " +
                $"dim={chunkKey.Dimension}; " +
                $"complete={discovery.Complete}; " +
                $"discoveryAnchorPositions={discovery.DiscoveryAnchorPositions}; " +
                $"roomRegistryQueries={discovery.RoomRegistryQueries}; " +
                $"coveredRoomPositionsSkipped={discovery.CoveredRoomPositionsSkipped}; " +
                $"anchorRoomInstances={discovery.AnchorRoomInstances}; " +
                $"incompleteAnchorRooms={discovery.IncompleteAnchorRooms}; " +
                $"classifiedManagedRooms={discovery.ClassifiedManagedRooms}; " +
                $"viableManagedRooms={discovery.ViableManagedRooms}; " +
                $"minimumInteriorPositions={config.MinimumManagedRoomInteriorPositions}; " +
                $"skippedTooSmall={discovery.SkippedTooSmallRooms}; " +
                $"roomSizes1To2={discovery.RoomSizes1To2}; " +
                $"roomSizes3To6={discovery.RoomSizes3To6}; " +
                $"roomSizes7To15={discovery.RoomSizes7To15}; " +
                $"roomSizes16To31={discovery.RoomSizes16To31}; " +
                $"roomSizes32Plus={discovery.RoomSizes32Plus}; " +
                $"managedRooms={managedRooms.Count}; " +
                $"roomTypes={roomTypes}"
            );
        }
    }

    private bool IsDiscoveryAnchorBlock(
        Block? block
    )
    {
        if (block?.Code == null)
        {
            return false;
        }

        if (DiscoveryAnchorBlockIdentityCache.TryGetValue(
                block.Id,
                out bool isAnchor
            ))
        {
            return isAnchor;
        }

        isAnchor = StillGreenhousesShared
            .IsRoomDiscoveryAnchorVegetation(
                block
            );

        DiscoveryAnchorBlockIdentityCache.TryAdd(
            block.Id,
            isAnchor
        );

        return isAnchor;
    }

    private void MarkRoomCoveredInChunk(
        Room room,
        ChunkKey chunkKey,
        bool[] covered
    )
    {
        int chunkSize =
            StillGreenhousesShared.ChunkSize;

        int startX = chunkKey.X * chunkSize;
        int startY = chunkKey.Y * chunkSize;
        int startZ = chunkKey.Z * chunkSize;

        int minX = Math.Max(
            room.Location.X1,
            startX
        );

        int maxX = Math.Min(
            room.Location.X2,
            startX + chunkSize - 1
        );

        int minY = Math.Max(
            room.Location.Y1,
            startY
        );

        int maxY = Math.Min(
            room.Location.Y2,
            startY + chunkSize - 1
        );

        int minZ = Math.Max(
            room.Location.Z1,
            startZ
        );

        int maxZ = Math.Min(
            room.Location.Z2,
            startZ + chunkSize - 1
        );

        BlockPos pos = new(
            minX,
            minY,
            minZ,
            chunkKey.Dimension
        );

        for (int y = minY; y <= maxY; y++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    pos.Set(x, y, z);

                    if (!room.Contains(pos))
                    {
                        continue;
                    }

                    int localX = x - startX;
                    int localY = y - startY;
                    int localZ = z - startZ;

                    int index =
                        (
                            localY * chunkSize
                            + localZ
                        )
                        * chunkSize
                        + localX;

                    covered[index] = true;
                }
            }
        }
    }

    // Revalidation/recovery viability first rejects tiny RoomRegistry interiors
    // using the exact PosInRoom occupancy count. Surviving rooms must contain a
    // narrower server discovery anchor; generic tree leaves may still receive
    // client wind inside an already managed room but cannot keep it alive alone.
    private ManagedRoomViability EvaluateManagedRoomViability(
        ICoreServerAPI api,
        GreenhouseRegion room,
        bool countViabilityCheck = true
    )
    {
        if (countViabilityCheck)
        {
            Interlocked.Increment(
                ref roomViabilityChecks
            );
        }

        int minimumPositions =
            config?.MinimumManagedRoomInteriorPositions
            ?? 7;

        if (!room.HasAtLeastOccupiedPositions(
                minimumPositions
            ))
        {
            return ManagedRoomViability.TooSmall;
        }

        foreach (
            BlockPos pos
            in room.GetOccupiedPositions()
        )
        {
            Block block =
                api.World.BlockAccessor.GetBlock(
                    pos
                );

            if (IsDiscoveryAnchorBlock(block))
            {
                return ManagedRoomViability.Viable;
            }
        }

        return ManagedRoomViability.NoDiscoveryAnchor;
    }

    private IEnumerable<ChunkKey> InstallGreenhouse(
        GreenhouseRegion greenhouse
    )
    {
        ClearPendingRoomDisappearance(
            greenhouse.Key,
            reason: "room-installed"
        );

        ClearRecoveredFormerManagedRooms(
            greenhouse
        );

        Greenhouses[
            greenhouse.Key
        ] = greenhouse;

        List<ChunkKey> affected = new();

        foreach (
            ChunkKey chunkKey
            in greenhouse.GetIntersectingChunks()
        )
        {
            ConcurrentDictionary<
                GreenhouseKey,
                byte
            > index =
                ChunkGreenhouseIndex.GetOrAdd(
                    chunkKey,
                    _ => new ConcurrentDictionary<
                        GreenhouseKey,
                        byte
                    >()
                );

            index[greenhouse.Key] = 0;
            affected.Add(chunkKey);
        }

        return affected;
    }

    private void OnDidPlaceBlock(
        IServerPlayer byPlayer,
        int oldblockId,
        BlockSelection blockSel,
        ItemStack withItemStack
    )
    {
        ICoreServerAPI? api = sapi;

        if (api == null)
        {
            return;
        }

        Block newBlock =
            api.World.BlockAccessor
                .GetBlock(blockSel.Position);

        bool isDiscoveryAnchor =
            IsDiscoveryAnchorBlock(newBlock);

        if (
            !isDiscoveryAnchor
            && StillGreenhousesShared
                .IsRoomInteriorPassThroughBlock(newBlock)
        )
        {
            return;
        }

        QueueRevalidationNear(
            blockSel.Position
        );
    }

    private void OnDidBreakBlock(
        IServerPlayer byPlayer,
        int oldblockId,
        BlockSelection blockSel
    )
    {
        ICoreServerAPI? api = sapi;

        if (api == null)
        {
            return;
        }

        Block? oldBlock =
            oldblockId >= 0
            && oldblockId < api.World.Blocks.Count
                ? api.World.Blocks[oldblockId]
                : null;

        bool wasDiscoveryAnchor =
            IsDiscoveryAnchorBlock(oldBlock);

        if (
            !wasDiscoveryAnchor
            && StillGreenhousesShared
                .IsRoomInteriorPassThroughBlock(oldBlock)
        )
        {
            return;
        }

        QueueRevalidationNear(
            blockSel.Position
        );
    }

    private void OnDidUseBlock(
        IServerPlayer byPlayer,
        BlockSelection blockSel
    )
    {
        ICoreServerAPI? api = sapi;

        if (api == null)
        {
            return;
        }

        Block block =
            api.World.BlockAccessor
                .GetBlock(blockSel.Position);

        if (
            StillGreenhousesShared
                .IsRoomInteriorPassThroughBlock(block)
        )
        {
            return;
        }

        QueueRevalidationNear(
            blockSel.Position
        );
    }

    private void OnChunkDirty(
        Vec3i chunkCoord,
        IWorldChunk chunk,
        EnumChunkDirtyReason reason
    )
    {
        if (
            reason == EnumChunkDirtyReason.MarkedDirty
        )
        {
            return;
        }

        ChunkKey loadedChunk =
            ChunkKey.FromInternalChunkCoord(
                chunkCoord
            );

        foreach (
            ChunkKey candidate
            in StillGreenhousesShared.GetNeighborChunks(
                loadedChunk,
                radius: 1
            )
        )
        {
            bool hasIncompleteState =
                ChunkStates.TryGetValue(
                    candidate,
                    out ServerChunkState? candidateState
                )
                && !candidateState.Complete;

            if (hasIncompleteState)
            {
                // A neighboring load can resolve AnyChunkUnloaded evidence,
                // but it must not discard an active scan's accumulated work.
                // Reset the bounded retry history and arrange one delayed
                // recovery only when no scan already owns this chunk.
                ResetIncompleteChunkRetry(candidate);

                if (!IsChunkScanQueuedOrActive(candidate))
                {
                    ScheduleChunkScan(candidate);
                }
                else
                {
                    Interlocked.Increment(
                        ref duplicateChunkScanAdmissionsSuppressed
                    );
                }
            }

            if (
                ChunkGreenhouseIndex.TryGetValue(
                    candidate,
                    out ConcurrentDictionary<
                        GreenhouseKey,
                        byte
                    >? index
                )
            )
            {
                foreach (
                    GreenhouseKey greenhouseKey
                    in index.Keys
                )
                {
                    ScheduleGreenhouseRevalidation(
                        greenhouseKey
                    );
                }
            }
        }
    }

    private void QueueRevalidationNear(
        BlockPos pos
    )
    {
        // A newly closed room may have no positive index yet, and its observed
        // vegetation can be in a neighboring chunk. Probe the changed block and
        // its six neighbors after RoomRegistry has settled instead of relying
        // solely on a same-chunk negative cache entry.
        ScheduleStructuralRoomProbe(pos);

        foreach (
            ChunkKey chunkKey
            in StillGreenhousesShared
                .GetBoundaryAffectedChunks(pos)
        )
        {
            bool completeNegative =
                ChunkStates.TryGetValue(
                    chunkKey,
                    out ServerChunkState? state
                )
                && state.Complete
                && GetGreenhousesForChunk(
                    chunkKey
                ).Count == 0;

            if (completeNegative)
            {
                // Keep the authoritative negative state and its subscribers.
                // That preserves a monotonic revision history and lets the
                // delayed scan push a future positive result without relying
                // on a racing client-side rediscovery request.
                ActiveChunkDiscoveries.TryRemove(
                    chunkKey,
                    out _
                );

                PendingMaintenanceScans.TryRemove(
                    chunkKey,
                    out _
                );

                PendingDiscoveryScans.TryRemove(
                    chunkKey,
                    out _
                );

                ResetIncompleteChunkRetry(chunkKey);

                if (ChunkSubscribers.ContainsKey(chunkKey))
                {
                    // RoomRegistry is rebuilt after the block event. Use the
                    // normal delayed scheduler so this scan observes the new
                    // room topology rather than immediately recaching the old
                    // negative result.
                    ScheduleChunkScan(chunkKey);
                }
            }
        }

        HashSet<GreenhouseKey> formerRoomKeys =
            FindFormerManagedRoomsNear(pos);

        foreach (
            GreenhouseKey formerRoomKey
            in formerRoomKeys
        )
        {
            ScheduleFormerManagedRoomScan(
                formerRoomKey
            );

            if (
                config?.DebugLogging == true
                && FormerManagedRooms.TryGetValue(
                    formerRoomKey,
                    out GreenhouseRegion? formerRoom
                )
            )
            {
                DebugLiteral(
                    "[StillGreenhouses] SERVER FORMER ROOM REVALIDATE " +
                    $"roomDim={formerRoom.Dimension}; " +
                    $"bounds={formerRoom.X1},{formerRoom.Y1},{formerRoom.Z1}.." +
                    $"{formerRoom.X2},{formerRoom.Y2},{formerRoom.Z2}; " +
                    $"roomType={formerRoom.RoomType}; " +
                    $"shape=0x{formerRoom.Key.ShapeHash:X16}; " +
                    $"changedPos={pos.X},{pos.Y},{pos.Z}; " +
                    $"dim={pos.dimension}; " +
                    $"intersectingChunks={formerRoom.GetIntersectingChunks().Count()}"
                );
            }
        }

        HashSet<GreenhouseKey> greenhouseKeys =
            FindGreenhousesNear(pos);

        foreach (
            GreenhouseKey greenhouseKey
            in greenhouseKeys
        )
        {
            ScheduleGreenhouseRevalidation(
                greenhouseKey
            );

            if (
                config?.DebugLogging == true
                && Greenhouses.TryGetValue(
                    greenhouseKey,
                    out GreenhouseRegion? greenhouse
                )
            )
            {
                DebugLiteral(
                    "[StillGreenhouses] SERVER GREENHOUSE REVALIDATE " +
                    $"greenhouseDim={greenhouseKey.Dimension}; " +
                    $"bounds={greenhouseKey.X1},{greenhouseKey.Y1},{greenhouseKey.Z1}.." +
                    $"{greenhouseKey.X2},{greenhouseKey.Y2},{greenhouseKey.Z2}; " +
                    $"roomType={greenhouseKey.RoomType}; " +
                    $"shape=0x{greenhouseKey.ShapeHash:X16}; " +
                    $"changedPos={pos.X},{pos.Y},{pos.Z}; " +
                    $"dim={pos.dimension}; " +
                    $"intersectingChunks={greenhouse.GetIntersectingChunks().Count()}"
                );
            }
        }
    }

    private void ScheduleStructuralRoomProbe(
        BlockPos pos,
        int attemptsRemaining = 2
    )
    {
        ICoreServerAPI? api = sapi;
        StillGreenhousesConfig? currentConfig =
            config;

        if (
            api == null
            || currentConfig?.Enabled != true
        )
        {
            return;
        }

        StructuralRoomProbeKey probeKey =
            StructuralRoomProbeKey.From(pos);

        StructuralRoomProbeBatch batch =
            ScheduledStructuralRoomProbes.GetOrAdd(
                probeKey,
                _ => new StructuralRoomProbeBatch()
            );

        long scheduleVersion =
            batch.Add(
                StructuralRoomProbePosition.From(pos)
            );

        // Coalesce the authoritative work for a wall-building burst into one
        // probe per small spatial cell. The batch retains every changed
        // position, and superseded timers return without querying RoomRegistry.
        // That preserves the actual closing block while keeping the expensive
        // work bounded to the settled batch.

        api.Event.RegisterCallback(
            _elapsedSeconds =>
            {
                if (
                    !ScheduledStructuralRoomProbes
                        .TryGetValue(
                            probeKey,
                            out StructuralRoomProbeBatch?
                                currentBatch
                        )
                    || !ReferenceEquals(
                        currentBatch,
                        batch
                    )
                    || currentBatch.ScheduleVersion
                        != scheduleVersion
                )
                {
                    return;
                }

                ScheduledStructuralRoomProbes.TryRemove(
                    probeKey,
                    out _
                );

                BlockPos[] changedPositions =
                    currentBatch
                        .GetPositions()
                        .Select(position =>
                            position.ToBlockPos()
                        )
                        .ToArray();

                // The ordinary greenhouse timer may have been scheduled by an
                // earlier edit and fired before this final wall change settled
                // in RoomRegistry. Enqueue every still-live room touched by the
                // retained batch now, after the structural debounce, so an
                // opened room cannot remain stuck in its login-time state.
                foreach (BlockPos changedPosition in changedPositions)
                {
                    foreach (
                        GreenhouseKey greenhouseKey
                        in FindGreenhousesNear(changedPosition)
                    )
                    {
                        if (Greenhouses.ContainsKey(greenhouseKey))
                        {
                            PendingGreenhouseRevalidations[
                                greenhouseKey
                            ] = 0;
                        }
                    }
                }

                DiscoverNewManagedRoomsNear(
                    api,
                    changedPositions
                );

                if (
                    attemptsRemaining > 1
                )
                {
                    // Always make one bounded follow-up. A mixed batch can
                    // observe one existing managed room while a newly closed
                    // neighboring room is still absent from RoomRegistry; a
                    // batch-wide success flag must not suppress that retry.
                    foreach (
                        BlockPos changedPosition
                        in changedPositions
                    )
                    {
                        ScheduleStructuralRoomProbe(
                            changedPosition,
                            attemptsRemaining - 1
                        );
                    }
                }
            },
            currentConfig.ServerRescanDelayMs
        );
    }

    private bool DiscoverNewManagedRoomsNear(
        ICoreServerAPI api,
        IReadOnlyCollection<BlockPos> changedPositions
    )
    {
        RoomRegistry? registry =
            roomRegistry;

        if (
            registry == null
            || config?.Enabled != true
        )
        {
            return false;
        }

        HashSet<StructuralRoomProbePosition> probePositions =
            new();

        foreach (BlockPos changedPos in changedPositions)
        {
            int dimension = changedPos.dimension;

            probePositions.Add(
                StructuralRoomProbePosition.From(changedPos)
            );

            probePositions.Add(new(
                changedPos.X - 1,
                changedPos.Y,
                changedPos.Z,
                dimension
            ));

            probePositions.Add(new(
                changedPos.X + 1,
                changedPos.Y,
                changedPos.Z,
                dimension
            ));

            probePositions.Add(new(
                changedPos.X,
                changedPos.Y - 1,
                changedPos.Z,
                dimension
            ));

            probePositions.Add(new(
                changedPos.X,
                changedPos.Y + 1,
                changedPos.Z,
                dimension
            ));

            probePositions.Add(new(
                changedPos.X,
                changedPos.Y,
                changedPos.Z - 1,
                dimension
            ));

            probePositions.Add(new(
                changedPos.X,
                changedPos.Y,
                changedPos.Z + 1,
                dimension
            ));
        }

        HashSet<Room> seenRoomInstances = new(
            ReferenceEqualityComparer.Instance
        );

        HashSet<RoomRevalidationKey> seenRooms = new();

        Dictionary<
            GreenhouseKey,
            GreenhouseRegion
        > newlyDiscoveredRooms = new();

        bool managedRoomObserved = false;

        foreach (
            StructuralRoomProbePosition probePosition
            in probePositions
        )
        {
            BlockPos probe =
                probePosition.ToBlockPos();

            Room? room =
                registry.GetRoomForPosition(probe);

            if (
                room == null
                || !seenRoomInstances.Add(room)
                || room.AnyChunkUnloaded > 0
            )
            {
                continue;
            }

            RoomRevalidationKey roomKey =
                RoomRevalidationKey.From(room);

            if (
                !seenRooms.Add(roomKey)
                || !StillGreenhousesShared
                    .TryClassifyRoom(
                        room,
                        out ManagedRoomType roomType
                    )
            )
            {
                continue;
            }

            GreenhouseRegion discovered =
                GreenhouseRegion.FromRoom(
                    room,
                    probe.dimension,
                    roomType
                );

            if (
                Greenhouses.ContainsKey(
                    discovered.Key
                )
                || HasOverlappingLiveGreenhouse(
                    discovered
                )
            )
            {
                managedRoomObserved = true;
                continue;
            }

            if (
                EvaluateManagedRoomViability(
                    api,
                    discovered
                ) != ManagedRoomViability.Viable
            )
            {
                continue;
            }

            managedRoomObserved = true;

            newlyDiscoveredRooms[
                discovered.Key
            ] = discovered;
        }

        if (newlyDiscoveredRooms.Count == 0)
        {
            return managedRoomObserved;
        }

        HashSet<ChunkKey> affectedChunks = new();

        foreach (
            GreenhouseRegion discovered
            in newlyDiscoveredRooms.Values
        )
        {
            affectedChunks.UnionWith(
                InstallGreenhouse(discovered)
            );
        }

        foreach (ChunkKey chunkKey in affectedChunks)
        {
            bool complete =
                ChunkStates.TryGetValue(
                    chunkKey,
                    out ServerChunkState? state
                )
                && state.Complete;

            SetChunkStateAndPublish(
                chunkKey,
                complete,
                visualTransition: true
            );

            if (!complete)
            {
                ScheduleIncompleteChunkRetry(
                    chunkKey,
                    reason:
                        "structural-room-probe-incomplete-state"
                );
            }
        }

        if (config?.DebugLogging == true)
        {
            DebugLiteral(
                "[StillGreenhouses] SERVER STRUCTURAL ROOM DISCOVERY " +
                $"changedPositions={changedPositions.Count}; " +
                $"probePositions={probePositions.Count}; " +
                $"newRooms={newlyDiscoveredRooms.Count}; " +
                $"affectedChunks={affectedChunks.Count}"
            );
        }

        return true;
    }

    private bool HasOverlappingLiveGreenhouse(
        GreenhouseRegion candidate
    )
    {
        HashSet<GreenhouseKey> checkedKeys = new();

        foreach (
            ChunkKey chunkKey
            in candidate.GetIntersectingChunks()
        )
        {
            if (
                !ChunkGreenhouseIndex.TryGetValue(
                    chunkKey,
                    out ConcurrentDictionary<
                        GreenhouseKey,
                        byte
                    >? index
                )
            )
            {
                continue;
            }

            foreach (GreenhouseKey greenhouseKey in index.Keys)
            {
                if (
                    checkedKeys.Add(greenhouseKey)
                    && Greenhouses.TryGetValue(
                        greenhouseKey,
                        out GreenhouseRegion? existing
                    )
                    && RegionsOverlap(
                        existing,
                        candidate
                    )
                )
                {
                    return true;
                }
            }
        }

        return false;
    }

    private HashSet<GreenhouseKey> FindGreenhousesNear(
        BlockPos pos
    )
    {
        HashSet<GreenhouseKey> results = new();

        foreach (
            ChunkKey chunkKey
            in StillGreenhousesShared
                .GetBoundaryAffectedChunks(pos)
        )
        {
            if (
                !ChunkGreenhouseIndex.TryGetValue(
                    chunkKey,
                    out ConcurrentDictionary<
                        GreenhouseKey,
                        byte
                    >? index
                )
            )
            {
                continue;
            }

            foreach (
                GreenhouseKey greenhouseKey
                in index.Keys
            )
            {
                if (
                    Greenhouses.TryGetValue(
                        greenhouseKey,
                        out GreenhouseRegion? greenhouse
                    )
                    && greenhouse
                        .IsWithinStructuralMargin(
                            pos,
                            margin: 1
                        )
                )
                {
                    results.Add(greenhouseKey);
                }
            }
        }

        return results;
    }

    private HashSet<GreenhouseKey>
        FindFormerManagedRoomsNear(
            BlockPos pos
        )
    {
        HashSet<GreenhouseKey> results = new();

        foreach (
            ChunkKey chunkKey
            in StillGreenhousesShared
                .GetBoundaryAffectedChunks(pos)
        )
        {
            if (
                !FormerManagedRoomChunkIndex.TryGetValue(
                    chunkKey,
                    out ConcurrentDictionary<
                        GreenhouseKey,
                        byte
                    >? index
                )
            )
            {
                continue;
            }

            foreach (
                GreenhouseKey formerRoomKey
                in index.Keys
            )
            {
                if (
                    FormerManagedRooms.TryGetValue(
                        formerRoomKey,
                        out GreenhouseRegion? formerRoom
                    )
                    && formerRoom.IsWithinStructuralMargin(
                        pos,
                        margin: 1
                    )
                )
                {
                    results.Add(
                        formerRoomKey
                    );
                }
            }
        }

        return results;
    }

    private void ScheduleFormerManagedRoomScan(
        GreenhouseKey formerRoomKey
    )
    {
        ICoreServerAPI? api = sapi;
        StillGreenhousesConfig? currentConfig =
            config;

        if (
            api == null
            || currentConfig?.Enabled != true
            || !FormerManagedRooms.ContainsKey(
                formerRoomKey
            )
            || !ScheduledFormerManagedRoomScans.TryAdd(
                formerRoomKey,
                0
            )
        )
        {
            return;
        }

        api.Event.RegisterCallback(
            _elapsedSeconds =>
            {
                ScheduledFormerManagedRoomScans.TryRemove(
                    formerRoomKey,
                    out _
                );

                if (
                    !FormerManagedRooms.TryGetValue(
                        formerRoomKey,
                        out GreenhouseRegion? formerRoom
                    )
                )
                {
                    return;
                }

                HashSet<ChunkKey> scanChunks =
                    formerRoom
                        .GetIntersectingChunks()
                        .ToHashSet();

                int nearbyPlayers = 0;

                foreach (
                    ChunkKey chunkKey
                    in scanChunks
                )
                {
                    nearbyPlayers +=
                        SubscribeNearbyPlayersForChunk(
                            api,
                            chunkKey
                        );
                }

                if (nearbyPlayers <= 0)
                {
                    return;
                }

                foreach (
                    ChunkKey chunkKey
                    in scanChunks
                )
                {
                    ActiveChunkDiscoveries.TryRemove(
                        chunkKey,
                        out _
                    );

                    PendingMaintenanceScans[
                        chunkKey
                    ] = 0;
                }

                if (config?.DebugLogging == true)
                {
                    DebugLiteral(
                        "[StillGreenhouses] SERVER FORMER ROOM RESCAN QUEUED " +
                        $"roomDim={formerRoom.Dimension}; " +
                        $"bounds={formerRoom.X1},{formerRoom.Y1},{formerRoom.Z1}.." +
                        $"{formerRoom.X2},{formerRoom.Y2},{formerRoom.Z2}; " +
                        $"roomType={formerRoom.RoomType}; " +
                        $"shape=0x{formerRoom.Key.ShapeHash:X16}; " +
                        $"scanChunks={scanChunks.Count}; " +
                        $"nearbyPlayerSubscriptions={nearbyPlayers}"
                    );
                }
            },
            currentConfig.ServerRescanDelayMs
        );
    }

    private int SubscribeNearbyPlayersForChunk(
        ICoreServerAPI api,
        ChunkKey chunkKey
    )
    {
        StillGreenhousesConfig? currentConfig =
            config;

        if (currentConfig == null)
        {
            return 0;
        }

        int chunkSize =
            StillGreenhousesShared.ChunkSize;

        Vec3d chunkCenter = new(
            chunkKey.X * chunkSize
                + chunkSize / 2d,
            chunkKey.Y * chunkSize
                + chunkSize / 2d,
            chunkKey.Z * chunkSize
                + chunkSize / 2d
        );

        float horizontalRange =
            currentConfig.ServerSubscriptionRadiusChunks
                * chunkSize
            + chunkSize;

        float verticalRange =
            9 * chunkSize;

        int subscribedPlayers = 0;

        foreach (
            IPlayer candidate
            in api.World.GetPlayersAround(
                chunkCenter,
                horizontalRange,
                verticalRange
            )
        )
        {
            if (
                candidate is not IServerPlayer player
                || player.Entity == null
                || !IsWithinSubscriptionRetentionRadius(
                    player,
                    chunkKey
                )
            )
            {
                continue;
            }

            SubscribePlayer(
                player.PlayerUID,
                chunkKey
            );

            subscribedPlayers++;
        }

        return subscribedPlayers;
    }

    private bool HasNearbyPlayerForFormerRoom(
        ICoreServerAPI api,
        GreenhouseRegion formerRoom
    )
    {
        foreach (
            ChunkKey chunkKey
            in formerRoom.GetIntersectingChunks()
        )
        {
            if (HasNearbyPlayerForChunk(
                    api,
                    chunkKey
                ))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasNearbyPlayerForChunk(
        ICoreServerAPI api,
        ChunkKey chunkKey
    )
    {
        StillGreenhousesConfig? currentConfig =
            config;

        if (currentConfig == null)
        {
            return false;
        }

        int chunkSize =
            StillGreenhousesShared.ChunkSize;

        Vec3d chunkCenter = new(
            chunkKey.X * chunkSize
                + chunkSize / 2d,
            chunkKey.Y * chunkSize
                + chunkSize / 2d,
            chunkKey.Z * chunkSize
                + chunkSize / 2d
        );

        float horizontalRange =
            currentConfig.ServerSubscriptionRadiusChunks
                * chunkSize
            + chunkSize;

        float verticalRange =
            9 * chunkSize;

        foreach (
            IPlayer candidate
            in api.World.GetPlayersAround(
                chunkCenter,
                horizontalRange,
                verticalRange
            )
        )
        {
            if (
                candidate is IServerPlayer player
                && player.Entity != null
                && IsWithinSubscriptionRetentionRadius(
                    player,
                    chunkKey
                )
            )
            {
                return true;
            }
        }

        return false;
    }

    private void ScheduleGreenhouseRevalidation(
        GreenhouseKey greenhouseKey
    )
    {
        ICoreServerAPI? api = sapi;
        StillGreenhousesConfig? currentConfig =
            config;

        if (
            api == null
            || currentConfig?.Enabled != true
            || !Greenhouses.ContainsKey(greenhouseKey)
            || !ScheduledGreenhouseRevalidations.TryAdd(
                greenhouseKey,
                0
            )
        )
        {
            return;
        }

        api.Event.RegisterCallback(
            _elapsedSeconds =>
            {
                ScheduledGreenhouseRevalidations.TryRemove(
                    greenhouseKey,
                    out _
                );

                if (Greenhouses.ContainsKey(greenhouseKey))
                {
                    PendingGreenhouseRevalidations[
                        greenhouseKey
                    ] = 0;
                }
            },
            currentConfig.ServerRescanDelayMs
        );
    }

    private bool ShouldHoldGreenhouseForDisappearanceGrace(
        GreenhouseKey greenhouseKey,
        int oldRegionCount,
        int replacementCount
    )
    {
        if (oldRegionCount <= 0)
        {
            ClearPendingRoomDisappearance(
                greenhouseKey,
                reason: "no-live-region"
            );

            return false;
        }

        if (replacementCount > 0)
        {
            bool hadPendingDisappearance =
                PendingRoomDisappearances.ContainsKey(
                    greenhouseKey
                );

            ICoreServerAPI? replacementApi = sapi;

            if (
                config?.DebugLogging == true
                && hadPendingDisappearance
                && replacementApi != null
                && Greenhouses.TryGetValue(
                    greenhouseKey,
                    out GreenhouseRegion? replacementSeed
                )
            )
            {
                LogSeedRoomAnchorObservations(
                    replacementApi,
                    replacementSeed,
                    stage: "replacement-observed"
                );
            }

            ClearPendingRoomDisappearance(
                greenhouseKey,
                reason: "replacement-observed"
            );

            return false;
        }

        long nowMilliseconds =
            Environment.TickCount64;

        int graceMs =
            config?.ServerRoomDisappearanceGraceMs
            ?? 5000;

        while (true)
        {
            if (
                PendingRoomDisappearances.TryGetValue(
                    greenhouseKey,
                    out PendingRoomDisappearance? pending
                )
            )
            {
                PendingRoomDisappearance updated =
                    pending with
                    {
                        ZeroObservations =
                            pending.ZeroObservations + 1
                    };

                if (
                    !PendingRoomDisappearances.TryUpdate(
                        greenhouseKey,
                        updated,
                        pending
                    )
                )
                {
                    continue;
                }

                Interlocked.Increment(
                    ref roomDisappearanceZeroObservations
                );

                long elapsedMs = Math.Max(
                    0,
                    nowMilliseconds
                    - pending.FirstZeroMilliseconds
                );

                DebugLiteral(
                    "[StillGreenhouses] SERVER GREENHOUSE DISAPPEARANCE STILL PENDING " +
                    $"greenhouseDim={greenhouseKey.Dimension}; " +
                    $"bounds={greenhouseKey.X1},{greenhouseKey.Y1},{greenhouseKey.Z1}.." +
                    $"{greenhouseKey.X2},{greenhouseKey.Y2},{greenhouseKey.Z2}; " +
                    $"roomType={greenhouseKey.RoomType}; " +
                    $"shape=0x{greenhouseKey.ShapeHash:X16}; " +
                    $"elapsedMs={elapsedMs}; " +
                    $"graceMs={graceMs}; " +
                    $"zeroObservations={updated.ZeroObservations}; " +
                    $"oldRegionCount={oldRegionCount}; " +
                    $"replacementCount={replacementCount}"
                );

                return true;
            }

            PendingRoomDisappearance created = new(
                FirstZeroMilliseconds:
                    nowMilliseconds,
                ZeroObservations: 1
            );

            if (
                !PendingRoomDisappearances.TryAdd(
                    greenhouseKey,
                    created
                )
            )
            {
                continue;
            }

            Interlocked.Increment(
                ref roomDisappearanceGraceStarted
            );

            Interlocked.Increment(
                ref roomDisappearanceZeroObservations
            );

            ScheduleDisappearanceGraceCheck(
                greenhouseKey,
                graceMs
            );

            DebugLiteral(
                "[StillGreenhouses] SERVER GREENHOUSE DISAPPEARANCE GRACE STARTED " +
                $"greenhouseDim={greenhouseKey.Dimension}; " +
                $"bounds={greenhouseKey.X1},{greenhouseKey.Y1},{greenhouseKey.Z1}.." +
                $"{greenhouseKey.X2},{greenhouseKey.Y2},{greenhouseKey.Z2}; " +
                $"roomType={greenhouseKey.RoomType}; " +
                $"shape=0x{greenhouseKey.ShapeHash:X16}; " +
                $"elapsedMs=0; " +
                $"graceMs={graceMs}; " +
                $"zeroObservations=1; " +
                $"oldRegionCount={oldRegionCount}; " +
                $"replacementCount={replacementCount}"
            );

            ICoreServerAPI? startApi = sapi;

            if (
                config?.DebugLogging == true
                && startApi != null
                && Greenhouses.TryGetValue(
                    greenhouseKey,
                    out GreenhouseRegion? startSeed
                )
            )
            {
                LogSeedRoomAnchorObservations(
                    startApi,
                    startSeed,
                    stage: "grace-start"
                );
            }

            return true;
        }
    }

    private void ScheduleDisappearanceGraceCheck(
        GreenhouseKey greenhouseKey,
        int delayMs
    )
    {
        ICoreServerAPI? api = sapi;

        if (
            api == null
            || config?.Enabled != true
            || !ScheduledDisappearanceGraceChecks.TryAdd(
                greenhouseKey,
                0
            )
        )
        {
            return;
        }

        api.Event.RegisterCallback(
            _elapsedSeconds =>
            {
                if (
                    !ScheduledDisappearanceGraceChecks
                        .TryRemove(
                            greenhouseKey,
                            out _
                        )
                )
                {
                    return;
                }

                ConfirmPendingRoomDisappearance(
                    api,
                    greenhouseKey
                );
            },
            Math.Max(1, delayMs)
        );
    }

    private SeedRoomAnchorObservationSummary
        LogSeedRoomAnchorObservations(
            ICoreServerAPI api,
            GreenhouseRegion seedGreenhouse,
            string stage
        )
    {
        RoomRegistry? registry =
            roomRegistry;

        if (registry == null)
        {
            return default;
        }

        Interlocked.Increment(
            ref seedRoomRegistryObservationRuns
        );

        Dictionary<Room, int> roomAnchorHits = new();
        Dictionary<string, int> anchorCodeCounts = new(
            StringComparer.Ordinal
        );

        int seedAnchors = 0;
        int anchorsWithRoom = 0;
        int anchorsWithoutRoom = 0;
        int incompleteRoomAnchorHits = 0;
        int classifiedRooms = 0;
        int classifiedOverlappingRooms = 0;

        foreach (
            BlockPos pos
            in seedGreenhouse.GetOccupiedPositions()
        )
        {
            Block block =
                api.World.BlockAccessor.GetBlock(pos);

            if (!IsDiscoveryAnchorBlock(block))
            {
                continue;
            }

            seedAnchors++;

            string blockCode =
                block.Code?.ToString()
                ?? $"block-id-{block.Id}";

            anchorCodeCounts[blockCode] =
                anchorCodeCounts.TryGetValue(
                    blockCode,
                    out int existingCodeCount
                )
                    ? existingCodeCount + 1
                    : 1;

            Room? room =
                registry.GetRoomForPosition(pos);

            if (room == null)
            {
                anchorsWithoutRoom++;
                continue;
            }

            anchorsWithRoom++;

            roomAnchorHits[room] =
                roomAnchorHits.TryGetValue(
                    room,
                    out int existingHits
                )
                    ? existingHits + 1
                    : 1;
        }

        string anchorCodes =
            anchorCodeCounts.Count == 0
                ? "<none>"
                : string.Join(
                    ",",
                    anchorCodeCounts
                        .OrderByDescending(pair => pair.Value)
                        .ThenBy(pair => pair.Key)
                        .Take(12)
                        .Select(pair =>
                            $"{pair.Key}:{pair.Value}"
                        )
                );

        DebugLiteral(
            "[StillGreenhouses] SERVER ROOMREGISTRY SEED OBSERVATION " +
            $"stage={stage}; " +
            $"seedDim={seedGreenhouse.Dimension}; " +
            $"seedBounds={seedGreenhouse.X1},{seedGreenhouse.Y1},{seedGreenhouse.Z1}.." +
            $"{seedGreenhouse.X2},{seedGreenhouse.Y2},{seedGreenhouse.Z2}; " +
            $"seedRoomType={seedGreenhouse.RoomType}; " +
            $"seedShape=0x{seedGreenhouse.Key.ShapeHash:X16}; " +
            $"seedAnchors={seedAnchors}; " +
            $"anchorsWithRoom={anchorsWithRoom}; " +
            $"anchorsWithoutRoom={anchorsWithoutRoom}; " +
            $"distinctRooms={roomAnchorHits.Count}; " +
            $"anchorCodes={anchorCodes}"
        );

        int roomIndex = 0;

        foreach (
            KeyValuePair<Room, int> pair
            in roomAnchorHits
                .OrderByDescending(pair => pair.Value)
        )
        {
            Room room = pair.Key;
            int occupiedPositions =
                StillGreenhousesShared
                    .CountRoomOccupiedPositions(room);

            bool classified =
                StillGreenhousesShared.TryClassifyRoom(
                    room,
                    out ManagedRoomType roomType
                );

            bool overlapsSeed =
                RoomBounds.From(room)
                    .OverlapsWithMargin(
                        seedGreenhouse,
                        margin: 0
                    );

            if (room.AnyChunkUnloaded > 0)
            {
                incompleteRoomAnchorHits += pair.Value;
            }

            if (classified)
            {
                classifiedRooms++;

                if (overlapsSeed)
                {
                    classifiedOverlappingRooms++;
                }
            }

            if (roomIndex < 12)
            {
                DebugLiteral(
                    "[StillGreenhouses] SERVER ROOMREGISTRY SEED ROOM " +
                    $"stage={stage}; " +
                    $"index={roomIndex}; " +
                    $"runtimeIdentity={RuntimeHelpers.GetHashCode(room)}; " +
                    $"bounds={room.Location.X1},{room.Location.Y1},{room.Location.Z1}.." +
                    $"{room.Location.X2},{room.Location.Y2},{room.Location.Z2}; " +
                    $"anchorHits={pair.Value}; " +
                    $"anyChunkUnloaded={room.AnyChunkUnloaded}; " +
                    $"exitCount={room.ExitCount}; " +
                    $"skylightCount={room.SkylightCount}; " +
                    $"nonSkylightCount={room.NonSkylightCount}; " +
                    $"isSmallRoom={room.IsSmallRoom}; " +
                    $"occupiedPositions={occupiedPositions}; " +
                    $"classified={classified}; " +
                    $"roomType={(classified ? roomType.ToString() : "<none>")}; " +
                    $"overlapsSeed={overlapsSeed}"
                );
            }

            roomIndex++;
        }

        return new SeedRoomAnchorObservationSummary(
            SeedAnchors: seedAnchors,
            AnchorsWithRoom: anchorsWithRoom,
            AnchorsWithoutRoom: anchorsWithoutRoom,
            DistinctRooms: roomAnchorHits.Count,
            IncompleteRoomAnchorHits:
                incompleteRoomAnchorHits,
            ClassifiedRooms: classifiedRooms,
            ClassifiedOverlappingRooms:
                classifiedOverlappingRooms
        );
    }

    private void ConfirmPendingRoomDisappearance(
        ICoreServerAPI api,
        GreenhouseKey greenhouseKey
    )
    {
        using ServerPerformanceScope performance =
            new(
                this,
                "room-disappearance-final-check",
                $"greenhouseDim={greenhouseKey.Dimension}; " +
                $"bounds={greenhouseKey.X1},{greenhouseKey.Y1},{greenhouseKey.Z1}.." +
                $"{greenhouseKey.X2},{greenhouseKey.Y2},{greenhouseKey.Z2}; " +
                "thread=server-main"
            );

        if (
            !PendingRoomDisappearances.TryGetValue(
                greenhouseKey,
                out PendingRoomDisappearance? pending
            )
        )
        {
            return;
        }

        if (
            !Greenhouses.TryGetValue(
                greenhouseKey,
                out GreenhouseRegion? seedGreenhouse
            )
        )
        {
            ClearPendingRoomDisappearance(
                greenhouseKey,
                reason: "live-room-missing"
            );

            return;
        }

        int graceMs =
            config?.ServerRoomDisappearanceGraceMs
            ?? 5000;

        int incompleteRetentionMs =
            config?.ServerIncompleteRoomDisappearanceRetentionMs
            ?? 30000;

        long elapsedMs = Math.Max(
            0,
            Environment.TickCount64
            - pending.FirstZeroMilliseconds
        );

        if (elapsedMs < graceMs)
        {
            ScheduleDisappearanceGraceCheck(
                greenhouseKey,
                (int)Math.Max(
                    1,
                    graceMs - elapsedMs
                )
            );

            return;
        }

        ChunkKey[] discoveryChunks =
            GetStructuralMarginChunks(
                seedGreenhouse,
                margin: 1
            )
            .Distinct()
            .ToArray();

        if (
            PendingDisappearanceDiscoveryRefreshes
                .TryAdd(
                    greenhouseKey,
                    0
                )
        )
        {
            foreach (ChunkKey chunkKey in discoveryChunks)
            {
                ActiveChunkDiscoveries.TryRemove(
                    chunkKey,
                    out _
                );

                // Clear any pre-refresh result. Confirmation below requires a
                // replacement entry written by the scan just queued here.
                LatestChunkDiscoveryResults.TryRemove(
                    chunkKey,
                    out _
                );

                PendingMaintenanceScans[
                    chunkKey
                ] = 0;
            }

            ScheduleDisappearanceGraceCheck(
                greenhouseKey,
                250
            );

            return;
        }

        ChunkKey[] missingDiscoveryResults =
            discoveryChunks
                .Where(chunkKey =>
                    !LatestChunkDiscoveryResults
                        .ContainsKey(chunkKey)
                )
                .ToArray();

        bool discoveryRefreshPending =
            missingDiscoveryResults.Length > 0
            || discoveryChunks.Any(chunkKey =>
                ActiveChunkDiscoveries.ContainsKey(
                    chunkKey
                )
                || PendingMaintenanceScans.ContainsKey(
                    chunkKey
                )
                || PendingDiscoveryScans.ContainsKey(
                    chunkKey
                )
            );

        if (discoveryRefreshPending)
        {
            // A dropped/unavailable work item must not leave the disappearance
            // confirmation waiting forever. Requeue only chunks that still
            // lack a post-refresh result and are not already in flight.
            foreach (ChunkKey chunkKey in missingDiscoveryResults)
            {
                if (
                    !ActiveChunkDiscoveries.ContainsKey(chunkKey)
                    && !PendingMaintenanceScans.ContainsKey(
                        chunkKey
                    )
                    && !PendingDiscoveryScans.ContainsKey(
                        chunkKey
                    )
                )
                {
                    PendingMaintenanceScans[
                        chunkKey
                    ] = 0;
                }
            }

            ScheduleDisappearanceGraceCheck(
                greenhouseKey,
                250
            );

            return;
        }

        PendingDisappearanceDiscoveryRefreshes.TryRemove(
            greenhouseKey,
            out _
        );

        ChunkDiscoveryResult[] discoveries =
            discoveryChunks
                .Select(chunkKey =>
                    LatestChunkDiscoveryResults[
                        chunkKey
                    ]
                )
                .ToArray();

        int incompleteDiscoveryChunks =
            discoveries.Count(result =>
                !result.Loaded
                || !result.Complete
            );

        SeedRoomAnchorObservationSummary seedObservation =
            incompleteDiscoveryChunks > 0
            || config?.DebugLogging == true
                ? LogSeedRoomAnchorObservations(
                    api,
                    seedGreenhouse,
                    stage:
                        elapsedMs < incompleteRetentionMs
                            ? "grace-final"
                            : "incomplete-retention-recheck"
                )
                : default;

        GreenhouseRegion[] replacements =
            discoveries
                .SelectMany(result =>
                    result.ManagedRooms.Values
                )
                .Where(region =>
                    RegionsOverlap(
                        seedGreenhouse,
                        region
                    )
                )
                .GroupBy(region => region.Key)
                .Select(group => group.First())
                .ToArray();

        if (replacements.Length > 0)
        {
            ClearPendingRoomDisappearance(
                greenhouseKey,
                reason: "final-anchor-rediscovery"
            );

            ReconcileFinalDisappearanceRediscovery(
                seedGreenhouse,
                replacements
            );

            foreach (
                ChunkDiscoveryResult discovery
                in discoveries
            )
            {
                if (discovery.Complete)
                {
                    ResetIncompleteChunkRetry(
                        discovery.ChunkKey
                    );
                }
                else
                {
                    SetChunkStateAndPublish(
                        discovery.ChunkKey,
                        complete: false
                    );

                    ScheduleIncompleteChunkRetry(
                        discovery.ChunkKey,
                        reason:
                            "disappearance-final-check-incomplete"
                    );
                }
            }

            return;
        }

        // A chunk-wide discovery can be incomplete because an unrelated room
        // crosses an unloaded boundary. The old managed room is nevertheless
        // authoritatively gone when every structural chunk is loaded and its
        // own live discovery anchors resolve only to complete, non-managed
        // RoomRegistry rooms (or no room). Do not let unrelated incomplete
        // rooms retain a stale positive forever.
        bool seedAuthoritativelyNotManaged =
            discoveries.All(result => result.Loaded)
            && seedObservation.SeedAnchors > 0
            && seedObservation.IncompleteRoomAnchorHits == 0
            && seedObservation.ClassifiedOverlappingRooms == 0;

        if (
            incompleteDiscoveryChunks > 0
            && !seedAuthoritativelyNotManaged
        )
        {
            Interlocked.Increment(
                ref roomDisappearanceFinalChecksIncomplete
            );

            foreach (
                ChunkDiscoveryResult discovery
                in discoveries.Where(result =>
                    !result.Loaded
                    || !result.Complete
                )
            )
            {
                SetChunkStateAndPublish(
                    discovery.ChunkKey,
                    complete: false
                );

                ScheduleIncompleteChunkRetry(
                    discovery.ChunkKey,
                    reason:
                        "disappearance-final-check-incomplete"
                );
            }

            int nextCheckDelayMs;
            string retentionState;

            if (elapsedMs < incompleteRetentionMs)
            {
                Interlocked.Increment(
                    ref roomDisappearanceIncompletePositiveRetained
                );

                nextCheckDelayMs =
                    (int)Math.Max(
                        1,
                        incompleteRetentionMs - elapsedMs
                    );

                retentionState =
                    "initial-retention";

                DebugLiteral(
                    "[StillGreenhouses] SERVER GREENHOUSE DISAPPEARANCE INCOMPLETE POSITIVE RETAINED " +
                    $"greenhouseDim={greenhouseKey.Dimension}; " +
                    $"bounds={greenhouseKey.X1},{greenhouseKey.Y1},{greenhouseKey.Z1}.." +
                    $"{greenhouseKey.X2},{greenhouseKey.Y2},{greenhouseKey.Z2}; " +
                    $"roomType={greenhouseKey.RoomType}; " +
                    $"shape=0x{greenhouseKey.ShapeHash:X16}; " +
                    $"elapsedMs={elapsedMs}; " +
                    $"graceMs={graceMs}; " +
                    $"retentionMs={incompleteRetentionMs}; " +
                    $"zeroObservations={pending.ZeroObservations}; " +
                    $"discoveryChunks={discoveries.Length}; " +
                    $"incompleteDiscoveryChunks={incompleteDiscoveryChunks}; " +
                    $"seedAnchors={seedObservation.SeedAnchors}; " +
                    $"anchorsWithRoom={seedObservation.AnchorsWithRoom}; " +
                    $"anchorsWithoutRoom={seedObservation.AnchorsWithoutRoom}; " +
                    $"distinctSeedRooms={seedObservation.DistinctRooms}; " +
                    $"incompleteRoomAnchorHits={seedObservation.IncompleteRoomAnchorHits}; " +
                    $"classifiedSeedRooms={seedObservation.ClassifiedRooms}; " +
                    $"classifiedOverlappingSeedRooms={seedObservation.ClassifiedOverlappingRooms}; " +
                    $"nextCheckDelayMs={nextCheckDelayMs}"
                );
            }
            else
            {
                Interlocked.Increment(
                    ref roomDisappearanceIncompleteRetentionExtended
                );

                nextCheckDelayMs =
                    incompleteRetentionMs;

                retentionState =
                    "retention-extended";

                DebugLiteral(
                    "[StillGreenhouses] SERVER GREENHOUSE DISAPPEARANCE INCOMPLETE POSITIVE RETENTION EXTENDED " +
                    $"greenhouseDim={greenhouseKey.Dimension}; " +
                    $"bounds={greenhouseKey.X1},{greenhouseKey.Y1},{greenhouseKey.Z1}.." +
                    $"{greenhouseKey.X2},{greenhouseKey.Y2},{greenhouseKey.Z2}; " +
                    $"roomType={greenhouseKey.RoomType}; " +
                    $"shape=0x{greenhouseKey.ShapeHash:X16}; " +
                    $"elapsedMs={elapsedMs}; " +
                    $"graceMs={graceMs}; " +
                    $"retentionMs={incompleteRetentionMs}; " +
                    $"zeroObservations={pending.ZeroObservations}; " +
                    $"discoveryChunks={discoveries.Length}; " +
                    $"incompleteDiscoveryChunks={incompleteDiscoveryChunks}; " +
                    $"seedAnchors={seedObservation.SeedAnchors}; " +
                    $"anchorsWithRoom={seedObservation.AnchorsWithRoom}; " +
                    $"anchorsWithoutRoom={seedObservation.AnchorsWithoutRoom}; " +
                    $"distinctSeedRooms={seedObservation.DistinctRooms}; " +
                    $"incompleteRoomAnchorHits={seedObservation.IncompleteRoomAnchorHits}; " +
                    $"classifiedSeedRooms={seedObservation.ClassifiedRooms}; " +
                    $"classifiedOverlappingSeedRooms={seedObservation.ClassifiedOverlappingRooms}; " +
                    $"nextCheckDelayMs={nextCheckDelayMs}"
                );
            }

            DebugLiteral(
                "[StillGreenhouses] SERVER GREENHOUSE DISAPPEARANCE FINAL CHECK INCOMPLETE " +
                $"greenhouseDim={greenhouseKey.Dimension}; " +
                $"bounds={greenhouseKey.X1},{greenhouseKey.Y1},{greenhouseKey.Z1}.." +
                $"{greenhouseKey.X2},{greenhouseKey.Y2},{greenhouseKey.Z2}; " +
                $"roomType={greenhouseKey.RoomType}; " +
                $"shape=0x{greenhouseKey.ShapeHash:X16}; " +
                $"elapsedMs={elapsedMs}; " +
                $"retentionState={retentionState}; " +
                $"discoveryChunks={discoveries.Length}; " +
                $"incompleteDiscoveryChunks={incompleteDiscoveryChunks}; " +
                $"allIncompleteRooms={discoveries.Sum(result => result.IncompleteRoomBounds.Count)}"
            );

            ScheduleDisappearanceGraceCheck(
                greenhouseKey,
                nextCheckDelayMs
            );

            return;
        }

        if (
            !PendingRoomDisappearances.TryRemove(
                greenhouseKey,
                out PendingRoomDisappearance? confirmed
            )
        )
        {
            return;
        }

        ScheduledDisappearanceGraceChecks.TryRemove(
            greenhouseKey,
            out _
        );

        Interlocked.Increment(
            ref roomDisappearanceGraceConfirmed
        );

        DebugLiteral(
            "[StillGreenhouses] SERVER GREENHOUSE DISAPPEARANCE GRACE CONFIRMED " +
            $"greenhouseDim={greenhouseKey.Dimension}; " +
            $"bounds={greenhouseKey.X1},{greenhouseKey.Y1},{greenhouseKey.Z1}.." +
            $"{greenhouseKey.X2},{greenhouseKey.Y2},{greenhouseKey.Z2}; " +
            $"roomType={greenhouseKey.RoomType}; " +
            $"shape=0x{greenhouseKey.ShapeHash:X16}; " +
            $"elapsedMs={Math.Max(0, Environment.TickCount64 - confirmed.FirstZeroMilliseconds)}; " +
            $"graceMs={graceMs}; " +
            $"zeroObservations={confirmed.ZeroObservations}; " +
            $"finalRediscoveredRooms=0; " +
            $"discoveryComplete={incompleteDiscoveryChunks == 0}; " +
            $"seedAuthoritativelyNotManaged={seedAuthoritativelyNotManaged}; " +
            $"seedAnchors={seedObservation.SeedAnchors}; " +
            $"anchorsWithRoom={seedObservation.AnchorsWithRoom}; " +
            $"distinctSeedRooms={seedObservation.DistinctRooms}"
        );

        CommitGreenhouseReplacement(
            new[] { seedGreenhouse },
            Array.Empty<GreenhouseRegion>()
        );
    }

    private void ReconcileFinalDisappearanceRediscovery(
        GreenhouseRegion seedGreenhouse,
        IReadOnlyCollection<GreenhouseRegion> replacements
    )
    {
        GreenhouseRegion[] replacementArray =
            replacements
                .GroupBy(region => region.Key)
                .Select(group => group.First())
                .ToArray();

        GreenhouseRegion[] oldRooms =
            Greenhouses.Values
                .Where(liveRoom =>
                    liveRoom.Key == seedGreenhouse.Key
                    || replacementArray.Any(replacement =>
                        RegionsOverlap(
                            liveRoom,
                            replacement
                        )
                    )
                )
                .GroupBy(region => region.Key)
                .Select(group => group.First())
                .ToArray();

        HashSet<GreenhouseKey> oldSet =
            oldRooms
                .Select(region => region.Key)
                .ToHashSet();

        HashSet<GreenhouseKey> newSet =
            replacementArray
                .Select(region => region.Key)
                .ToHashSet();

        if (oldSet.SetEquals(newSet))
        {
            DebugLiteral(
                "[StillGreenhouses] SERVER GREENHOUSE DISAPPEARANCE REDISCOVERY UNCHANGED " +
                $"seedDim={seedGreenhouse.Dimension}; " +
                $"seedBounds={seedGreenhouse.X1},{seedGreenhouse.Y1},{seedGreenhouse.Z1}.." +
                $"{seedGreenhouse.X2},{seedGreenhouse.Y2},{seedGreenhouse.Z2}; " +
                $"seedRoomType={seedGreenhouse.RoomType}; " +
                $"seedShape=0x{seedGreenhouse.Key.ShapeHash:X16}; " +
                $"regionCount={newSet.Count}"
            );

            return;
        }

        CommitGreenhouseReplacement(
            oldRooms,
            replacementArray
        );
    }

    private void ClearPendingRoomDisappearance(
        GreenhouseKey greenhouseKey,
        string reason
    )
    {
        PendingDisappearanceDiscoveryRefreshes.TryRemove(
            greenhouseKey,
            out _
        );

        if (
            !PendingRoomDisappearances.TryRemove(
                greenhouseKey,
                out PendingRoomDisappearance? pending
            )
        )
        {
            return;
        }

        ScheduledDisappearanceGraceChecks.TryRemove(
            greenhouseKey,
            out _
        );

        Interlocked.Increment(
            ref roomDisappearanceGraceCleared
        );

        DebugLiteral(
            "[StillGreenhouses] SERVER GREENHOUSE DISAPPEARANCE GRACE CLEARED " +
            $"greenhouseDim={greenhouseKey.Dimension}; " +
            $"bounds={greenhouseKey.X1},{greenhouseKey.Y1},{greenhouseKey.Z1}.." +
            $"{greenhouseKey.X2},{greenhouseKey.Y2},{greenhouseKey.Z2}; " +
            $"roomType={greenhouseKey.RoomType}; " +
            $"shape=0x{greenhouseKey.ShapeHash:X16}; " +
            $"elapsedMs={Math.Max(0, Environment.TickCount64 - pending.FirstZeroMilliseconds)}; " +
            $"graceMs={config?.ServerRoomDisappearanceGraceMs ?? 5000}; " +
            $"zeroObservations={pending.ZeroObservations}; " +
            $"reason={reason}"
        );
    }

    private void RevalidateGreenhouse(
        ICoreServerAPI api,
        GreenhouseKey greenhouseKey
    )
    {
        RoomRegistry? registry =
            roomRegistry;

        if (
            registry == null
            || !Greenhouses.TryGetValue(
                greenhouseKey,
                out GreenhouseRegion? seedGreenhouse
            )
        )
        {
            return;
        }

        HashSet<Room> seenRoomInstances = new(
            ReferenceEqualityComparer.Instance
        );

        HashSet<RoomRevalidationKey> seenRooms = new();
        List<Room> observedRooms = new();
        Dictionary<
            GreenhouseKey,
            GreenhouseRegion
        > replacementGreenhouses = new();

        foreach (
            BlockPos probe
            in seedGreenhouse.GetOccupiedPositions()
        )
        {
            Room? room =
                registry.GetRoomForPosition(probe);

            if (room == null)
            {
                // An opened room legitimately resolves to no RoomRegistry
                // entry. Treat that as no replacement for this probe and let
                // the existing disappearance grace/final rediscovery path
                // distinguish a real removal from a transient registry gap.
                continue;
            }

            if (!seenRoomInstances.Add(room))
            {
                continue;
            }

            RoomRevalidationKey roomKey =
                RoomRevalidationKey.From(room);

            if (!seenRooms.Add(roomKey))
            {
                continue;
            }

            if (room.AnyChunkUnloaded > 0)
            {
                ScheduleGreenhouseRevalidation(
                    greenhouseKey
                );

                return;
            }

            observedRooms.Add(room);

            if (
                !StillGreenhousesShared
                    .TryClassifyRoom(
                        room,
                        out ManagedRoomType roomType
                    )
            )
            {
                continue;
            }

            Interlocked.Increment(
                ref roomViabilityChecks
            );

            int occupiedPositionCount =
                StillGreenhousesShared
                    .CountRoomOccupiedPositions(
                        room
                    );

            if (
                occupiedPositionCount
                < (
                    config
                        ?.MinimumManagedRoomInteriorPositions
                    ?? 7
                )
            )
            {
                Interlocked.Increment(
                    ref roomsSkippedTooSmall
                );

                continue;
            }

            GreenhouseRegion replacement =
                GreenhouseRegion.FromRoom(
                    room,
                    greenhouseKey.Dimension,
                    roomType
                );

            ManagedRoomViability viability =
                EvaluateManagedRoomViability(
                    api,
                    replacement,
                    countViabilityCheck: false
                );

            if (
                viability
                    == ManagedRoomViability.NoDiscoveryAnchor
            )
            {
                Interlocked.Increment(
                    ref roomsSkippedWithoutDiscoveryAnchor
                );

                continue;
            }

            replacementGreenhouses[
                replacement.Key
            ] = replacement;
        }

        Dictionary<
            GreenhouseKey,
            GreenhouseRegion
        > oldGreenhouses =
            FindGreenhousesConnectedToRooms(
                seedGreenhouse,
                observedRooms
            );

        if (ShouldHoldGreenhouseForDisappearanceGrace(
                greenhouseKey,
                oldGreenhouses.Count,
                replacementGreenhouses.Count
            ))
        {
            return;
        }

        HashSet<GreenhouseKey> oldSet =
            oldGreenhouses.Keys.ToHashSet();

        HashSet<GreenhouseKey> newSet =
            replacementGreenhouses.Keys.ToHashSet();

        if (oldSet.SetEquals(newSet))
        {
            if (config?.DebugLogging == true)
            {
                DebugLiteral(
                    "[StillGreenhouses] SERVER GREENHOUSE UNCHANGED " +
                    $"seedDim={greenhouseKey.Dimension}; " +
                    $"seedBounds={greenhouseKey.X1},{greenhouseKey.Y1},{greenhouseKey.Z1}.." +
                    $"{greenhouseKey.X2},{greenhouseKey.Y2},{greenhouseKey.Z2}; " +
                    $"seedRoomType={greenhouseKey.RoomType}; " +
                    $"seedShape=0x{greenhouseKey.ShapeHash:X16}; " +
                    $"regionCount={oldSet.Count}"
                );
            }

            return;
        }

        CommitGreenhouseReplacement(
            oldGreenhouses.Values,
            replacementGreenhouses.Values
        );
    }

    private Dictionary<
        GreenhouseKey,
        GreenhouseRegion
    > FindGreenhousesConnectedToRooms(
        GreenhouseRegion seedGreenhouse,
        IReadOnlyList<Room> observedRooms
    )
    {
        Dictionary<
            GreenhouseKey,
            GreenhouseRegion
        > connected = new()
        {
            [seedGreenhouse.Key] =
                seedGreenhouse
        };

        if (observedRooms.Count == 0)
        {
            return connected;
        }

        foreach (
            KeyValuePair<
                GreenhouseKey,
                GreenhouseRegion
            > entry
            in Greenhouses
        )
        {
            if (
                entry.Key == seedGreenhouse.Key
                || entry.Value.Dimension
                    != seedGreenhouse.Dimension
            )
            {
                continue;
            }

            if (IsRegionConnectedToRooms(
                    entry.Value,
                    observedRooms
                ))
            {
                connected[
                    entry.Key
                ] = entry.Value;
            }
        }

        return connected;
    }

    private static bool IsRegionConnectedToRooms(
        GreenhouseRegion greenhouse,
        IReadOnlyList<Room> rooms
    )
    {
        foreach (
            BlockPos pos
            in greenhouse.GetOccupiedPositions()
        )
        {
            foreach (Room room in rooms)
            {
                if (room.Contains(pos))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void CommitGreenhouseReplacement(
        IEnumerable<GreenhouseRegion> oldGreenhouses,
        IEnumerable<GreenhouseRegion> replacements
    )
    {
        GreenhouseRegion[] oldArray =
            oldGreenhouses
                .GroupBy(region => region.Key)
                .Select(group => group.First())
                .ToArray();

        GreenhouseRegion[] replacementArray =
            replacements
                .GroupBy(region => region.Key)
                .Select(group => group.First())
                .ToArray();

        HashSet<ChunkKey> visualTransitionChunks =
            FindVegetationMembershipTransitionChunks(
                oldArray,
                replacementArray
            );

        GreenhouseRegion[] disappearedRooms =
            oldArray
                .Where(oldRoom =>
                    !replacementArray.Any(
                        replacement =>
                            RegionsOverlap(
                                oldRoom,
                                replacement
                            )
                    )
                )
                .ToArray();

        HashSet<ChunkKey> affectedChunks = new();

        foreach (
            GreenhouseRegion oldGreenhouse
            in oldArray
        )
        {
            affectedChunks.UnionWith(
                oldGreenhouse.GetIntersectingChunks()
            );
        }

        foreach (
            GreenhouseRegion replacement
            in replacementArray
        )
        {
            affectedChunks.UnionWith(
                replacement.GetIntersectingChunks()
            );
        }

        foreach (
            GreenhouseRegion oldGreenhouse
            in oldArray
        )
        {
            RemoveGreenhouseFromLiveIndex(
                oldGreenhouse
            );
        }

        foreach (
            GreenhouseRegion replacement
            in replacementArray
        )
        {
            foreach (
                ChunkKey chunkKey
                in InstallGreenhouse(replacement)
            )
            {
                affectedChunks.Add(chunkKey);
            }
        }

        foreach (
            GreenhouseRegion disappearedRoom
            in disappearedRooms
        )
        {
            RememberFormerManagedRoom(
                disappearedRoom
            );
        }

        foreach (
            ChunkKey chunkKey
            in affectedChunks
        )
        {
            bool complete =
                ChunkStates.TryGetValue(
                    chunkKey,
                    out ServerChunkState? state
                )
                && state.Complete;

            SetChunkStateAndPublish(
                chunkKey,
                complete,
                visualTransition:
                    visualTransitionChunks.Contains(
                        chunkKey
                    )
            );

            if (!complete)
            {
                ScheduleIncompleteChunkRetry(
                    chunkKey,
                    reason: "room-transition-incomplete-state"
                );
            }
        }

        if (config?.DebugLogging == true)
        {
            DebugLiteral(
                "[StillGreenhouses] SERVER GREENHOUSE TRANSITION " +
                $"oldRegionCount={oldArray.Length}; " +
                $"replacementCount={replacementArray.Length}; " +
                $"disappearedRoomCount={disappearedRooms.Length}; " +
                $"affectedChunks={affectedChunks.Count}; " +
                $"visualTransitionChunks={visualTransitionChunks.Count}"
            );
        }
    }

    private HashSet<ChunkKey>
        FindVegetationMembershipTransitionChunks(
            IReadOnlyList<GreenhouseRegion> oldGreenhouses,
            IReadOnlyList<GreenhouseRegion> newGreenhouses
        )
    {
        ICoreServerAPI? api = sapi;

        HashSet<ChunkKey> changedChunks = new();

        if (api == null)
        {
            return changedChunks;
        }

        HashSet<GreenhouseMembershipPos> oldMembership =
            BuildGreenhouseMembershipSet(
                oldGreenhouses
            );

        HashSet<GreenhouseMembershipPos> changedPositions =
            BuildGreenhouseMembershipSet(
                newGreenhouses
            );

        changedPositions.SymmetricExceptWith(
            oldMembership
        );

        foreach (
            GreenhouseMembershipPos position
            in changedPositions
        )
        {
            BlockPos blockPos =
                position.ToBlockPos();

            Block block =
                api.World.BlockAccessor
                    .GetBlock(blockPos);

            if (
                !StillGreenhousesShared
                    .IsVegetationCandidate(block)
            )
            {
                continue;
            }

            changedChunks.Add(
                ChunkKey.From(blockPos)
            );
        }

        return changedChunks;
    }

    private static HashSet<GreenhouseMembershipPos>
        BuildGreenhouseMembershipSet(
            IEnumerable<GreenhouseRegion> greenhouses
        )
    {
        HashSet<GreenhouseMembershipPos> result =
            new();

        foreach (
            GreenhouseRegion greenhouse
            in greenhouses
        )
        {
            foreach (
                BlockPos pos
                in greenhouse.GetOccupiedPositions()
            )
            {
                result.Add(
                    GreenhouseMembershipPos.From(pos)
                );
            }
        }

        return result;
    }

    private static bool RegionsOverlap(
        GreenhouseRegion first,
        GreenhouseRegion second
    )
    {
        if (
            first.Dimension != second.Dimension
            || first.X2 < second.X1
            || first.X1 > second.X2
            || first.Y2 < second.Y1
            || first.Y1 > second.Y2
            || first.Z2 < second.Z1
            || first.Z1 > second.Z2
        )
        {
            return false;
        }

        foreach (
            BlockPos pos
            in first.GetOccupiedPositions()
        )
        {
            if (second.Contains(pos))
            {
                return true;
            }
        }

        return false;
    }

    private void RememberFormerManagedRoom(
        GreenhouseRegion formerRoom
    )
    {
        ICoreServerAPI? api = sapi;

        if (api == null)
        {
            return;
        }

        ManagedRoomViability viability =
            EvaluateManagedRoomViability(
                api,
                formerRoom
            );

        if (viability != ManagedRoomViability.Viable)
        {
            string reason;

            if (viability == ManagedRoomViability.TooSmall)
            {
                Interlocked.Increment(
                    ref formerRoomsSkippedTooSmall
                );

                reason =
                    $"too-small-{formerRoom.OccupiedPositionCount}-of-" +
                    $"{config?.MinimumManagedRoomInteriorPositions ?? 7}";
            }
            else
            {
                Interlocked.Increment(
                    ref formerRoomsSkippedWithoutDiscoveryAnchor
                );

                reason = "no-discovery-anchor";
            }

            if (config?.DebugLogging == true)
            {
                DebugLiteral(
                    "[StillGreenhouses] SERVER FORMER ROOM NOT TRACKED " +
                    $"roomDim={formerRoom.Dimension}; " +
                    $"bounds={formerRoom.X1},{formerRoom.Y1},{formerRoom.Z1}.." +
                    $"{formerRoom.X2},{formerRoom.Y2},{formerRoom.Z2}; " +
                    $"roomType={formerRoom.RoomType}; " +
                    $"shape=0x{formerRoom.Key.ShapeHash:X16}; " +
                    $"occupiedPositions={formerRoom.OccupiedPositionCount}; " +
                    $"reason={reason}"
                );
            }

            return;
        }

        FormerManagedRooms[
            formerRoom.Key
        ] = formerRoom;

        foreach (
            ChunkKey chunkKey
            in GetStructuralMarginChunks(
                formerRoom,
                margin: 1
            )
        )
        {
            FormerManagedRoomChunkIndex
                .GetOrAdd(
                    chunkKey,
                    _ => new ConcurrentDictionary<
                        GreenhouseKey,
                        byte
                    >()
                )[formerRoom.Key] = 0;
        }

        if (config?.DebugLogging == true)
        {
            DebugLiteral(
                "[StillGreenhouses] SERVER FORMER ROOM TRACKED " +
                $"roomDim={formerRoom.Dimension}; " +
                $"bounds={formerRoom.X1},{formerRoom.Y1},{formerRoom.Z1}.." +
                $"{formerRoom.X2},{formerRoom.Y2},{formerRoom.Z2}; " +
                $"roomType={formerRoom.RoomType}; " +
                $"shape=0x{formerRoom.Key.ShapeHash:X16}"
            );
        }
    }

    private void RemoveFormerManagedRoom(
        GreenhouseKey formerRoomKey,
        string reason
    )
    {
        if (
            !FormerManagedRooms.TryRemove(
                formerRoomKey,
                out GreenhouseRegion? formerRoom
            )
        )
        {
            return;
        }

        ScheduledFormerManagedRoomScans.TryRemove(
            formerRoomKey,
            out _
        );

        foreach (
            ChunkKey chunkKey
            in GetStructuralMarginChunks(
                formerRoom,
                margin: 1
            )
        )
        {
            if (
                !FormerManagedRoomChunkIndex.TryGetValue(
                    chunkKey,
                    out ConcurrentDictionary<
                        GreenhouseKey,
                        byte
                    >? index
                )
            )
            {
                continue;
            }

            index.TryRemove(
                formerRoomKey,
                out _
            );

            if (index.IsEmpty)
            {
                FormerManagedRoomChunkIndex.TryRemove(
                    chunkKey,
                    out _
                );
            }
        }

        if (config?.DebugLogging == true)
        {
            DebugLiteral(
                "[StillGreenhouses] SERVER FORMER ROOM REMOVED " +
                $"roomDim={formerRoom.Dimension}; " +
                $"bounds={formerRoom.X1},{formerRoom.Y1},{formerRoom.Z1}.." +
                $"{formerRoom.X2},{formerRoom.Y2},{formerRoom.Z2}; " +
                $"roomType={formerRoom.RoomType}; " +
                $"shape=0x{formerRoom.Key.ShapeHash:X16}; " +
                $"reason={reason}"
            );
        }
    }

    private void ClearRecoveredFormerManagedRooms(
        GreenhouseRegion liveRoom
    )
    {
        HashSet<GreenhouseKey> candidates = new();

        foreach (
            ChunkKey chunkKey
            in liveRoom.GetIntersectingChunks()
        )
        {
            if (
                FormerManagedRoomChunkIndex.TryGetValue(
                    chunkKey,
                    out ConcurrentDictionary<
                        GreenhouseKey,
                        byte
                    >? index
                )
            )
            {
                candidates.UnionWith(
                    index.Keys
                );
            }
        }

        foreach (
            GreenhouseKey formerRoomKey
            in candidates
        )
        {
            if (
                FormerManagedRooms.TryGetValue(
                    formerRoomKey,
                    out GreenhouseRegion? formerRoom
                )
                && RegionsOverlap(
                    formerRoom,
                    liveRoom
                )
            )
            {
                RemoveFormerManagedRoom(
                    formerRoomKey,
                    reason: "overlapping-managed-room-restored"
                );
            }
        }
    }

    private static IEnumerable<ChunkKey>
        GetStructuralMarginChunks(
            GreenhouseRegion room,
            int margin
        )
    {
        int chunkSize =
            StillGreenhousesShared.ChunkSize;

        int minChunkX =
            StillGreenhousesShared.FloorDiv(
                room.X1 - margin,
                chunkSize
            );

        int maxChunkX =
            StillGreenhousesShared.FloorDiv(
                room.X2 + margin,
                chunkSize
            );

        int minChunkY =
            StillGreenhousesShared.FloorDiv(
                room.Y1 - margin,
                chunkSize
            );

        int maxChunkY =
            StillGreenhousesShared.FloorDiv(
                room.Y2 + margin,
                chunkSize
            );

        int minChunkZ =
            StillGreenhousesShared.FloorDiv(
                room.Z1 - margin,
                chunkSize
            );

        int maxChunkZ =
            StillGreenhousesShared.FloorDiv(
                room.Z2 + margin,
                chunkSize
            );

        for (
            int chunkY = minChunkY;
            chunkY <= maxChunkY;
            chunkY++
        )
        {
            for (
                int chunkZ = minChunkZ;
                chunkZ <= maxChunkZ;
                chunkZ++
            )
            {
                for (
                    int chunkX = minChunkX;
                    chunkX <= maxChunkX;
                    chunkX++
                )
                {
                    yield return new ChunkKey(
                        chunkX,
                        chunkY,
                        chunkZ,
                        room.Dimension
                    );
                }
            }
        }
    }

    private void RemoveGreenhouseFromLiveIndex(
        GreenhouseRegion greenhouse
    )
    {
        Greenhouses.TryRemove(
            greenhouse.Key,
            out _
        );

        PendingGreenhouseRevalidations.TryRemove(
            greenhouse.Key,
            out _
        );

        ScheduledGreenhouseRevalidations.TryRemove(
            greenhouse.Key,
            out _
        );

        ClearPendingRoomDisappearance(
            greenhouse.Key,
            reason: "live-room-removed"
        );

        foreach (
            ChunkKey chunkKey
            in greenhouse.GetIntersectingChunks()
        )
        {
            if (
                !ChunkGreenhouseIndex.TryGetValue(
                    chunkKey,
                    out ConcurrentDictionary<
                        GreenhouseKey,
                        byte
                    >? index
                )
            )
            {
                continue;
            }

            index.TryRemove(
                greenhouse.Key,
                out _
            );

            if (index.IsEmpty)
            {
                ChunkGreenhouseIndex.TryRemove(
                    chunkKey,
                    out _
                );
            }
        }
    }

    private void ScheduleIncompleteChunkRetry(
        ChunkKey chunkKey,
        string reason
    )
    {
        ICoreServerAPI? api = sapi;

        if (
            api == null
            || config?.Enabled != true
        )
        {
            return;
        }

        if (
            IncompleteChunkRetryAttempts.TryGetValue(
                chunkKey,
                out int previousAttempts
            )
            && previousAttempts
               >= MaxAutomaticIncompleteChunkRetries
        )
        {
            DebugLiteral(
                "[StillGreenhouses] SERVER INCOMPLETE CHUNK RETRY EXHAUSTED " +
                $"chunk={chunkKey.X},{chunkKey.Y},{chunkKey.Z}; " +
                $"dim={chunkKey.Dimension}; " +
                $"attempts={previousAttempts}; " +
                $"reason={reason}; " +
                "nextRetry=chunk-dirty-or-room-event"
            );

            return;
        }

        if (!ScheduledIncompleteChunkRetries.TryAdd(
                chunkKey,
                0
            ))
        {
            return;
        }

        int attempt =
            IncompleteChunkRetryAttempts.AddOrUpdate(
                chunkKey,
                1,
                (_, current) => Math.Min(
                    MaxAutomaticIncompleteChunkRetries,
                    current + 1
                )
            );

        int delayMs = attempt switch
        {
            1 => 1000,
            2 => 2000,
            3 => 5000,
            _ => 10000
        };

        Interlocked.Increment(
            ref incompleteChunkRetriesScheduled
        );

        DebugLiteral(
            "[StillGreenhouses] SERVER INCOMPLETE CHUNK RETRY SCHEDULED " +
            $"chunk={chunkKey.X},{chunkKey.Y},{chunkKey.Z}; " +
            $"dim={chunkKey.Dimension}; " +
            $"attempt={attempt}; " +
            $"delayMs={delayMs}; " +
            $"reason={reason}"
        );

        api.Event.RegisterCallback(
            _elapsedSeconds =>
            {
                if (
                    !ScheduledIncompleteChunkRetries
                        .TryRemove(
                            chunkKey,
                            out _
                        )
                )
                {
                    return;
                }

                if (
                    ChunkSubscribers.ContainsKey(chunkKey)
                    || ChunkGreenhouseIndex.ContainsKey(chunkKey)
                )
                {
                    if (!IsChunkScanQueuedOrActive(chunkKey))
                    {
                        PendingMaintenanceScans.TryAdd(
                            chunkKey,
                            0
                        );
                    }
                    else
                    {
                        Interlocked.Increment(
                            ref duplicateChunkScanAdmissionsSuppressed
                        );
                    }
                }
                else
                {
                    // No subscriber or indexed room owns this retry anymore.
                    // Do not leave an attempt counter that can block a later
                    // reconnect from restarting the incomplete state.
                    IncompleteChunkRetryAttempts.TryRemove(
                        chunkKey,
                        out _
                    );
                }
            },
            delayMs
        );
    }

    private void ResetIncompleteChunkRetry(
        ChunkKey chunkKey
    )
    {
        IncompleteChunkRetryAttempts.TryRemove(
            chunkKey,
            out _
        );

        ScheduledIncompleteChunkRetries.TryRemove(
            chunkKey,
            out _
        );
    }

    private void ScheduleChunkScan(
        ChunkKey chunkKey
    )
    {
        ICoreServerAPI? api = sapi;
        StillGreenhousesConfig? currentConfig =
            config;

        if (api == null
            || currentConfig?.Enabled != true
            || !ScheduledChunkScans.TryAdd(
                chunkKey,
                0
            ))
        {
            return;
        }

        api.Event.RegisterCallback(
            _elapsedSeconds =>
            {
                ScheduledChunkScans.TryRemove(
                    chunkKey,
                    out _
                );

                if (
                    ChunkSubscribers.ContainsKey(chunkKey)
                    || ChunkGreenhouseIndex.ContainsKey(chunkKey)
                )
                {
                    if (!IsChunkScanQueuedOrActive(chunkKey))
                    {
                        PendingMaintenanceScans.TryAdd(
                            chunkKey,
                            0
                        );
                    }
                    else
                    {
                        Interlocked.Increment(
                            ref duplicateChunkScanAdmissionsSuppressed
                        );
                    }
                }
            },
            currentConfig.ServerRescanDelayMs
        );
    }

    private void SetChunkStateAndPublish(
        ChunkKey chunkKey,
        bool complete,
        bool visualTransition = false
    )
    {
        List<GreenhouseRegion> greenhouses =
            GetGreenhousesForChunk(chunkKey);

        ulong contentHash =
            StillGreenhousesShared
                .ComputeRegionSetHash(greenhouses);

        bool changed;
        ServerChunkState newState;

        if (
            ChunkStates.TryGetValue(
                chunkKey,
                out ServerChunkState? oldState
            )
        )
        {
            bool contentChanged =
                oldState.ContentHash != contentHash;

            bool visualStateChanged =
                visualTransition
                || contentChanged;

            changed =
                oldState.Complete != complete
                || contentChanged
                || visualTransition;

            newState = new ServerChunkState(
                changed
                    ? oldState.Revision + 1
                    : oldState.Revision,
                visualStateChanged
                    ? oldState.VisualRevision + 1
                    : oldState.VisualRevision,
                complete,
                contentHash
            );
        }
        else
        {
            changed = true;

            newState = new ServerChunkState(
                Revision: 1,
                VisualRevision:
                    greenhouses.Count > 0
                        ? 1
                        : 0,
                Complete: complete,
                ContentHash: contentHash
            );
        }

        ChunkStates[
            chunkKey
        ] = newState;

        if (changed)
        {
            SendSnapshotToSubscribers(
                chunkKey,
                greenhouses,
                newState,
                contentHash
            );
        }

    }

    private List<GreenhouseRegion> GetGreenhousesForChunk(
        ChunkKey chunkKey
    )
    {
        List<GreenhouseRegion> greenhouses = new();

        if (
            !ChunkGreenhouseIndex.TryGetValue(
                chunkKey,
                out ConcurrentDictionary<
                    GreenhouseKey,
                    byte
                >? index
            )
        )
        {
            return greenhouses;
        }

        foreach (
            GreenhouseKey greenhouseKey
            in index.Keys
        )
        {
            if (
                Greenhouses.TryGetValue(
                    greenhouseKey,
                    out GreenhouseRegion? greenhouse
                )
            )
            {
                greenhouses.Add(greenhouse);
            }
        }

        return greenhouses;
    }

    private void SendSnapshotToSubscribers(
        ChunkKey chunkKey,
        IReadOnlyList<GreenhouseRegion> greenhouses,
        ServerChunkState state,
        ulong contentHash
    )
    {
        ICoreServerAPI? api = sapi;

        if (
            api == null
            || !ChunkSubscribers.TryGetValue(
                chunkKey,
                out ConcurrentDictionary<
                    string,
                    byte
                >? subscribers
            )
        )
        {
            return;
        }

        List<IServerPlayer> players = new();

        foreach (
            KeyValuePair<string, byte> subscriber
            in subscribers
        )
        {
            if (
                api.World.PlayerByUid(subscriber.Key)
                is IServerPlayer player
            )
            {
                players.Add(player);
            }
        }

        if (players.Count == 0)
        {
            return;
        }

        GreenhouseChunkSnapshot packet =
            BuildSnapshot(
                chunkKey,
                greenhouses,
                state,
                contentHash
            );

        serverChannel?.SendPacket(
            packet,
            players.ToArray()
        );
    }

    private void SendSnapshotToPlayer(
        ChunkKey chunkKey,
        IServerPlayer player
    )
    {
        serverChannel?.SendPacket(
            BuildSnapshot(chunkKey),
            player
        );
    }

    private GreenhouseChunkSnapshot BuildSnapshot(
        ChunkKey chunkKey
    )
    {
        List<GreenhouseRegion> greenhouses =
            GetGreenhousesForChunk(chunkKey);

        ulong contentHash =
            StillGreenhousesShared
                .ComputeRegionSetHash(greenhouses);

        ServerChunkState state =
            ChunkStates.TryGetValue(
                chunkKey,
                out ServerChunkState? cachedState
            )
                ? cachedState
                : new ServerChunkState(
                    Revision: 0,
                    VisualRevision:
                        greenhouses.Count > 0
                            ? 1
                            : 0,
                    Complete: false,
                    ContentHash: contentHash
                );

        return BuildSnapshot(
            chunkKey,
            greenhouses,
            state,
            contentHash
        );
    }

    private static GreenhouseChunkSnapshot BuildSnapshot(
        ChunkKey chunkKey,
        IReadOnlyList<GreenhouseRegion> greenhouses,
        ServerChunkState state,
        ulong contentHash
    )
    {
        return new GreenhouseChunkSnapshot
        {
            ChunkX = chunkKey.X,
            ChunkY = chunkKey.Y,
            ChunkZ = chunkKey.Z,
            Dimension = chunkKey.Dimension,
            Revision = state.Revision,
            VisualRevision = state.VisualRevision,
            Complete = state.Complete,
            ContentHash = contentHash,
            Greenhouses = greenhouses
                .OrderBy(greenhouse => greenhouse.Key.X1)
                .ThenBy(greenhouse => greenhouse.Key.Y1)
                .ThenBy(greenhouse => greenhouse.Key.Z1)
                .Select(greenhouse => greenhouse.ToPacket())
                .ToList()
        };
    }

    private void SubscribePlayer(
        string playerUid,
        ChunkKey chunkKey
    )
    {
        ChunkSubscribers
            .GetOrAdd(
                chunkKey,
                _ => new ConcurrentDictionary<
                    string,
                    byte
                >()
            )[playerUid] = 0;

        PlayerSubscriptions
            .GetOrAdd(
                playerUid,
                _ => new ConcurrentDictionary<
                    ChunkKey,
                    byte
                >()
            )[chunkKey] = 0;
    }

    private void OnPlayerDisconnect(
        IServerPlayer player
    )
    {
        RemovePlayerSubscriptions(
            player.PlayerUID
        );

        ClearRoomInspectionPlayerState(
            player.PlayerUID
        );
    }

    private void PruneSubscriptionsAndCache(
        float dt
    )
    {
        ICoreServerAPI? api = sapi;

        if (api == null)
        {
            return;
        }

        foreach (
            KeyValuePair<
                string,
                ConcurrentDictionary<ChunkKey, byte>
            > playerEntry
            in PlayerSubscriptions.ToArray()
        )
        {
            if (
                api.World.PlayerByUid(playerEntry.Key)
                is not IServerPlayer player
            )
            {
                RemovePlayerSubscriptions(
                    playerEntry.Key
                );

                continue;
            }

            foreach (
                ChunkKey chunkKey
                in playerEntry.Value.Keys.ToArray()
            )
            {
                if (
                    IsWithinSubscriptionRetentionRadius(
                        player,
                        chunkKey
                    )
                )
                {
                    continue;
                }

                RemoveSubscription(
                    playerEntry.Key,
                    chunkKey
                );
            }
        }

        HashSet<ChunkKey> subscribedChunks =
            ChunkSubscribers
                .Where(entry => !entry.Value.IsEmpty)
                .Select(entry => entry.Key)
                .ToHashSet();

        foreach (ChunkKey chunkKey in ChunkStates.Keys)
        {
            if (subscribedChunks.Contains(chunkKey))
            {
                continue;
            }

            ChunkStates.TryRemove(
                chunkKey,
                out _
            );

            LatestChunkDiscoveryResults.TryRemove(
                chunkKey,
                out _
            );

            PendingMaintenanceScans.TryRemove(
                chunkKey,
                out _
            );

            PendingDiscoveryScans.TryRemove(
                chunkKey,
                out _
            );

            ActiveChunkDiscoveries.TryRemove(
                chunkKey,
                out _
            );

            ScheduledChunkScans.TryRemove(
                chunkKey,
                out _
            );

            ResetIncompleteChunkRetry(chunkKey);
        }

        foreach (
            KeyValuePair<
                GreenhouseKey,
                GreenhouseRegion
            > formerRoomEntry
            in FormerManagedRooms.ToArray()
        )
        {
            if (HasNearbyPlayerForFormerRoom(
                    api,
                    formerRoomEntry.Value
                ))
            {
                continue;
            }

            RemoveFormerManagedRoom(
                formerRoomEntry.Key,
                reason: "no-relevant-player-nearby"
            );
        }

        HashSet<GreenhouseKey> neededGreenhouses = new();

        foreach (ChunkKey chunkKey in subscribedChunks)
        {
            if (
                !ChunkGreenhouseIndex.TryGetValue(
                    chunkKey,
                    out ConcurrentDictionary<
                        GreenhouseKey,
                        byte
                    >? index
                )
            )
            {
                continue;
            }

            neededGreenhouses.UnionWith(
                index.Keys
            );
        }

        foreach (
            GreenhouseKey greenhouseKey
            in Greenhouses.Keys.ToArray()
        )
        {
            if (
                neededGreenhouses.Contains(
                    greenhouseKey
                )
            )
            {
                continue;
            }

            if (
                !Greenhouses.TryGetValue(
                    greenhouseKey,
                    out GreenhouseRegion? greenhouse
                )
            )
            {
                continue;
            }

            RemoveGreenhouseFromLiveIndex(
                greenhouse
            );
        }

        if (config?.DebugLogging == true)
        {
            DebugLiteral(
                "[StillGreenhouses] SERVER PRUNE " +
                $"players={PlayerSubscriptions.Count}; " +
                $"subscribedChunks={subscribedChunks.Count}; " +
                $"chunkStates={ChunkStates.Count}; " +
                $"formerManagedRooms={FormerManagedRooms.Count}; " +
                $"formerRoomIndexChunks={FormerManagedRoomChunkIndex.Count}; " +
                $"greenhouses={Greenhouses.Count}"
            );
        }
    }

    private void RemovePlayerSubscriptions(
        string playerUid
    )
    {
        if (
            !PlayerSubscriptions.TryRemove(
                playerUid,
                out ConcurrentDictionary<
                    ChunkKey,
                    byte
                >? subscriptions
            )
        )
        {
            return;
        }

        foreach (
            ChunkKey chunkKey
            in subscriptions.Keys
        )
        {
            RemoveSubscription(
                playerUid,
                chunkKey,
                removeFromPlayerMap: false
            );
        }
    }

    private void RemoveSubscription(
        string playerUid,
        ChunkKey chunkKey,
        bool removeFromPlayerMap = true
    )
    {
        if (
            removeFromPlayerMap
            && PlayerSubscriptions.TryGetValue(
                playerUid,
                out ConcurrentDictionary<
                    ChunkKey,
                    byte
                >? playerChunks
            )
        )
        {
            playerChunks.TryRemove(
                chunkKey,
                out _
            );

            if (playerChunks.IsEmpty)
            {
                PlayerSubscriptions.TryRemove(
                    playerUid,
                    out _
                );
            }
        }

        if (
            !ChunkSubscribers.TryGetValue(
                chunkKey,
                out ConcurrentDictionary<
                    string,
                    byte
                >? subscribers
            )
        )
        {
            return;
        }

        subscribers.TryRemove(
            playerUid,
            out _
        );

        if (subscribers.IsEmpty)
        {
            ChunkSubscribers.TryRemove(
                chunkKey,
                out _
            );
        }
    }

    private bool IsWithinSubscriptionRetentionRadius(
        IServerPlayer player,
        ChunkKey chunkKey
    )
    {
        StillGreenhousesConfig? currentConfig =
            config;

        if (currentConfig == null)
        {
            return false;
        }

        BlockPos playerPos =
            new BlockPos(
                player.Entity.Pos.Dimension
            )
            .Set(player.Entity.Pos);

        ChunkKey playerChunk =
            ChunkKey.From(playerPos);

        int radius =
            currentConfig.ServerSubscriptionRadiusChunks;

        return chunkKey.Dimension
                   == playerChunk.Dimension
               && Math.Abs(
                   chunkKey.X - playerChunk.X
               ) <= radius
               && Math.Abs(
                   chunkKey.Z - playerChunk.Z
               ) <= radius
               && Math.Abs(
                   chunkKey.Y - playerChunk.Y
               ) <= 8;
    }

    private bool IsRequestReasonable(
        IServerPlayer player,
        ChunkKey requestedChunk
    )
    {
        BlockPos playerPos =
            new BlockPos(
                player.Entity.Pos.Dimension
            )
            .Set(player.Entity.Pos);

        ChunkKey playerChunk =
            ChunkKey.From(playerPos);

        return requestedChunk.Dimension
                   == playerChunk.Dimension
               && Math.Abs(
                   requestedChunk.X - playerChunk.X
               ) <= 64
               && Math.Abs(
                   requestedChunk.Y - playerChunk.Y
               ) <= 16
               && Math.Abs(
                   requestedChunk.Z - playerChunk.Z
               ) <= 64;
    }

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

    private readonly record struct StructuralRoomProbeKey(
        int CellX,
        int CellY,
        int CellZ,
        int Dimension
    )
    {
        private const int CoalesceSize = 8;

        internal static StructuralRoomProbeKey From(
            BlockPos pos
        ) =>
            new(
                StillGreenhousesShared.FloorDiv(
                    pos.X,
                    CoalesceSize
                ),
                StillGreenhousesShared.FloorDiv(
                    pos.Y,
                    CoalesceSize
                ),
                StillGreenhousesShared.FloorDiv(
                    pos.Z,
                    CoalesceSize
                ),
                pos.dimension
            );
    }

    private readonly record struct StructuralRoomProbePosition(
        int X,
        int Y,
        int Z,
        int Dimension
    )
    {
        internal static StructuralRoomProbePosition From(
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

    private sealed class StructuralRoomProbeBatch
    {
        private readonly ConcurrentDictionary<
            StructuralRoomProbePosition,
            byte
        > positions = new();

        private long scheduleVersion;

        internal long ScheduleVersion =>
            Interlocked.Read(
                ref scheduleVersion
            );

        internal long Add(
            StructuralRoomProbePosition position
        )
        {
            positions[position] = 0;

            return Interlocked.Increment(
                ref scheduleVersion
            );
        }

        internal StructuralRoomProbePosition[]
            GetPositions() =>
                positions.Keys.ToArray();
    }

    private readonly record struct RoomRevalidationKey(
        int X1,
        int Y1,
        int Z1,
        int X2,
        int Y2,
        int Z2,
        ulong ShapeHash
    )
    {
        internal static RoomRevalidationKey From(
            Room room
        ) =>
            new(
                room.Location.X1,
                room.Location.Y1,
                room.Location.Z1,
                room.Location.X2,
                room.Location.Y2,
                room.Location.Z2,
                StillGreenhousesShared.ComputeShapeHash(
                    room.PosInRoom
                    ?? Array.Empty<byte>()
                )
            );
    }

    private void LogServerCacheSummary(
        float dt
    )
    {
        if (config?.DebugLogging != true)
        {
            return;
        }

        DebugLiteral(
            "[StillGreenhouses] SERVER CACHE SUMMARY " +
            $"greenhouseRevalidations={PendingGreenhouseRevalidations.Count}; " +
            $"maintenancePending={PendingMaintenanceScans.Count}; " +
            $"discoveryPending={PendingDiscoveryScans.Count}; " +
            $"activeDiscoveryScans={ActiveChunkDiscoveries.Count}; " +
            $"latestDiscoveryResults={LatestChunkDiscoveryResults.Count}; " +
            $"structuralProbeBatches={ScheduledStructuralRoomProbes.Count}; " +
            $"formerManagedRooms={FormerManagedRooms.Count}; " +
            $"formerRoomScansScheduled={ScheduledFormerManagedRoomScans.Count}; " +
            $"pendingRoomDisappearances={PendingRoomDisappearances.Count}; " +
            $"scheduledDisappearanceGraceChecks={ScheduledDisappearanceGraceChecks.Count}; " +
            $"pendingDisappearanceDiscoveryRefreshes={PendingDisappearanceDiscoveryRefreshes.Count}; " +
            $"minimumInteriorPositions={config.MinimumManagedRoomInteriorPositions}; " +
            $"disappearanceGraceMs={config.ServerRoomDisappearanceGraceMs}; " +
            $"incompleteDisappearanceRetentionMs={config.ServerIncompleteRoomDisappearanceRetentionMs}; " +
            $"discoveryAnchorPositions={Interlocked.Read(ref discoveryAnchorPositionsObserved)}; " +
            $"discoveryRoomRegistryQueries={Interlocked.Read(ref discoveryRoomRegistryQueriesObserved)}; " +
            $"discoveryCoveredRoomPositionsSkipped={Interlocked.Read(ref discoveryCoveredRoomPositionsSkipped)}; " +
            $"discoveryAnchorRoomInstances={Interlocked.Read(ref discoveryAnchorRoomInstancesObserved)}; " +
            $"discoveryIncompleteAnchorRooms={Interlocked.Read(ref discoveryIncompleteAnchorRoomsObserved)}; " +
            $"roomViabilityChecks={Interlocked.Read(ref roomViabilityChecks)}; " +
            $"roomsSkippedTooSmall={Interlocked.Read(ref roomsSkippedTooSmall)}; " +
            $"roomsSkippedNoDiscoveryAnchor={Interlocked.Read(ref roomsSkippedWithoutDiscoveryAnchor)}; " +
            $"formerRoomsSkippedTooSmall={Interlocked.Read(ref formerRoomsSkippedTooSmall)}; " +
            $"formerRoomsSkippedNoDiscoveryAnchor={Interlocked.Read(ref formerRoomsSkippedWithoutDiscoveryAnchor)}; " +
            $"roomDisappearanceGraceStarted={Interlocked.Read(ref roomDisappearanceGraceStarted)}; " +
            $"roomDisappearanceZeroObservations={Interlocked.Read(ref roomDisappearanceZeroObservations)}; " +
            $"roomDisappearanceGraceCleared={Interlocked.Read(ref roomDisappearanceGraceCleared)}; " +
            $"roomDisappearanceGraceConfirmed={Interlocked.Read(ref roomDisappearanceGraceConfirmed)}; " +
            $"roomDisappearanceFinalChecksIncomplete={Interlocked.Read(ref roomDisappearanceFinalChecksIncomplete)}; " +
            $"roomDisappearanceIncompletePositiveRetained={Interlocked.Read(ref roomDisappearanceIncompletePositiveRetained)}; " +
            $"roomDisappearanceIncompleteRetentionExtended={Interlocked.Read(ref roomDisappearanceIncompleteRetentionExtended)}; " +
            $"seedRoomRegistryObservationRuns={Interlocked.Read(ref seedRoomRegistryObservationRuns)}; " +
            $"incompleteChunkRetryAttempts={IncompleteChunkRetryAttempts.Count}; " +
            $"scheduledIncompleteChunkRetries={ScheduledIncompleteChunkRetries.Count}; " +
            $"incompleteChunkRetriesScheduled={Interlocked.Read(ref incompleteChunkRetriesScheduled)}; " +
            $"pendingChunkAcknowledgementsPublished={Interlocked.Read(ref pendingChunkAcknowledgementsPublished)}; " +
            $"duplicateChunkScanAdmissionsSuppressed={Interlocked.Read(ref duplicateChunkScanAdmissionsSuppressed)}; " +
            $"staleChunkScanQueueEntriesRemoved={Interlocked.Read(ref staleChunkScanQueueEntriesRemoved)}; " +
            $"foregroundBudgetMs={config.ServerForegroundWorkBudgetMs:0.###}; " +
            $"effectiveSliceBudgetMs={GetEffectiveDiscoveryWorkBudgetMs(config.ServerForegroundWorkBudgetMs):0.###}; " +
            $"foregroundBudgetYields={Interlocked.Read(ref serverForegroundBudgetYields)}; " +
            $"serverScanOperations={Interlocked.Read(ref serverScanOperations)}; " +
            $"serverScanLastMs={Interlocked.Read(ref lastServerScanMicroseconds) / 1000d:F3}; " +
            $"serverScanMaxMs={Interlocked.Read(ref maxServerScanMicroseconds) / 1000d:F3}; " +
            $"serverRevalidationOperations={Interlocked.Read(ref serverRevalidationOperations)}; " +
            $"serverRevalidationLastMs={Interlocked.Read(ref lastServerRevalidationMicroseconds) / 1000d:F3}; " +
            $"serverRevalidationMaxMs={Interlocked.Read(ref maxServerRevalidationMicroseconds) / 1000d:F3}; " +
            $"discoveryOperations={Interlocked.Read(ref discoveryOperations)}; " +
            $"discoveryPositionsVisited={Interlocked.Read(ref discoveryPositionsVisited)}; " +
            $"discoveryLastMs={Interlocked.Read(ref lastDiscoveryMicroseconds) / 1000d:F3}; " +
            $"discoveryMaxMs={Interlocked.Read(ref maxDiscoveryMicroseconds) / 1000d:F3}; " +
            $"butterflyWeatherLookups={Interlocked.Read(ref butterflyWeatherLookups)}; " +
            $"butterflyShelteredWeatherLookups={Interlocked.Read(ref butterflyShelteredWeatherLookups)}; " +
            $"butterflyWanderBoostLookups={Interlocked.Read(ref butterflyWanderBoostLookups)}; " +
            $"butterflyShelteredWanderBoosts={Interlocked.Read(ref butterflyShelteredWanderBoosts)}; " +
            $"chunkStates={ChunkStates.Count}; " +
            $"managedRooms={Greenhouses.Count}; " +
            $"subscriptions={ChunkSubscribers.Sum(pair => pair.Value.Count)}"
        );
    }

    public override void Dispose()
    {
        ICoreServerAPI? api = sapi;

        // Stop new patched AI calls from entering this instance before Harmony
        // is removed or any room-cache state is cleared. CompareExchange also
        // prevents an older world instance from clearing a newer one.
        Interlocked.CompareExchange(
            ref activeInstance,
            null,
            this
        );

        // The butterfly patch is process-wide and idempotent. Keeping it
        // installed between worlds closes the unpatch/repatch race; with no
        // active instance its resolvers simply return Vanilla weather values.
        serverHarmony = null;

        if (api != null)
        {
            api.Event.DidPlaceBlock -= OnDidPlaceBlock;
            api.Event.DidBreakBlock -= OnDidBreakBlock;
            api.Event.DidUseBlock -= OnDidUseBlock;
            api.Event.ChunkDirty -= OnChunkDirty;
            api.Event.PlayerDisconnect -= OnPlayerDisconnect;

            if (chunkScanListenerId != 0)
            {
                api.Event.UnregisterGameTickListener(
                    chunkScanListenerId
                );

                chunkScanListenerId = 0;
            }

            if (subscriptionPruneListenerId != 0)
            {
                api.Event.UnregisterGameTickListener(
                    subscriptionPruneListenerId
                );

                subscriptionPruneListenerId = 0;
            }

            if (debugSummaryListenerId != 0)
            {
                api.Event.UnregisterGameTickListener(
                    debugSummaryListenerId
                );

                debugSummaryListenerId = 0;
            }
        }

        PendingMaintenanceScans.Clear();
        PendingDiscoveryScans.Clear();
        ActiveChunkDiscoveries.Clear();
        LatestChunkDiscoveryResults.Clear();
        ScheduledChunkScans.Clear();
        ScheduledStructuralRoomProbes.Clear();
        FormerManagedRooms.Clear();
        FormerManagedRoomChunkIndex.Clear();
        ScheduledFormerManagedRoomScans.Clear();
        PendingGreenhouseRevalidations.Clear();
        ScheduledGreenhouseRevalidations.Clear();
        PendingRoomDisappearances.Clear();
        ScheduledDisappearanceGraceChecks.Clear();
        PendingDisappearanceDiscoveryRefreshes.Clear();
        IncompleteChunkRetryAttempts.Clear();
        ScheduledIncompleteChunkRetries.Clear();
        DiscoveryAnchorBlockIdentityCache.Clear();
        Greenhouses.Clear();
        ChunkGreenhouseIndex.Clear();
        ChunkStates.Clear();
        ChunkSubscribers.Clear();
        PlayerSubscriptions.Clear();

        config = null;
        roomRegistry = null;
        serverChannel = null;
        sapi = null;

        base.Dispose();
    }

    private readonly record struct RoomBounds(
        int X1,
        int Y1,
        int Z1,
        int X2,
        int Y2,
        int Z2
    )
    {
        internal static RoomBounds From(
            Room room
        ) =>
            new(
                room.Location.X1,
                room.Location.Y1,
                room.Location.Z1,
                room.Location.X2,
                room.Location.Y2,
                room.Location.Z2
            );

        internal bool OverlapsWithMargin(
            GreenhouseRegion room,
            int margin
        ) =>
            X1 <= room.X2 + margin
            && X2 >= room.X1 - margin
            && Y1 <= room.Y2 + margin
            && Y2 >= room.Y1 - margin
            && Z1 <= room.Z2 + margin
            && Z2 >= room.Z1 - margin;
    }

    private sealed class ServerPerformanceScope : IDisposable
    {
        private StillGreenhousesServerSystem? owner;
        private readonly string operation;
        private readonly string details;
        private readonly long startTimestamp;

        internal ServerPerformanceScope(
            StillGreenhousesServerSystem owner,
            string operation,
            string details
        )
        {
            this.owner = owner;
            this.operation = operation;
            this.details = details;
            startTimestamp = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            StillGreenhousesServerSystem? currentOwner =
                owner;

            if (currentOwner == null)
            {
                return;
            }

            owner = null;

            currentOwner.LogSlowServerOperation(
                operation,
                GetElapsedMilliseconds(
                    startTimestamp
                ),
                details
            );
        }
    }

    private readonly record struct SeedRoomAnchorObservationSummary(
        int SeedAnchors,
        int AnchorsWithRoom,
        int AnchorsWithoutRoom,
        int DistinctRooms,
        int IncompleteRoomAnchorHits,
        int ClassifiedRooms,
        int ClassifiedOverlappingRooms
    );

    private sealed record PendingRoomDisappearance(
        long FirstZeroMilliseconds,
        int ZeroObservations
    );

    private sealed class ChunkDiscoveryScan
    {
        internal ChunkDiscoveryScan(
            ChunkKey chunkKey,
            RoomRegistry registry,
            IWorldChunk sourceChunk
        )
        {
            ChunkKey = chunkKey;
            Registry = registry;
            SourceChunk = sourceChunk;
            ChunkData = sourceChunk.Data;
            ChunkSize = StillGreenhousesShared.ChunkSize;
            StartX = chunkKey.X * ChunkSize;
            StartY = chunkKey.Y * ChunkSize;
            StartZ = chunkKey.Z * ChunkSize;
            Probe = new BlockPos(
                StartX,
                StartY,
                StartZ,
                chunkKey.Dimension
            );
            CoveredRoomPositions = new bool[
                ChunkSize * ChunkSize * ChunkSize
            ];
        }

        internal ChunkKey ChunkKey { get; }
        internal RoomRegistry Registry { get; }
        internal IWorldChunk SourceChunk { get; }
        internal IChunkBlocks ChunkData { get; }
        internal int ChunkSize { get; }
        internal int StartX { get; }
        internal int StartY { get; }
        internal int StartZ { get; }
        internal BlockPos Probe { get; }
        internal bool[] CoveredRoomPositions { get; }

        internal HashSet<Room> SeenRoomInstances { get; } =
            new(ReferenceEqualityComparer.Instance);

        internal HashSet<RoomRevalidationKey> SeenRoomKeys { get; } =
            new();

        internal Dictionary<
            GreenhouseKey,
            GreenhouseRegion
        > DiscoveredRooms { get; } = new();

        internal List<RoomBounds> IncompleteRoomBounds { get; } =
            new();

        internal int NextLocalIndex { get; set; }
        internal int SliceCount { get; set; }
        internal long ProcessingMicroseconds { get; set; }
        internal long MaxSliceMicroseconds { get; set; }
        internal int DiscoveryAnchorPositions { get; set; }
        internal int RoomRegistryQueries { get; set; }
        internal int CoveredRoomPositionsSkipped { get; set; }
        internal int AnchorRoomInstances { get; set; }
        internal int IncompleteAnchorRooms { get; set; }
        internal int ClassifiedManagedRooms { get; set; }
        internal int ViableManagedRooms { get; set; }
        internal int SkippedTooSmallRooms { get; set; }
        internal int RoomSizes1To2 { get; set; }
        internal int RoomSizes3To6 { get; set; }
        internal int RoomSizes7To15 { get; set; }
        internal int RoomSizes16To31 { get; set; }
        internal int RoomSizes32Plus { get; set; }
    }

    private sealed record ChunkDiscoveryResult(
        ChunkKey ChunkKey,
        bool Loaded,
        bool Complete,
        Dictionary<
            GreenhouseKey,
            GreenhouseRegion
        > ManagedRooms,
        List<RoomBounds> IncompleteRoomBounds,
        int DiscoveryAnchorPositions,
        int RoomRegistryQueries,
        int CoveredRoomPositionsSkipped,
        int AnchorRoomInstances,
        int IncompleteAnchorRooms,
        int ClassifiedManagedRooms,
        int ViableManagedRooms,
        int SkippedTooSmallRooms,
        int RoomSizes1To2,
        int RoomSizes3To6,
        int RoomSizes7To15,
        int RoomSizes16To31,
        int RoomSizes32Plus
    )
    {
        internal static ChunkDiscoveryResult Unavailable(
            ChunkKey chunkKey
        ) =>
            new(
                ChunkKey: chunkKey,
                Loaded: false,
                Complete: false,
                ManagedRooms: new Dictionary<
                    GreenhouseKey,
                    GreenhouseRegion
                >(),
                IncompleteRoomBounds: new List<RoomBounds>(),
                DiscoveryAnchorPositions: 0,
                RoomRegistryQueries: 0,
                CoveredRoomPositionsSkipped: 0,
                AnchorRoomInstances: 0,
                IncompleteAnchorRooms: 0,
                ClassifiedManagedRooms: 0,
                ViableManagedRooms: 0,
                SkippedTooSmallRooms: 0,
                RoomSizes1To2: 0,
                RoomSizes3To6: 0,
                RoomSizes7To15: 0,
                RoomSizes16To31: 0,
                RoomSizes32Plus: 0
            );
    }

    private sealed record ServerChunkState(
        long Revision,
        long VisualRevision,
        bool Complete,
        ulong ContentHash
    );
}
