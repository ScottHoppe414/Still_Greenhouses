/*
version 0.18.0
*/

using System.Runtime.InteropServices;

namespace StillGreenhouses;

/// <summary>
/// A quarter-block spatial cell used by the room-wind GPU lookup table.
/// World coordinates are quantized with floor(worldCoordinate * 4).
/// </summary>
internal readonly record struct RoomWindCellCoordinate(
    int X,
    int Y,
    int Z
)
{
    internal const int CellsPerBlock = 4;

    internal static RoomWindCellCoordinate FromWorld(
        double x,
        double y,
        double z
    ) =>
        new(
            QuantizeWorldCoordinate(x),
            QuantizeWorldCoordinate(y),
            QuantizeWorldCoordinate(z)
        );

    internal static int QuantizeWorldCoordinate(double coordinate)
    {
        double quantized = Math.Floor(coordinate * CellsPerBlock);

        if (quantized < int.MinValue || quantized > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(coordinate),
                coordinate,
                "The quarter-block coordinate does not fit in Int32."
            );
        }

        return (int)quantized;
    }
}

/// <summary>
/// The shader payload for one spatial cell. Each target occupies two bits:
/// zero means absent and values 1..3 encode room state indices 0..2.
/// </summary>
internal readonly record struct RoomWindCellPayload
{
    internal const int StateCount = 3;
    internal const byte TargetMask = 0b0000_0011;
    internal const int WaterShift = 2;

    internal RoomWindCellPayload(byte packedValue)
    {
        if ((packedValue & 0b1111_0000) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(packedValue),
                packedValue,
                "Only the low four room-wind payload bits may be set."
            );
        }

        PackedValue = packedValue;
    }

    internal byte PackedValue { get; }

    internal bool HasVegetation => VegetationCode != 0;

    internal bool HasWater => WaterCode != 0;

    internal int VegetationStateIndex => VegetationCode - 1;

    internal int WaterStateIndex => WaterCode - 1;

    private int VegetationCode => PackedValue & TargetMask;

    private int WaterCode =>
        (PackedValue >> WaterShift) & TargetMask;

    internal static RoomWindCellPayload FromStateIndices(
        int? vegetationStateIndex,
        int? waterStateIndex
    )
    {
        byte vegetationCode = EncodeState(
            vegetationStateIndex,
            nameof(vegetationStateIndex)
        );

        byte waterCode = EncodeState(
            waterStateIndex,
            nameof(waterStateIndex)
        );

        return new RoomWindCellPayload(
            (byte)(vegetationCode | (waterCode << WaterShift))
        );
    }

    private static byte EncodeState(
        int? stateIndex,
        string parameterName
    )
    {
        if (!stateIndex.HasValue)
        {
            return 0;
        }

        if ((uint)stateIndex.Value >= StateCount)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                stateIndex,
                $"Room-wind state indices must be between 0 and " +
                $"{StateCount - 1}."
            );
        }

        return (byte)(stateIndex.Value + 1);
    }
}

/// <summary>
/// One target contribution to a spatial cell. A null state means that target
/// does not contribute to the cell.
/// </summary>
internal readonly record struct RoomWindCellContribution(
    RoomWindCellCoordinate Coordinate,
    int? VegetationStateIndex,
    int? WaterStateIndex
)
{
    internal static RoomWindCellContribution ForVegetation(
        RoomWindCellCoordinate coordinate,
        int stateIndex
    ) =>
        new(coordinate, stateIndex, null);

    internal static RoomWindCellContribution ForWater(
        RoomWindCellCoordinate coordinate,
        int stateIndex
    ) =>
        new(coordinate, null, stateIndex);

    internal static RoomWindCellContribution ForBoth(
        RoomWindCellCoordinate coordinate,
        int vegetationStateIndex,
        int waterStateIndex
    ) =>
        new(coordinate, vegetationStateIndex, waterStateIndex);
}

/// <summary>
/// Immutable RGBA8 texture snapshot for the room-wind spatial hash.
/// Each table slot consumes two adjacent texels:
/// texel 0 = relative X low/high, relative Y low/high;
/// texel 1 = relative Z low/high, payload, occupied sentinel.
/// Packed Int32 pixels use Vintage Story's logical RGBA representation:
/// B | G&lt;&lt;8 | R&lt;&lt;16 | A&lt;&lt;24.
/// </summary>
internal sealed class RoomWindCellHashSnapshot
{
    internal RoomWindCellHashSnapshot(
        int[] pixels,
        int width,
        int height,
        int capacity,
        uint seed,
        RoomWindCellCoordinate origin,
        int entryCount,
        int vegetationConflictCount,
        int waterConflictCount,
        int maxProbeCountUsed
    )
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        Capacity = capacity;
        Mask = capacity - 1;
        Seed = seed;
        Origin = origin;
        EntryCount = entryCount;
        VegetationConflictCount = vegetationConflictCount;
        WaterConflictCount = waterConflictCount;
        MaxProbeCountUsed = maxProbeCountUsed;
    }

    internal int[] Pixels { get; }

    internal int Width { get; }

    internal int Height { get; }

    internal int Capacity { get; }

    internal int Mask { get; }

    internal uint Seed { get; }

    internal RoomWindCellCoordinate Origin { get; }

    internal int EntryCount { get; }

    internal int VegetationConflictCount { get; }

    internal int WaterConflictCount { get; }

    internal int MaxProbeCountUsed { get; }

    internal bool TryLookup(
        RoomWindCellCoordinate coordinate,
        out RoomWindCellPayload payload
    )
    {
        if (!RoomWindCellHash.TryCreateRelativeCoordinate(
                coordinate,
                Origin,
                out RoomWindCellCoordinate relative
            ))
        {
            payload = default;
            return false;
        }

        uint hash = RoomWindCellHash.HashRelativeCoordinate(
            relative,
            Seed
        );

        for (int probe = 0; probe < RoomWindCellHash.ProbeLimit; probe++)
        {
            int slot = (int)((hash + (uint)probe) & (uint)Mask);
            int firstPixelIndex = slot * RoomWindCellHash.TexelsPerSlot;
            uint keyPixel = unchecked((uint)Pixels[firstPixelIndex]);
            uint valuePixel = unchecked((uint)Pixels[firstPixelIndex + 1]);

            byte occupied = (byte)(valuePixel >> 24);

            if (occupied == 0)
            {
                payload = default;
                return false;
            }

            short storedX = unchecked((short)(
                ((keyPixel >> 16) & 0xffu)
                | (keyPixel & 0xff00u)
            ));

            short storedY = unchecked((short)(
                (keyPixel & 0xffu)
                | ((keyPixel >> 16) & 0xff00u)
            ));

            short storedZ = unchecked((short)(
                ((valuePixel >> 16) & 0xffu)
                | (valuePixel & 0xff00u)
            ));

            if (storedX == relative.X
                && storedY == relative.Y
                && storedZ == relative.Z)
            {
                payload = new RoomWindCellPayload(
                    (byte)valuePixel
                );

                return true;
            }
        }

        payload = default;
        return false;
    }
}

/// <summary>
/// Builds a deterministic, bounded-probe texture hash that can be reproduced
/// exactly in GLSL 3.30 using unsigned 32-bit arithmetic.
/// </summary>
internal static class RoomWindCellHash
{
    internal const int TexelsPerSlot = 2;
    internal const int ProbeLimit = 8;
    internal const int DefaultTextureWidth = 1024;

    private const int MinimumCapacity = 8;
    private const int SeedAttemptsPerCapacity = 8;
    private const uint InitialSeed = 0x6d2b79f5u;
    private const uint SeedStep = 0x9e3779b9u;
    private const byte OccupiedSentinel = 0xff;

    // Keep this source beside HashRelativeCoordinate. The shader implementation
    // must use the same constants, operations, and relative quarter coordinates.
    internal const string GlslHashSource = """
uint stillGreenhousesHashRelativeCell(ivec3 cell, uint seed)
{
    uint hash = seed ^ 0x9e3779b9u;
    hash ^= uint(cell.x) * 0x85ebca6bu;
    hash = (hash << 13u) | (hash >> 19u);
    hash ^= uint(cell.y) * 0xc2b2ae35u;
    hash = (hash << 15u) | (hash >> 17u);
    hash ^= uint(cell.z) * 0x27d4eb2du;
    hash ^= hash >> 16u;
    hash *= 0x7feb352du;
    hash ^= hash >> 15u;
    hash *= 0x846ca68bu;
    hash ^= hash >> 16u;
    return hash;
}
""";

    internal static RoomWindCellHashSnapshot Build(
        IEnumerable<RoomWindCellContribution> contributions,
        int textureWidth = DefaultTextureWidth
    )
    {
        ArgumentNullException.ThrowIfNull(contributions);

        if (!IsPositivePowerOfTwo(textureWidth))
        {
            throw new ArgumentOutOfRangeException(
                nameof(textureWidth),
                textureWidth,
                "The texture width must be a positive power of two."
            );
        }

        Dictionary<RoomWindCellCoordinate, MutablePayload> merged =
            new();

        foreach (RoomWindCellContribution contribution in contributions)
        {
            ValidateOptionalState(
                contribution.VegetationStateIndex,
                nameof(contribution.VegetationStateIndex)
            );

            ValidateOptionalState(
                contribution.WaterStateIndex,
                nameof(contribution.WaterStateIndex)
            );

            if (!contribution.VegetationStateIndex.HasValue
                && !contribution.WaterStateIndex.HasValue)
            {
                continue;
            }

            ref MutablePayload payload = ref CollectionsMarshal
                .GetValueRefOrAddDefault(
                    merged,
                    contribution.Coordinate,
                    out _
                );

            payload.MergeVegetation(
                contribution.VegetationStateIndex
            );

            payload.MergeWater(contribution.WaterStateIndex);
        }

        int vegetationConflictCount = 0;
        int waterConflictCount = 0;

        List<CellEntry> entries = new(merged.Count);

        foreach (KeyValuePair<
                     RoomWindCellCoordinate,
                     MutablePayload
                 > pair in merged)
        {
            if (pair.Value.VegetationConflict)
            {
                vegetationConflictCount++;
            }

            if (pair.Value.WaterConflict)
            {
                waterConflictCount++;
            }

            RoomWindCellPayload packed =
                RoomWindCellPayload.FromStateIndices(
                    pair.Value.VegetationStateIndex,
                    pair.Value.WaterStateIndex
                );

            if (packed.PackedValue != 0)
            {
                entries.Add(new CellEntry(pair.Key, packed));
            }
        }

        entries.Sort(CellEntryComparer.Instance);

        RoomWindCellCoordinate origin = SelectOrigin(entries);

        CellEntry[] relativeEntries = CreateRelativeEntries(
            entries,
            origin
        );

        int capacity = SelectInitialCapacity(relativeEntries.Length);

        while (true)
        {
            CellEntry?[] slots = new CellEntry?[capacity];

            for (int seedAttempt = 0;
                 seedAttempt < SeedAttemptsPerCapacity;
                 seedAttempt++)
            {
                uint seed = unchecked(
                    InitialSeed + (uint)seedAttempt * SeedStep
                );

                if (TryBuildSlots(
                        relativeEntries,
                        slots,
                        seed,
                        out int maxProbeCountUsed
                    ))
                {
                    return CreateSnapshot(
                        slots,
                        textureWidth,
                        capacity,
                        seed,
                        origin,
                        relativeEntries.Length,
                        vegetationConflictCount,
                        waterConflictCount,
                        maxProbeCountUsed
                    );
                }
            }

            if (capacity > (1 << 28))
            {
                throw new InvalidOperationException(
                    "Unable to build the bounded-probe room-wind hash " +
                    "without exceeding the supported table size."
                );
            }

            capacity *= 2;
        }
    }

    internal static uint HashRelativeCoordinate(
        RoomWindCellCoordinate relativeCoordinate,
        uint seed
    )
    {
        unchecked
        {
            uint hash = seed ^ 0x9e3779b9u;
            hash ^= (uint)relativeCoordinate.X * 0x85ebca6bu;
            hash = RotateLeft(hash, 13);
            hash ^= (uint)relativeCoordinate.Y * 0xc2b2ae35u;
            hash = RotateLeft(hash, 15);
            hash ^= (uint)relativeCoordinate.Z * 0x27d4eb2du;
            hash ^= hash >> 16;
            hash *= 0x7feb352du;
            hash ^= hash >> 15;
            hash *= 0x846ca68bu;
            hash ^= hash >> 16;
            return hash;
        }
    }

    internal static bool TryCreateRelativeCoordinate(
        RoomWindCellCoordinate coordinate,
        RoomWindCellCoordinate origin,
        out RoomWindCellCoordinate relativeCoordinate
    )
    {
        long relativeX = (long)coordinate.X - origin.X;
        long relativeY = (long)coordinate.Y - origin.Y;
        long relativeZ = (long)coordinate.Z - origin.Z;

        if (relativeX < short.MinValue || relativeX > short.MaxValue
            || relativeY < short.MinValue || relativeY > short.MaxValue
            || relativeZ < short.MinValue || relativeZ > short.MaxValue)
        {
            relativeCoordinate = default;
            return false;
        }

        relativeCoordinate = new RoomWindCellCoordinate(
            (int)relativeX,
            (int)relativeY,
            (int)relativeZ
        );

        return true;
    }

    private static RoomWindCellHashSnapshot CreateSnapshot(
        CellEntry?[] slots,
        int maximumTextureWidth,
        int capacity,
        uint seed,
        RoomWindCellCoordinate origin,
        int entryCount,
        int vegetationConflictCount,
        int waterConflictCount,
        int maxProbeCountUsed
    )
    {
        int texelCount = checked(capacity * TexelsPerSlot);
        int width = Math.Min(maximumTextureWidth, texelCount);
        int height = texelCount / width;
        int[] pixels = new int[texelCount];

        for (int slot = 0; slot < slots.Length; slot++)
        {
            if (!slots[slot].HasValue)
            {
                continue;
            }

            CellEntry entry = slots[slot]!.Value;
            int firstPixelIndex = slot * TexelsPerSlot;

            pixels[firstPixelIndex] = PackRgba(
                unchecked((byte)entry.Coordinate.X),
                unchecked((byte)(entry.Coordinate.X >> 8)),
                unchecked((byte)entry.Coordinate.Y),
                unchecked((byte)(entry.Coordinate.Y >> 8))
            );

            pixels[firstPixelIndex + 1] = PackRgba(
                unchecked((byte)entry.Coordinate.Z),
                unchecked((byte)(entry.Coordinate.Z >> 8)),
                entry.Payload.PackedValue,
                OccupiedSentinel
            );
        }

        return new RoomWindCellHashSnapshot(
            pixels,
            width,
            height,
            capacity,
            seed,
            origin,
            entryCount,
            vegetationConflictCount,
            waterConflictCount,
            maxProbeCountUsed
        );
    }

    private static bool TryBuildSlots(
        IReadOnlyList<CellEntry> entries,
        CellEntry?[] slots,
        uint seed,
        out int maxProbeCountUsed
    )
    {
        Array.Clear(slots);
        maxProbeCountUsed = 0;
        uint mask = (uint)(slots.Length - 1);

        foreach (CellEntry entry in entries)
        {
            uint hash = HashRelativeCoordinate(
                entry.Coordinate,
                seed
            );

            bool inserted = false;

            for (int probe = 0; probe < ProbeLimit; probe++)
            {
                int slot = (int)((hash + (uint)probe) & mask);

                if (slots[slot].HasValue)
                {
                    continue;
                }

                slots[slot] = entry;
                maxProbeCountUsed = Math.Max(
                    maxProbeCountUsed,
                    probe + 1
                );

                inserted = true;
                break;
            }

            if (!inserted)
            {
                maxProbeCountUsed = 0;
                return false;
            }
        }

        return true;
    }

    private static CellEntry[] CreateRelativeEntries(
        IReadOnlyList<CellEntry> entries,
        RoomWindCellCoordinate origin
    )
    {
        CellEntry[] relativeEntries = new CellEntry[entries.Count];

        for (int index = 0; index < entries.Count; index++)
        {
            CellEntry entry = entries[index];

            if (!TryCreateRelativeCoordinate(
                    entry.Coordinate,
                    origin,
                    out RoomWindCellCoordinate relative
                ))
            {
                throw new InvalidOperationException(
                    "The room-wind cell span exceeds the signed 16-bit " +
                    "relative texture-key range."
                );
            }

            relativeEntries[index] = new CellEntry(
                relative,
                entry.Payload
            );
        }

        return relativeEntries;
    }

    private static RoomWindCellCoordinate SelectOrigin(
        IReadOnlyList<CellEntry> entries
    )
    {
        if (entries.Count == 0)
        {
            return default;
        }

        int minX = entries[0].Coordinate.X;
        int maxX = minX;
        int minY = entries[0].Coordinate.Y;
        int maxY = minY;
        int minZ = entries[0].Coordinate.Z;
        int maxZ = minZ;

        for (int index = 1; index < entries.Count; index++)
        {
            RoomWindCellCoordinate coordinate =
                entries[index].Coordinate;

            minX = Math.Min(minX, coordinate.X);
            maxX = Math.Max(maxX, coordinate.X);
            minY = Math.Min(minY, coordinate.Y);
            maxY = Math.Max(maxY, coordinate.Y);
            minZ = Math.Min(minZ, coordinate.Z);
            maxZ = Math.Max(maxZ, coordinate.Z);
        }

        return new RoomWindCellCoordinate(
            Midpoint(minX, maxX),
            Midpoint(minY, maxY),
            Midpoint(minZ, maxZ)
        );
    }

    private static int SelectInitialCapacity(int entryCount)
    {
        long requiredCapacity = Math.Max(
            MinimumCapacity,
            (long)entryCount * 2
        );

        int capacity = MinimumCapacity;

        while (capacity < requiredCapacity)
        {
            if (capacity > (1 << 28))
            {
                throw new InvalidOperationException(
                    "The room-wind cell count exceeds the supported " +
                    "texture hash capacity."
                );
            }

            capacity *= 2;
        }

        return capacity;
    }

    private static int Midpoint(int minimum, int maximum) =>
        (int)(minimum + (((long)maximum - minimum + 1L) / 2L));

    private static int PackRgba(
        byte red,
        byte green,
        byte blue,
        byte alpha
    ) =>
        unchecked((int)(
            blue
            | ((uint)green << 8)
            | ((uint)red << 16)
            | ((uint)alpha << 24)
        ));

    private static uint RotateLeft(uint value, int count) =>
        (value << count) | (value >> (32 - count));

    private static bool IsPositivePowerOfTwo(int value) =>
        value > 0 && (value & (value - 1)) == 0;

    private static void ValidateOptionalState(
        int? stateIndex,
        string parameterName
    )
    {
        if (stateIndex.HasValue
            && (uint)stateIndex.Value >= RoomWindCellPayload.StateCount)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                stateIndex,
                $"Room-wind state indices must be between 0 and " +
                $"{RoomWindCellPayload.StateCount - 1}."
            );
        }
    }

    private struct MutablePayload
    {
        private int? vegetationStateIndex;
        private int? waterStateIndex;
        private bool vegetationConflict;
        private bool waterConflict;

        internal int? VegetationStateIndex => vegetationStateIndex;

        internal int? WaterStateIndex => waterStateIndex;

        internal bool VegetationConflict => vegetationConflict;

        internal bool WaterConflict => waterConflict;

        internal void MergeVegetation(int? stateIndex)
        {
            MergeTarget(
                stateIndex,
                ref vegetationStateIndex,
                ref vegetationConflict
            );
        }

        internal void MergeWater(int? stateIndex)
        {
            MergeTarget(
                stateIndex,
                ref waterStateIndex,
                ref waterConflict
            );
        }

        private static void MergeTarget(
            int? incomingStateIndex,
            ref int? currentStateIndex,
            ref bool conflict
        )
        {
            if (!incomingStateIndex.HasValue || conflict)
            {
                return;
            }

            if (!currentStateIndex.HasValue)
            {
                currentStateIndex = incomingStateIndex;
                return;
            }

            if (currentStateIndex.Value == incomingStateIndex.Value)
            {
                return;
            }

            currentStateIndex = null;
            conflict = true;
        }
    }

    private readonly record struct CellEntry(
        RoomWindCellCoordinate Coordinate,
        RoomWindCellPayload Payload
    );

    private sealed class CellEntryComparer : IComparer<CellEntry>
    {
        internal static readonly CellEntryComparer Instance = new();

        public int Compare(CellEntry left, CellEntry right)
        {
            int x = left.Coordinate.X.CompareTo(right.Coordinate.X);

            if (x != 0)
            {
                return x;
            }

            int y = left.Coordinate.Y.CompareTo(right.Coordinate.Y);

            if (y != 0)
            {
                return y;
            }

            return left.Coordinate.Z.CompareTo(right.Coordinate.Z);
        }
    }
}
