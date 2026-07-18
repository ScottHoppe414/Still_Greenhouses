/*
version 0.18.0
*/

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Client.Tesselation;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace StillGreenhouses;

internal static class StillGreenhousesHarmonyMethodIdentity
{
    internal static bool AreSame(
        MethodBase left,
        MethodBase right
    )
    {
        try
        {
            return left.Module.Equals(right.Module)
                   && left.MetadataToken == right.MetadataToken;
        }
        catch
        {
            return left.Equals(right);
        }
    }
}

[HarmonyPatch]
internal static partial class JsonVegetationWindPatch
{
    private static readonly Dictionary<Type, MethodBase>
        EffectiveMethodByRuntimeType = new();

    private static IEnumerable<MethodBase> TargetMethods()
    {
        Type meshRef = typeof(MeshData).MakeByRefType();
        Type lightRef = typeof(int[]).MakeByRefType();

        Type[] signature =
        [
            meshRef,
            lightRef,
            typeof(BlockPos),
            typeof(Block[]),
            typeof(int)
        ];

        HashSet<MethodBase> targets = new();

        EffectiveMethodByRuntimeType.Clear();

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (Type type in GetLoadableTypes(assembly))
            {
                if (
                    type.IsAbstract
                    || !typeof(Block).IsAssignableFrom(type)
                )
                {
                    continue;
                }

                MethodInfo? method;

                try
                {
                    method = type.GetMethod(
                        nameof(Block.OnJsonTesselation),
                        BindingFlags.Instance
                            | BindingFlags.Public
                            | BindingFlags.NonPublic,
                        binder: null,
                        types: signature,
                        modifiers: null
                    );
                }
                catch
                {
                    continue;
                }

                if (
                    method == null
                    || method.IsAbstract
                )
                {
                    continue;
                }

                EffectiveMethodByRuntimeType[type] = method;
                targets.Add(method);
            }
        }

        StillGreenhousesClientSystem.Capi?.Logger.Notification(
            $"[StillGreenhouses] Broad JSON vegetation wind patch discovered " +
            $"{targets.Count} effective OnJsonTesselation implementations."
        );

        return targets;
    }

    [HarmonyPostfix]
    private static void Postfix(
        Block __instance,
        ref MeshData __0,
        BlockPos __2,
        MethodBase __originalMethod
    )
    {
        try
        {
            // BlockReeds/tallplant-* uses a dedicated BlockPlant postfix below.
            // The generalized patch may also be attached to BlockPlant, so it
            // must explicitly skip these runtime blocks to avoid double work.
            if (
                __instance is BlockReeds
                || (
                    __instance.Code?.Path?.StartsWith(
                        "tallplant-",
                        StringComparison.Ordinal
                    ) == true
                )
            )
            {
                return;
            }

            // Some overrides call their base OnJsonTesselation implementation.
            // Only the most-derived method used by this runtime block is allowed
            // to process the mesh, preventing duplicate wind modification.
            if (
                !EffectiveMethodByRuntimeType.TryGetValue(
                    __instance.GetType(),
                    out MethodBase? effectiveMethod
                )
                || !StillGreenhousesHarmonyMethodIdentity.AreSame(
                    effectiveMethod,
                    __originalMethod
                )
            )
            {
                return;
            }

            string source =
                $"{__originalMethod.DeclaringType?.FullName ?? "<unknown>"}" +
                $".{__originalMethod.Name}";

            StillGreenhousesWindPatchHelper.ProcessVegetationMesh(
                source,
                __instance,
                ref __0,
                __2,
                allowXRunCompaction: true
            );
        }
        catch (Exception e)
        {
            StillGreenhousesClientSystem.LogPatchFailure(
                "JSON OnJsonTesselation",
                __2,
                e
            );
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(
        Assembly assembly
    )
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types
                .Where(type => type != null)
                .Cast<Type>();
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }
}


[HarmonyPatch]
internal static class BlockEntityContainedVegetationWindPatch
{
    private static readonly Dictionary<Type, MethodBase>
        EffectiveMethodByRuntimeType = new();

    private static IEnumerable<MethodBase> TargetMethods()
    {
        Type[] signature =
        [
            typeof(ITerrainMeshPool),
            typeof(ITesselatorAPI)
        ];

        HashSet<MethodBase> targets = new();
        EffectiveMethodByRuntimeType.Clear();

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (Type type in GetLoadableTypes(assembly))
            {
                if (
                    type.IsAbstract
                    || !typeof(BlockEntity).IsAssignableFrom(type)
                    || typeof(BlockEntityPlantContainer)
                        .IsAssignableFrom(type)
                )
                {
                    continue;
                }

                MethodInfo? method;

                try
                {
                    method = type.GetMethod(
                        nameof(BlockEntity.OnTesselation),
                        BindingFlags.Instance
                            | BindingFlags.Public
                            | BindingFlags.NonPublic,
                        binder: null,
                        types: signature,
                        modifiers: null
                    );
                }
                catch
                {
                    continue;
                }

                if (method == null || method.IsAbstract)
                {
                    continue;
                }

                EffectiveMethodByRuntimeType[type] = method;
                targets.Add(method);
            }
        }

        StillGreenhousesClientSystem.Capi?.Logger.Notification(
            "[StillGreenhouses] Contained vegetation mesh-pool patch discovered "
            + $"{targets.Count} effective BlockEntity.OnTesselation implementations."
        );

        return targets;
    }

    [HarmonyPrefix]
    private static void Prefix(
        BlockEntity __instance,
        ref ITerrainMeshPool __0,
        MethodBase __originalMethod
    )
    {
        try
        {
            if (
                !EffectiveMethodByRuntimeType.TryGetValue(
                    __instance.GetType(),
                    out MethodBase? effectiveMethod
                )
                || !StillGreenhousesHarmonyMethodIdentity.AreSame(
                    effectiveMethod,
                    __originalMethod
                )
            )
            {
                return;
            }

            StillGreenhousesConfig? config =
                StillGreenhousesClientSystem.Config;

            Block? containerBlock = __instance.Block;

            if (
                config?.ApplyToOtherVegetation != true
                || !StillGreenhousesClientSystem.VisualEffectEnabled
                || containerBlock == null
                || StillGreenhousesShared.IsVegetationCandidate(
                    containerBlock,
                    config
                )
                || !StillGreenhousesClientSystem.TryGetCachedWindMeshRoom(
                    __instance.Pos,
                    requestIfUnknown: false,
                    out GreenhouseRegion? room
                )
            )
            {
                return;
            }

            string source =
                $"{__originalMethod.DeclaringType?.FullName ?? "<unknown>"}"
                + $".{__originalMethod.Name}";

            __0 = new ContainedVegetationRoomWindMeshPool(
                __0,
                __instance,
                room,
                source
            );
        }
        catch (Exception e)
        {
            StillGreenhousesClientSystem.LogPatchFailure(
                "BlockEntity.OnTesselation contained vegetation adapter",
                __instance.Pos,
                e
            );
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(
        Assembly assembly
    )
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types
                .Where(type => type != null)
                .Cast<Type>();
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    private sealed class ContainedVegetationRoomWindMeshPool :
        ITerrainMeshPool
    {
        private readonly ITerrainMeshPool inner;
        private readonly BlockEntity blockEntity;
        private readonly GreenhouseRegion room;
        private readonly string source;

        internal ContainedVegetationRoomWindMeshPool(
            ITerrainMeshPool inner,
            BlockEntity blockEntity,
            GreenhouseRegion room,
            string source
        )
        {
            this.inner = inner;
            this.blockEntity = blockEntity;
            this.room = room;
            this.source = source;
        }

        public void AddMeshData(
            MeshData data,
            int lodLevel = 1
        )
        {
            MeshData forwarded =
                PrepareMesh(
                    data,
                    transform: null
                );

            inner.AddMeshData(
                forwarded,
                lodLevel
            );
        }

        public void AddMeshData(
            MeshData data,
            float[] tfMatrix,
            int lodLevel = 1
        )
        {
            MeshData forwarded =
                PrepareMesh(
                    data,
                    tfMatrix
                );

            inner.AddMeshData(
                forwarded,
                tfMatrix,
                lodLevel
            );
        }

        public void AddMeshData(
            MeshData data,
            ColorMapData colorMapData,
            int lodLevel = 1
        )
        {
            MeshData forwarded =
                PrepareMesh(
                    data,
                    transform: null
                );

            inner.AddMeshData(
                forwarded,
                colorMapData,
                lodLevel
            );
        }

        private MeshData PrepareMesh(
            MeshData data,
            float[]? transform
        )
        {
            if (
                data == null
                || !StillGreenhousesClientSystem.HasActiveWindMode(data)
            )
            {
                return data!;
            }

            StillGreenhousesClientSystem
                .ObserveVegetation(blockEntity.Pos);

            MeshData measurementMesh = data;

            if (transform != null)
            {
                measurementMesh = data.Clone();
                measurementMesh.MatrixTransform(transform);
            }

            if (!StillGreenhousesRoomWindUniformRenderer
                    .TryMeasureWindVertexEnvelope(
                        measurementMesh,
                        out _,
                        out ManagedRoomWindEnvelope windEnvelope,
                        out _,
                        out _
                    ))
            {
                return data;
            }

            StillGreenhousesRoomWindUniformRenderer
                .RegisterPosition(
                    blockEntity.Pos,
                    room,
                    windEnvelope,
                    allowRoomAbove: true
                );

            StillGreenhousesClientSystem
                .LogContainerWindTargetOnce(
                    source,
                    blockEntity,
                    room,
                    measurementMesh,
                    windEnvelope,
                    transformedByMeshPoolMatrix:
                        transform != null
                );

            return data;
        }
    }
}


[HarmonyPatch(
    typeof(BlockEntityPlantContainer),
    nameof(BlockEntityPlantContainer.OnTesselation)
)]
internal static class PlantContainerRoomWindPatch
{
    private const string PatchSource =
        "BlockEntityPlantContainer.OnTesselation[RoomWind]";

    private static readonly MethodInfo? GenerateMeshesMethod =
        AccessTools.Method(
            typeof(BlockEntityPlantContainer),
            "genMeshes",
            Type.EmptyTypes
        );

    private static readonly FieldInfo? PotMeshField =
        AccessTools.Field(
            typeof(BlockEntityPlantContainer),
            "potMesh"
        );

    private static readonly FieldInfo? ContentMeshField =
        AccessTools.Field(
            typeof(BlockEntityPlantContainer),
            "contentMesh"
        );

    private static int accessFailureLogged;

    [HarmonyPrefix]
    private static bool Prefix(
        BlockEntityPlantContainer __instance,
        ITerrainMeshPool __0,
        ref bool __result
    )
    {
        BlockPos pos = __instance.Pos;

        try
        {
            StillGreenhousesConfig? config =
                StillGreenhousesClientSystem.Config;

            if (
                config?.ApplyToOtherVegetation != true
                || !StillGreenhousesClientSystem.VisualEffectEnabled
            )
            {
                return true;
            }

            if (
                GenerateMeshesMethod == null
                || PotMeshField == null
                || ContentMeshField == null
            )
            {
                LogAccessFailureOnce();
                return true;
            }

            MeshData? potMesh =
                PotMeshField.GetValue(__instance) as MeshData;

            if (potMesh == null)
            {
                GenerateMeshesMethod.Invoke(
                    __instance,
                    parameters: null
                );

                potMesh =
                    PotMeshField.GetValue(__instance) as MeshData;
            }

            MeshData? contentMesh =
                ContentMeshField.GetValue(__instance) as MeshData;

            if (
                potMesh == null
                || contentMesh == null
                || !StillGreenhousesClientSystem
                    .HasActiveWindMode(contentMesh)
            )
            {
                return true;
            }

            // Vanilla chooses whether to clear this cached mesh's wind flags
            // from GetDistanceToRainFall(). A nearby door can therefore change
            // the potted plant's mesh even though its managed-room membership
            // did not change. Preserve the untouched cached content mesh and
            // let the same room shader pipeline used by world plants control it.
            StillGreenhousesClientSystem
                .ObserveVegetation(pos);

            if (!StillGreenhousesClientSystem
                    .TryGetCachedWindMeshRoom(
                        pos,
                        requestIfUnknown: true,
                        out GreenhouseRegion? room
                    ))
            {
                return true;
            }

            if (!StillGreenhousesRoomWindUniformRenderer
                    .TryMeasureWindVertexEnvelope(
                        contentMesh,
                        out _,
                        out ManagedRoomWindEnvelope windEnvelope,
                        out _,
                        out _
                    ))
            {
                return true;
            }

            StillGreenhousesRoomWindUniformRenderer
                .RegisterPosition(
                    pos,
                    room,
                    windEnvelope,
                    allowRoomAbove: true
                );

            StillGreenhousesClientSystem
                .LogContainerWindTargetOnce(
                    PatchSource,
                    __instance,
                    room,
                    contentMesh,
                    windEnvelope,
                    transformedByMeshPoolMatrix: false
                );

            __0.AddMeshData(potMesh);
            __0.AddMeshData(contentMesh);

            __result = true;
            return false;
        }
        catch (Exception e)
        {
            StillGreenhousesClientSystem.LogPatchFailure(
                PatchSource,
                pos,
                e
            );

            // Rendering outside the narrowly supported path remains Vanilla.
            return true;
        }
    }

    private static void LogAccessFailureOnce()
    {
        if (Interlocked.Exchange(
                ref accessFailureLogged,
                1
            ) != 0)
        {
            return;
        }

        StillGreenhousesClientSystem.WarningLiteral(
            "[StillGreenhouses] Could not resolve Vanilla's "
            + "BlockEntityPlantContainer potMesh/contentMesh/genMeshes "
            + "members. Potted plants will retain Vanilla rendering."
        );
    }
}


[HarmonyPatch(
    typeof(BlockPlant),
    nameof(BlockPlant.OnJsonTesselation)
)]
internal static class TallPlantRoomWindPatch
{
    [HarmonyPostfix]
    private static void Postfix(
        BlockPlant __instance,
        ref MeshData __0,
        BlockPos __2
    )
    {
        try
        {
            bool isTallPlant =
                __instance is BlockReeds
                || (
                    __instance.Code?.Path?.StartsWith(
                        "tallplant-",
                        StringComparison.Ordinal
                    ) == true
                );

            if (!isTallPlant)
            {
                return;
            }

            StillGreenhousesWindPatchHelper.ProcessVegetationMesh(
                "BlockPlant.OnJsonTesselation[TallPlant]",
                __instance,
                ref __0,
                __2,
                allowXRunCompaction: false
            );
        }
        catch (Exception e)
        {
            StillGreenhousesClientSystem.LogPatchFailure(
                "BlockPlant.OnJsonTesselation[TallPlant]",
                __2,
                e
            );
        }
    }
}

[HarmonyPatch(
    typeof(BEBehaviorFruitingBushMesh),
    nameof(BEBehaviorFruitingBushMesh.OnTesselation)
)]
internal static class FruitingBushMeshWindPatch
{
    private static readonly MethodInfo? EnsureMeshExistsMethod =
        AccessTools.Method(
            typeof(BEBehaviorFruitingBushMesh),
            "ensureMeshExists"
        );

    private static readonly FieldInfo? BushMeshField =
        AccessTools.Field(
            typeof(BEBehaviorFruitingBushMesh),
            "bushMesh"
        );

    private static int accessFailureLogged;

    [HarmonyPrefix]
    private static bool Prefix(
        BEBehaviorFruitingBushMesh __instance,
        ITerrainMeshPool __0,
        ref bool __result
    )
    {
        BlockPos pos = __instance.Pos;

        try
        {
            Block block = __instance.Block;

            if (!StillGreenhousesWindPatchHelper
                    .TryGetVegetationWindTarget(
                        block,
                        pos,
                        out GreenhouseRegion? greenhouse
                    ))
            {
                return true;
            }

            if (EnsureMeshExistsMethod == null || BushMeshField == null)
            {
                LogAccessFailureOnce();
                return true;
            }

            EnsureMeshExistsMethod.Invoke(
                __instance,
                parameters: null
            );

            MeshData? bushMesh =
                BushMeshField.GetValue(__instance) as MeshData;

            if (
                bushMesh == null
                || !StillGreenhousesClientSystem.HasActiveWindMode(
                    bushMesh
                )
            )
            {
                return true;
            }

            StillGreenhousesClientSystem.ObserveRenderPath(
                block,
                "BEBehaviorFruitingBushMesh.cachedBushMesh",
                bushMesh
            );

            if (!StillGreenhousesRoomWindUniformRenderer
                    .TryMeasureWindVertexEnvelope(
                        bushMesh,
                        out _,
                        out ManagedRoomWindEnvelope windVertexEnvelope,
                        out _,
                        out _
                    ))
            {
                StillGreenhousesClientSystem.WarningLiteral(
                    "[StillGreenhouses] ROOM WIND MESH ENVELOPE SKIPPED " +
                    $"source=BEBehaviorFruitingBushMesh.cachedBushMesh; " +
                    $"code={block.Code}; " +
                    $"pos={pos.X},{pos.Y},{pos.Z}; " +
                    "reason=no-finite-wind-vertex-envelope"
                );

                return true;
            }

            StillGreenhousesRoomWindUniformRenderer
                .RegisterPosition(
                    pos,
                    greenhouse,
                    windVertexEnvelope
                );

            MeshData stillMesh =
                StillGreenhousesWindPatchHelper.CloneForRoomWind(
                    bushMesh
                );

            __0.AddMeshData(stillMesh);

            __result = true;

            StillGreenhousesClientSystem.LogWindAdjustment(
                "BEBehaviorFruitingBushMesh.OnTesselation",
                block,
                pos,
                greenhouse.Key,
                bushMesh,
                stillMesh
            );

            return false;
        }
        catch (Exception e)
        {
            StillGreenhousesClientSystem.LogPatchFailure(
                "BEBehaviorFruitingBushMesh.OnTesselation",
                pos,
                e
            );

            // Never allow a visual-only mod to break bush rendering.
            return true;
        }
    }

    private static void LogAccessFailureOnce()
    {
        if (Interlocked.Exchange(
                ref accessFailureLogged,
                1
            ) != 0)
        {
            return;
        }

        StillGreenhousesClientSystem.Capi?.Logger.Warning(
            "[StillGreenhouses] Could not resolve the vanilla " +
            "BEBehaviorFruitingBushMesh bushMesh/ensureMeshExists members. " +
            "Fruiting bushes will fall back to vanilla rendering."
        );
    }
}

internal static partial class StillGreenhousesWindPatchHelper
{
    internal static void ProcessVegetationMesh(
        string patchName,
        Block block,
        ref MeshData sourceMesh,
        BlockPos pos,
        bool allowXRunCompaction
    )
    {
        try
        {
            LogUnclassifiedWindBlockIfNeeded(
                block,
                pos,
                sourceMesh
            );

            if (!StillGreenhousesClientSystem.HasActiveWindMode(sourceMesh))
            {
                return;
            }

            bool normalVegetationTarget =
                TryGetVegetationWindTarget(
                    block,
                    pos,
                    out GreenhouseRegion? greenhouse
                );

            if (
                !normalVegetationTarget
                && !TryGetWindMeshFallbackTarget(
                    block,
                    pos,
                    out greenhouse
                )
            )
            {
                return;
            }

            if (greenhouse == null)
            {
                return;
            }

            StillGreenhousesClientSystem.ObserveRenderPath(
                block,
                patchName,
                sourceMesh
            );

            MeshData originalMesh = sourceMesh;

            if (!StillGreenhousesRoomWindUniformRenderer
                    .TryMeasureWindVertexEnvelope(
                        originalMesh,
                        out _,
                        out ManagedRoomWindEnvelope windVertexEnvelope,
                        out _,
                        out _
                    ))
            {
                StillGreenhousesClientSystem.WarningLiteral(
                    "[StillGreenhouses] ROOM WIND MESH ENVELOPE SKIPPED " +
                    $"source={patchName}; " +
                    $"code={block.Code}; " +
                    $"pos={pos.X},{pos.Y},{pos.Z}; " +
                    "reason=no-finite-wind-vertex-envelope"
                );

                return;
            }

            ManagedRoomWindEnvelope registrationEnvelope =
                ExpandForTerrainRandomization(
                    block,
                    windVertexEnvelope
                );

            StillGreenhousesClientSystem.LogFlowerMeshProcess(
                patchName,
                block,
                pos,
                originalMesh,
                windVertexEnvelope,
                registrationEnvelope
            );

            StillGreenhousesRoomWindUniformRenderer
                .RegisterPosition(
                    pos,
                    greenhouse,
                    registrationEnvelope,
                    allowRoomAbove:
                        !normalVegetationTarget,
                    allowXRunCompaction:
                        normalVegetationTarget
                        && allowXRunCompaction
                );

            MeshData roomWindMesh =
                CloneForRoomWind(
                    originalMesh
                );

            sourceMesh = roomWindMesh;

            StillGreenhousesClientSystem.LogWindAdjustment(
                patchName,
                block,
                pos,
                greenhouse.Key,
                originalMesh,
                roomWindMesh
            );
        }
        catch (Exception e)
        {
            StillGreenhousesClientSystem.LogPatchFailure(
                patchName,
                pos,
                e
            );
        }
    }

    private static void LogUnclassifiedWindBlockIfNeeded(
        Block block,
        BlockPos pos,
        MeshData mesh
    )
    {
        if (
            StillGreenhousesClientSystem.Config
                ?.DebugLogging
                != true
        )
        {
            return;
        }

        if (StillGreenhousesShared.IsVegetationCandidate(
                block,
                StillGreenhousesClientSystem.Config
            ))
        {
            return;
        }

        if (!StillGreenhousesClientSystem.HasActiveWindMode(
                mesh
            ))
        {
            return;
        }

        if (!StillGreenhousesClientSystem.TryGetCachedGreenhouse(
                pos,
                requestIfUnknown: false,
                out GreenhouseRegion? room
            ))
        {
            return;
        }

        StillGreenhousesClientSystem
            .LogUnclassifiedWindBlockOnce(
                block,
                pos,
                room
            );
    }

    internal static bool TryGetVegetationWindTarget(
        Block block,
        BlockPos pos,
        [NotNullWhen(true)]
        out GreenhouseRegion? greenhouse
    )
    {
        greenhouse = null;

        if (!StillGreenhousesShared.IsVegetationCandidate(
                block,
                StillGreenhousesClientSystem.Config
            ))
        {
            return false;
        }

        StillGreenhousesClientSystem.ObserveVegetation(
            pos
        );

        if (!StillGreenhousesClientSystem.TryGetCachedGreenhouse(
                pos,
                out greenhouse
            ))
        {
            return false;
        }

        StillGreenhousesClientSystem.LogGreenhousePolicy(
            pos,
            greenhouse
        );

        return StillGreenhousesClientSystem.VisualEffectEnabled;
    }

    private static bool TryGetWindMeshFallbackTarget(
        Block block,
        BlockPos pos,
        [NotNullWhen(true)]
        out GreenhouseRegion? greenhouse
    )
    {
        greenhouse = null;

        StillGreenhousesConfig? config =
            StillGreenhousesClientSystem.Config;

        if (
            config?.ApplyToOtherVegetation != true
            || StillGreenhousesShared.IsVegetationCandidate(
                block,
                config
            )
        )
        {
            return false;
        }

        StillGreenhousesClientSystem.ObserveVegetation(pos);

        if (!StillGreenhousesClientSystem.TryGetCachedWindMeshRoom(
                pos,
                requestIfUnknown: true,
                out greenhouse
            ))
        {
            return false;
        }

        StillGreenhousesClientSystem.LogGreenhousePolicy(
            pos,
            greenhouse
        );

        StillGreenhousesClientSystem
            .LogWindMeshFallbackTargetOnce(
                block,
                pos,
                greenhouse
            );

        return StillGreenhousesClientSystem.VisualEffectEnabled;
    }

    internal static MeshData CloneForRoomWind(
        MeshData sourceMesh
    )
    {
        // Both VanillaNoWind and VanillaLowWind use spatial room markers and
        // room-local shader state. Preserve every original WindMode/WindData
        // tuple and reuse Vanilla's mesh rather than baking movement into a new
        // copy. VanillaNoWind uploads an explicit exact-zero state.
        return sourceMesh;
    }

    private static ManagedRoomWindEnvelope
        ExpandForTerrainRandomization(
            Block block,
            ManagedRoomWindEnvelope envelope
        )
    {
        float minX = envelope.MinX;
        float minZ = envelope.MinZ;
        float maxX = envelope.MaxX;
        float maxZ = envelope.MaxZ;

        if (block.RandomizeRotations)
        {
            // JsonTesselator invokes OnJsonTesselation before applying its
            // deterministic block rotation around the local block center.
            // Enclose every possible Y rotation so all color-map/texture
            // fragments of a randomized plant resolve the same room state.
            float maxDistanceX = Math.Max(
                Math.Abs(minX - 0.5f),
                Math.Abs(maxX - 0.5f)
            );

            float maxDistanceZ = Math.Max(
                Math.Abs(minZ - 0.5f),
                Math.Abs(maxZ - 0.5f)
            );

            float radius = MathF.Sqrt(
                maxDistanceX * maxDistanceX
                + maxDistanceZ * maxDistanceZ
            );

            minX = 0.5f - radius;
            maxX = 0.5f + radius;
            minZ = 0.5f - radius;
            maxZ = 0.5f + radius;
        }

        if (block.RandomDrawOffset != 0)
        {
            // The terrain tesselator applies the documented sub-block draw
            // offset only after OnJsonTesselation. One third of a block is a
            // conservative bound for either signed hash direction.
            const float randomDrawOffsetMargin = 1f / 3f;

            minX -= randomDrawOffsetMargin;
            maxX += randomDrawOffsetMargin;
            minZ -= randomDrawOffsetMargin;
            maxZ += randomDrawOffsetMargin;
        }

        return new ManagedRoomWindEnvelope(
            minX,
            envelope.MinY,
            minZ,
            maxX,
            envelope.MaxY,
            maxZ
        );
    }

}
