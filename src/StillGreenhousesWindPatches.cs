/*
version 0.10.16a
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
                || !effectiveMethod.Equals(__originalMethod)
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
                __2
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
                __2
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

            StillGreenhousesRoomWindUniformRenderer
                .RegisterPosition(
                    pos,
                    greenhouse
                );

            MeshData stillMesh =
                StillGreenhousesWindPatchHelper.CloneForRoomWind(
                    bushMesh,
                    greenhouse
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
        BlockPos pos
    )
    {
        try
        {
            LogUnclassifiedWindBlockIfNeeded(
                block,
                pos,
                sourceMesh
            );

            if (!TryGetVegetationWindTarget(
                    block,
                    pos,
                    out GreenhouseRegion? greenhouse
                ))
            {
                return;
            }

            if (!StillGreenhousesClientSystem.HasActiveWindMode(sourceMesh))
            {
                return;
            }

            StillGreenhousesClientSystem.ObserveRenderPath(
                block,
                patchName,
                sourceMesh
            );

            MeshData originalMesh = sourceMesh;

            StillGreenhousesRoomWindUniformRenderer
                .RegisterPosition(
                    pos,
                    greenhouse
                );

            MeshData roomWindMesh =
                CloneForRoomWind(
                    originalMesh,
                    greenhouse
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

    internal static MeshData CloneForRoomWind(
        MeshData sourceMesh,
        GreenhouseRegion room
    )
    {
        RoomPlantMovementMode movementMode =
            StillGreenhousesClientSystem
                .PlantMovementMode;

        if (
            movementMode
                == RoomPlantMovementMode.NoWind
        )
        {
            return CloneWithoutWind(sourceMesh);
        }

        // VanillaLowWind uses a spatial room marker and room-local shader
        // state. Preserve the complete original WindMode/WindData tuple.
        return sourceMesh.Clone();
    }

    // Exact wind-clearing behavior from the confirmed-working 0.7.1 path.
    internal static MeshData CloneWithoutWind(
        MeshData sourceMesh
    )
    {
        MeshData stillMesh = sourceMesh.Clone();

        if (stillMesh.Flags == null)
        {
            return stillMesh;
        }

        int vertexCount = Math.Min(
            stillMesh.VerticesCount,
            stillMesh.Flags.Length
        );

        for (int i = 0; i < vertexCount; i++)
        {
            stillMesh.Flags[i] &=
                VertexFlags.ClearWindBitsMask;
        }

        stillMesh.HasAnyWindModeSet = false;

        return stillMesh;
    }


}
