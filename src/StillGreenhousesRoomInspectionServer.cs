/*
version 0.18.0
*/

using System;
using System.Collections.Concurrent;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace StillGreenhouses;

public sealed partial class StillGreenhousesServerSystem
{
    private const int RoomInspectionRequestCooldownMs = 250;

    private readonly ConcurrentDictionary<string, long>
        RoomInspectionLastRequestMs = new();

    private void OnRoomInspectionRequest(
        IServerPlayer fromPlayer,
        RoomInspectionRequest packet
    )
    {
        ICoreServerAPI? api = sapi;

        if (api == null)
        {
            return;
        }

        string playerUid = fromPlayer.PlayerUID;
        long nowMs = Environment.TickCount64;

        if (
            RoomInspectionLastRequestMs.TryGetValue(
                playerUid,
                out long previousMs
            )
            && nowMs - previousMs
                < RoomInspectionRequestCooldownMs
        )
        {
            return;
        }

        RoomInspectionLastRequestMs[playerUid] = nowMs;

        long requestId = packet.RequestId;

        api.Event.EnqueueMainThreadTask(
            () =>
            {
                IServerPlayer? player =
                    api.World.PlayerByUid(playerUid)
                        as IServerPlayer;

                if (player?.Entity == null)
                {
                    return;
                }

                BuildAndSendRoomInspectionResponse(
                    player,
                    requestId
                );
            },
            "stillgreenhouses-room-inspection-request"
        );
    }

    private void BuildAndSendRoomInspectionResponse(
        IServerPlayer player,
        long requestId
    )
    {
        ICoreServerAPI? api = sapi;
        StillGreenhousesConfig? currentConfig = config;

        if (
            api == null
            || currentConfig == null
        )
        {
            return;
        }

        BlockPos playerPos =
            new BlockPos(
                player.Entity.Pos.Dimension
            )
            .Set(player.Entity.Pos);

        if (
            !currentConfig.Enabled
            || !currentConfig.AllowClientRoomInspectionOverlay
        )
        {
            SendRoomInspectionResponse(
                player,
                CreateRoomInspectionResponse(
                    requestId,
                    playerPos,
                    currentConfig.RoomInspectionFailureRadius,
                    RoomInspectionResultKind.DisabledByServer,
                    RoomInspectionFailureReason.DisabledByServer
                )
            );

            return;
        }

        RoomRegistry? registry = roomRegistry;

        if (registry == null)
        {
            SendRoomInspectionResponse(
                player,
                CreateRoomInspectionResponse(
                    requestId,
                    playerPos,
                    currentConfig.RoomInspectionFailureRadius,
                    RoomInspectionResultKind.Incomplete,
                    RoomInspectionFailureReason.ChunkDataIncomplete
                )
            );

            return;
        }

        Room? room =
            registry.GetRoomForPosition(
                playerPos
            );

        if (room == null)
        {
            SendRoomInspectionResponse(
                player,
                CreateRoomInspectionResponse(
                    requestId,
                    playerPos,
                    currentConfig.RoomInspectionFailureRadius,
                    RoomInspectionResultKind.NoVanillaRoom,
                    RoomInspectionFailureReason.VanillaInvalid
                )
            );

            return;
        }

        if (room.AnyChunkUnloaded > 0)
        {
            SendRoomInspectionResponse(
                player,
                CreateRoomInspectionResponse(
                    requestId,
                    playerPos,
                    currentConfig.RoomInspectionFailureRadius,
                    RoomInspectionResultKind.Incomplete,
                    RoomInspectionFailureReason.ChunkDataIncomplete
                )
            );

            return;
        }

        if (!StillGreenhousesShared.TryClassifyRoom(
                room,
                out ManagedRoomType roomType
            ))
        {
            SendRoomInspectionResponse(
                player,
                CreateRoomInspectionResponse(
                    requestId,
                    playerPos,
                    currentConfig.RoomInspectionFailureRadius,
                    RoomInspectionResultKind.NoVanillaRoom,
                    RoomInspectionFailureReason.VanillaInvalid
                )
            );

            return;
        }

        GreenhouseRegion region =
            GreenhouseRegion.FromRoom(
                room,
                playerPos.dimension,
                roomType
            );

        ManagedRoomViability viability =
            EvaluateManagedRoomViability(
                api,
                region,
                countViabilityCheck: false
            );

        RoomInspectionResultKind resultKind =
            viability == ManagedRoomViability.Viable
                ? RoomInspectionResultKind.ManagedRoom
                : RoomInspectionResultKind.VanillaRoomOnly;

        RoomInspectionFailureReason failureReason =
            viability switch
            {
                ManagedRoomViability.TooSmall =>
                    RoomInspectionFailureReason.TooSmall,

                ManagedRoomViability.NoDiscoveryAnchor =>
                    RoomInspectionFailureReason.NoDiscoveryAnchor,

                _ =>
                    RoomInspectionFailureReason.None
            };

        RoomInspectionResponse response =
            CreateRoomInspectionResponse(
                requestId,
                playerPos,
                currentConfig.RoomInspectionFailureRadius,
                resultKind,
                failureReason
            );

        response.RoomType = (int)roomType;
        response.Region = region.ToPacket();

        SendRoomInspectionResponse(
            player,
            response
        );
    }

    private static RoomInspectionResponse
        CreateRoomInspectionResponse(
            long requestId,
            BlockPos center,
            int failureRadius,
            RoomInspectionResultKind resultKind,
            RoomInspectionFailureReason failureReason
        ) =>
            new()
            {
                RequestId = requestId,
                ResultKind = (int)resultKind,
                FailureReason = (int)failureReason,
                CenterX = center.X,
                CenterY = center.Y,
                CenterZ = center.Z,
                Dimension = center.dimension,
                FailureRadius = Math.Clamp(
                    failureRadius,
                    4,
                    24
                )
            };

    private void SendRoomInspectionResponse(
        IServerPlayer player,
        RoomInspectionResponse response
    )
    {
        serverChannel?.SendPacket(
            response,
            player
        );
    }

    private void ClearRoomInspectionPlayerState(
        string playerUid
    )
    {
        RoomInspectionLastRequestMs.TryRemove(
            playerUid,
            out _
        );
    }
}
