/*
version 0.18.0
*/

using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace StillGreenhouses;

public sealed partial class StillGreenhousesClientSystem
{
    private const int RoomInspectionHighlightSlot = 52731;
    private const int RoomInspectionRequestIntervalMs = 250;
    private const int RoomInspectionIncompleteRetryMs = 1000;

    private static readonly (int X, int Y, int Z)[]
        RoomInspectionNeighborOffsets =
        [
            (1, 0, 0),
            (-1, 0, 0),
            (0, 1, 0),
            (0, -1, 0),
            (0, 0, 1),
            (0, 0, -1)
        ];

    private static long roomInspectionTickListenerId;
    private static long nextRoomInspectionRequestId;
    private static long latestRoomInspectionRequestId;
    private static long latestAppliedRoomInspectionRequestId;
    private static long nextRoomInspectionRequestAtMs;

    private static bool roomInspectionRefreshPending;
    private static bool roomInspectionServerDenied;
    private static bool roomInspectionDisabledMessageShown;
    private static bool roomInspectionHighlightVisible;

    private static RoomInspectionPlayerCell?
        roomInspectionLastPlayerCell;

    private static GreenhouseRegion?
        roomInspectionCurrentRegion;

    private readonly record struct RoomInspectionPlayerCell(
        int X,
        int Y,
        int Z,
        int Dimension
    )
    {
        internal static RoomInspectionPlayerCell From(
            IClientPlayer player
        )
        {
            var pos = player.Entity.Pos;

            return new RoomInspectionPlayerCell(
                (int)Math.Floor(pos.X),
                (int)Math.Floor(pos.Y),
                (int)Math.Floor(pos.Z),
                pos.Dimension
            );
        }
    }

    private static void InitializeRoomInspection(
        ICoreClientAPI api
    )
    {
        ResetRoomInspectionState(
            api,
            clearHighlight: true
        );

        if (roomInspectionTickListenerId != 0)
        {
            api.Event.UnregisterGameTickListener(
                roomInspectionTickListenerId
            );
        }

        roomInspectionTickListenerId =
            api.Event.RegisterGameTickListener(
                UpdateRoomInspection,
                RoomInspectionRequestIntervalMs
            );

        QueueRoomInspectionRefresh();
    }

    private static void DisposeRoomInspection(
        ICoreClientAPI api
    )
    {
        if (roomInspectionTickListenerId != 0)
        {
            api.Event.UnregisterGameTickListener(
                roomInspectionTickListenerId
            );
        }

        roomInspectionTickListenerId = 0;

        ResetRoomInspectionState(
            api,
            clearHighlight: true
        );
    }

    private static void ResetRoomInspectionState(
        ICoreClientAPI? api,
        bool clearHighlight
    )
    {
        if (clearHighlight && api != null)
        {
            ClearRoomInspectionHighlight(api);
        }

        roomInspectionLastPlayerCell = null;
        roomInspectionCurrentRegion = null;
        roomInspectionRefreshPending = false;
        roomInspectionServerDenied = false;
        roomInspectionDisabledMessageShown = false;
        roomInspectionHighlightVisible = false;
        long requestBarrier =
            System.Threading.Interlocked.Increment(
                ref nextRoomInspectionRequestId
            );

        latestRoomInspectionRequestId = requestBarrier;
        latestAppliedRoomInspectionRequestId = requestBarrier;
        nextRoomInspectionRequestAtMs = 0;
    }

    private static void QueueRoomInspectionRefresh()
    {
        roomInspectionRefreshPending = true;
    }

    private static void QueueRoomInspectionRefreshForBlockChange(
        ICoreClientAPI api,
        BlockPos changedPos
    )
    {
        StillGreenhousesConfig? currentConfig = Config;
        IClientPlayer? player = api.World.Player;

        if (
            currentConfig?.Enabled != true
            || !currentConfig.ShowRoomInspectionOverlay
            || player?.Entity == null
        )
        {
            return;
        }

        RoomInspectionPlayerCell playerCell =
            RoomInspectionPlayerCell.From(
                player
            );

        if (changedPos.dimension != playerCell.Dimension)
        {
            return;
        }

        // The server chooses the actual failure-overlay radius. Use the
        // maximum supported radius plus a small structural margin only to
        // decide whether a local block change warrants a new validation.
        const int radius = 26;

        if (
            Math.Abs(changedPos.X - playerCell.X) > radius
            || Math.Abs(changedPos.Y - playerCell.Y) > radius
            || Math.Abs(changedPos.Z - playerCell.Z) > radius
        )
        {
            return;
        }

        roomInspectionCurrentRegion = null;
        roomInspectionRefreshPending = true;
        LimitRoomInspectionRequestDelay(
            api,
            RoomInspectionRequestIntervalMs
        );

        // Keep the previous overlay visible until the new authoritative
        // response arrives. This avoids flashing during rapid construction.
    }

    private static void UpdateRoomInspection(
        float dt
    )
    {
        ICoreClientAPI? api = Capi;
        StillGreenhousesConfig? currentConfig = Config;

        if (api == null || currentConfig == null)
        {
            return;
        }

        bool enabled =
            currentConfig.Enabled
            && currentConfig.ShowRoomInspectionOverlay;

        if (!enabled)
        {
            if (roomInspectionHighlightVisible)
            {
                ClearRoomInspectionHighlight(api);
            }

            roomInspectionLastPlayerCell = null;
            roomInspectionRefreshPending = false;
            return;
        }

        if (roomInspectionServerDenied)
        {
            return;
        }

        IClientPlayer? player = api.World.Player;

        if (player?.Entity == null)
        {
            return;
        }

        RoomInspectionPlayerCell playerCell =
            RoomInspectionPlayerCell.From(
                player
            );

        if (
            !roomInspectionLastPlayerCell.HasValue
            || roomInspectionLastPlayerCell.Value
                != playerCell
        )
        {
            roomInspectionLastPlayerCell = playerCell;

            BlockPos playerBlockPos = new(
                playerCell.X,
                playerCell.Y,
                playerCell.Z,
                playerCell.Dimension
            );

            bool remainsInsideValidatedRoom =
                roomInspectionCurrentRegion
                    ?.Contains(playerBlockPos)
                == true;

            if (!remainsInsideValidatedRoom)
            {
                roomInspectionCurrentRegion = null;
                roomInspectionRefreshPending = true;
                LimitRoomInspectionRequestDelay(
                    api,
                    RoomInspectionRequestIntervalMs
                );

                // Keep the previous overlay visible until the server returns
                // the classification for the new player position.
            }
        }

        if (
            !roomInspectionRefreshPending
            && latestAppliedRoomInspectionRequestId
                < latestRoomInspectionRequestId
            && api.ElapsedMilliseconds
                >= nextRoomInspectionRequestAtMs
        )
        {
            // A request may have been dropped by transport or server-side
            // rate limiting. Retry without requiring another player move.
            roomInspectionRefreshPending = true;
        }

        if (
            !roomInspectionRefreshPending
            || api.ElapsedMilliseconds
                < nextRoomInspectionRequestAtMs
        )
        {
            return;
        }

        SendRoomInspectionRequest(api);
    }

    private static void LimitRoomInspectionRequestDelay(
        ICoreClientAPI api,
        int delayMs
    )
    {
        long requestedAt =
            api.ElapsedMilliseconds + delayMs;

        if (
            nextRoomInspectionRequestAtMs == 0
            || requestedAt
                < nextRoomInspectionRequestAtMs
        )
        {
            nextRoomInspectionRequestAtMs =
                requestedAt;
        }
    }

    private static void SendRoomInspectionRequest(
        ICoreClientAPI api
    )
    {
        IClientNetworkChannel? channel = clientChannel;

        if (channel == null)
        {
            return;
        }

        long requestId =
            System.Threading.Interlocked.Increment(
                ref nextRoomInspectionRequestId
            );

        latestRoomInspectionRequestId = requestId;
        roomInspectionRefreshPending = false;
        nextRoomInspectionRequestAtMs =
            api.ElapsedMilliseconds
            + RoomInspectionIncompleteRetryMs;

        channel.SendPacket(
            new RoomInspectionRequest
            {
                RequestId = requestId
            }
        );
    }

    private static void OnRoomInspectionResponse(
        RoomInspectionResponse response
    )
    {
        ICoreClientAPI? api = Capi;

        if (api == null)
        {
            return;
        }

        api.Event.EnqueueMainThreadTask(
            () => ApplyRoomInspectionResponse(
                api,
                response
            ),
            "stillgreenhouses-room-inspection-response"
        );
    }

    private static void ApplyRoomInspectionResponse(
        ICoreClientAPI api,
        RoomInspectionResponse response
    )
    {
        StillGreenhousesConfig? currentConfig = Config;

        if (
            currentConfig?.Enabled != true
            || !currentConfig.ShowRoomInspectionOverlay
            || response.RequestId
                < latestRoomInspectionRequestId
        )
        {
            return;
        }

        latestAppliedRoomInspectionRequestId =
            Math.Max(
                latestAppliedRoomInspectionRequestId,
                response.RequestId
            );

        RoomInspectionResultKind resultKind =
            Enum.IsDefined(
                typeof(RoomInspectionResultKind),
                response.ResultKind
            )
                ? (RoomInspectionResultKind)response.ResultKind
                : RoomInspectionResultKind.Incomplete;

        switch (resultKind)
        {
            case RoomInspectionResultKind.DisabledByServer:
                roomInspectionCurrentRegion = null;
                roomInspectionServerDenied = true;
                roomInspectionRefreshPending = false;
                ClearRoomInspectionHighlight(api);

                if (!roomInspectionDisabledMessageShown)
                {
                    roomInspectionDisabledMessageShown = true;

                    api.ShowChatMessage(
                        Lang.Get(
                            "stillgreenhouses:room-inspection-disabled-by-server"
                        )
                    );
                }

                return;

            case RoomInspectionResultKind.NoVanillaRoom:
                roomInspectionCurrentRegion = null;
                ApplyFailureRoomInspectionHighlight(
                    api,
                    response,
                    ColorUtil.ColorFromRgba(
                        255,
                        59,
                        48,
                        64
                    )
                );
                return;

            case RoomInspectionResultKind.Incomplete:
                roomInspectionCurrentRegion = null;
                ApplyFailureRoomInspectionHighlight(
                    api,
                    response,
                    ColorUtil.ColorFromRgba(
                        174,
                        174,
                        178,
                        48
                    )
                );

                roomInspectionRefreshPending = true;
                nextRoomInspectionRequestAtMs =
                    api.ElapsedMilliseconds
                    + RoomInspectionIncompleteRetryMs;
                return;

            case RoomInspectionResultKind.VanillaRoomOnly:
            case RoomInspectionResultKind.ManagedRoom:
                if (response.Region == null)
                {
                    roomInspectionCurrentRegion = null;
                    ClearRoomInspectionHighlight(api);
                    roomInspectionRefreshPending = true;
                    return;
                }

                ApplyValidRoomInspectionHighlight(
                    api,
                    response,
                    resultKind
                );
                return;

            default:
                ClearRoomInspectionHighlight(api);
                roomInspectionRefreshPending = true;
                return;
        }
    }

    private static void ApplyValidRoomInspectionHighlight(
        ICoreClientAPI api,
        RoomInspectionResponse response,
        RoomInspectionResultKind resultKind
    )
    {
        GreenhouseRegion region =
            GreenhouseRegion.FromPacket(
                response.Region!
            );

        List<BlockPos> blocks = new(
            Math.Max(
                1,
                region.OccupiedPositionCount
            )
        );

        foreach (BlockPos pos in region.GetOccupiedPositions())
        {
            blocks.Add(pos);
        }

        roomInspectionCurrentRegion = region;

        ManagedRoomType roomType =
            StillGreenhousesShared.ResolveManagedRoomType(
                response.RoomType
            );

        int color = GetValidRoomInspectionColor(
            resultKind,
            roomType
        );

        ApplyRoomInspectionHighlight(
            api,
            blocks,
            color
        );
    }

    private static int GetValidRoomInspectionColor(
        RoomInspectionResultKind resultKind,
        ManagedRoomType roomType
    )
    {
        bool passesStillGreenhouses =
            resultKind
                == RoomInspectionResultKind.ManagedRoom;

        if (passesStillGreenhouses)
        {
            return roomType switch
            {
                ManagedRoomType.Greenhouse =>
                    ColorUtil.ColorFromRgba(
                        48,
                        209,
                        88,
                        64
                    ),

                ManagedRoomType.Cellar =>
                    ColorUtil.ColorFromRgba(
                        191,
                        90,
                        242,
                        64
                    ),

                _ =>
                    ColorUtil.ColorFromRgba(
                        64,
                        156,
                        255,
                        64
                    )
            };
        }

        return roomType switch
        {
            ManagedRoomType.Greenhouse =>
                ColorUtil.ColorFromRgba(
                    255,
                    214,
                    10,
                    64
                ),

            ManagedRoomType.Cellar =>
                ColorUtil.ColorFromRgba(
                    255,
                    105,
                    180,
                    64
                ),

            _ =>
                ColorUtil.ColorFromRgba(
                    255,
                    159,
                    10,
                    64
                )
        };
    }

    private static void ApplyFailureRoomInspectionHighlight(
        ICoreClientAPI api,
        RoomInspectionResponse response,
        int color
    )
    {
        int radius = Math.Clamp(
            response.FailureRadius,
            4,
            24
        );

        BlockPos center = new(
            response.CenterX,
            response.CenterY,
            response.CenterZ,
            response.Dimension
        );

        List<BlockPos> blocks =
            BuildFailureSurfacePositions(
                api,
                center,
                radius
            );

        ApplyRoomInspectionHighlight(
            api,
            blocks,
            color
        );
    }

    private static List<BlockPos>
        BuildFailureSurfacePositions(
            ICoreClientAPI api,
            BlockPos center,
            int radius
        )
    {
        IBlockAccessor blockAccessor =
            api.World.BlockAccessor;

        int radiusSquared = radius * radius;
        List<BlockPos> surfaces = new(4096);

        BlockPos sample = new(center.dimension);
        BlockPos neighbor = new(center.dimension);

        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (
                        dx * dx
                        + dy * dy
                        + dz * dz
                        > radiusSquared
                    )
                    {
                        continue;
                    }

                    int x = center.X + dx;
                    int y = center.Y + dy;
                    int z = center.Z + dz;

                    sample.Set(x, y, z);

                    Block block =
                        blockAccessor.GetBlock(sample);

                    if (block.Id == 0)
                    {
                        continue;
                    }

                    if (!HasExposedInspectionFace(
                            blockAccessor,
                            neighbor,
                            x,
                            y,
                            z
                        ))
                    {
                        continue;
                    }

                    surfaces.Add(
                        new BlockPos(
                            x,
                            y,
                            z,
                            center.dimension
                        )
                    );
                }
            }
        }

        return surfaces;
    }

    private static bool HasExposedInspectionFace(
        IBlockAccessor blockAccessor,
        BlockPos neighbor,
        int x,
        int y,
        int z
    )
    {
        foreach (
            (int X, int Y, int Z) offset
            in RoomInspectionNeighborOffsets
        )
        {
            neighbor.Set(
                x + offset.X,
                y + offset.Y,
                z + offset.Z
            );

            Block neighborBlock =
                blockAccessor.GetBlock(neighbor);

            bool openForInspection =
                neighborBlock.Id == 0
                || (
                    !neighborBlock.ForFluidsLayer
                    && (
                        neighborBlock.Replaceable >= 6000
                        || neighborBlock.CollisionBoxes == null
                        || neighborBlock.CollisionBoxes.Length == 0
                    )
                );

            if (openForInspection)
            {
                return true;
            }
        }

        return false;
    }

    private static void ApplyRoomInspectionHighlight(
        ICoreClientAPI api,
        List<BlockPos> blocks,
        int color
    )
    {
        IClientPlayer? player = api.World.Player;

        if (player == null)
        {
            return;
        }

        List<int> colors = new(blocks.Count);

        for (int index = 0; index < blocks.Count; index++)
        {
            colors.Add(color);
        }

        api.World.HighlightBlocks(
            player,
            RoomInspectionHighlightSlot,
            blocks,
            colors,
            EnumHighlightBlocksMode.Absolute,
            EnumHighlightShape.Arbitrary,
            1f
        );

        roomInspectionHighlightVisible =
            blocks.Count > 0;
    }

    private static void ClearRoomInspectionHighlight(
        ICoreClientAPI api
    )
    {
        IClientPlayer? player = api.World.Player;

        if (player == null)
        {
            roomInspectionHighlightVisible = false;
            return;
        }

        api.World.HighlightBlocks(
            player,
            RoomInspectionHighlightSlot,
            new List<BlockPos>(),
            new List<int>()
        );

        roomInspectionHighlightVisible = false;
    }

    private static void ApplyRoomInspectionConfigSaved(
        ICoreClientAPI api
    )
    {
        StillGreenhousesConfig savedConfig =
            StillGreenhousesShared.LoadConfig(
                api,
                storeNormalizedConfig: false
            );

        bool wasEnabled =
            Config?.ShowRoomInspectionOverlay == true;

        StillGreenhousesConfig activeConfig =
            Config ??= savedConfig;

        activeConfig.ShowRoomInspectionOverlay =
            savedConfig.ShowRoomInspectionOverlay;

        bool isEnabled =
            activeConfig.Enabled
            && activeConfig.ShowRoomInspectionOverlay;

        if (!isEnabled)
        {
            ClearRoomInspectionHighlight(api);
            roomInspectionLastPlayerCell = null;
            roomInspectionCurrentRegion = null;
            roomInspectionRefreshPending = false;
            return;
        }

        if (!wasEnabled)
        {
            roomInspectionServerDenied = false;
            roomInspectionDisabledMessageShown = false;
            roomInspectionLastPlayerCell = null;
            roomInspectionCurrentRegion = null;
            nextRoomInspectionRequestAtMs = 0;
        }

        QueueRoomInspectionRefresh();
    }
}
