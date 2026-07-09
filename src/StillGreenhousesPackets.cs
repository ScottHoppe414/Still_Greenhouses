/*
version 0.10.16a
*/

using ProtoBuf;

namespace StillGreenhouses;

internal static class StillGreenhousesNetwork
{
    internal const string ChannelName = "stillgreenhouses-cache-v4";
}

[ProtoContract]
internal sealed class GreenhouseChunkRequest
{
    [ProtoMember(1)]
    public int ChunkX;

    [ProtoMember(2)]
    public int ChunkY;

    [ProtoMember(3)]
    public int ChunkZ;

    [ProtoMember(4)]
    public int Dimension;
}

[ProtoContract]
internal sealed class GreenhouseChunkBatchRequest
{
    [ProtoMember(1)]
    public List<GreenhouseChunkRequest> Chunks = new();
}

[ProtoContract]
internal sealed class GreenhouseChunkSnapshot
{
    [ProtoMember(1)]
    public int ChunkX;

    [ProtoMember(2)]
    public int ChunkY;

    [ProtoMember(3)]
    public int ChunkZ;

    [ProtoMember(4)]
    public int Dimension;

    [ProtoMember(5)]
    public long Revision;

    [ProtoMember(6)]
    public bool Complete;

    [ProtoMember(7)]
    public ulong ContentHash;

    [ProtoMember(8)]
    public List<GreenhouseRegionPacket> Greenhouses = new();

    // Changes only when greenhouse wind classification changes for at least
    // one vegetation position in this chunk. Region geometry/content changes
    // may advance Revision without advancing VisualRevision.
    [ProtoMember(9)]
    public long VisualRevision;
}

[ProtoContract]
internal sealed class GreenhouseRegionPacket
{
    [ProtoMember(1)]
    public int Dimension;

    [ProtoMember(2)]
    public int X1;

    [ProtoMember(3)]
    public int Y1;

    [ProtoMember(4)]
    public int Z1;

    [ProtoMember(5)]
    public int X2;

    [ProtoMember(6)]
    public int Y2;

    [ProtoMember(7)]
    public int Z2;

    [ProtoMember(8)]
    public ulong ShapeHash;

    [ProtoMember(9)]
    public byte[] PosInRoom = Array.Empty<byte>();

    [ProtoMember(10)]
    public int RoomType;
}
