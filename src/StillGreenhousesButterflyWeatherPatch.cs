/*
version 0.18.0
*/

using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace StillGreenhouses;

internal static class StillGreenhousesButterflyWeatherPatch
{
    private const double CalmRoomWindSpeed = 0.05d;
    private const float ManagedRoomWanderDurationMultiplier = 12.0f;
    private const float ManagedRoomMinimumWanderSeconds = 10.0f;
    private const float ManagedRoomMaximumWanderSeconds = 15.0f;

    private static readonly object ApplyGate = new();

    private static readonly FieldInfo EntityField =
        AccessTools.Field(
            typeof(AiTaskBase),
            "entity"
        )
        ?? throw new MissingFieldException(
            typeof(AiTaskBase).FullName,
            "entity"
        );

    private static readonly MethodInfo WindSpeedMethod =
        AccessTools.Method(
            typeof(WeatherDataReader),
            nameof(WeatherDataReader.GetWindSpeed),
            [typeof(Vec3d)]
        )
        ?? throw new MissingMethodException(
            typeof(WeatherDataReader).FullName,
            nameof(WeatherDataReader.GetWindSpeed)
        );

    private static readonly MethodInfo PrecipitationMethod =
        AccessTools.Method(
            typeof(WeatherSystemBase),
            nameof(WeatherSystemBase.GetPrecipitation),
            [typeof(Vec3d)]
        )
        ?? throw new MissingMethodException(
            typeof(WeatherSystemBase).FullName,
            nameof(WeatherSystemBase.GetPrecipitation)
        );

    private static readonly MethodInfo ResolveWindSpeedMethod =
        AccessTools.Method(
            typeof(StillGreenhousesButterflyWeatherPatch),
            nameof(ResolveWindSpeed)
        )!;

    private static readonly MethodInfo ResolvePrecipitationMethod =
        AccessTools.Method(
            typeof(StillGreenhousesButterflyWeatherPatch),
            nameof(ResolvePrecipitation)
        )!;

    private static readonly MethodInfo TranspilerMethod =
        AccessTools.Method(
            typeof(StillGreenhousesButterflyWeatherPatch),
            nameof(Transpiler)
        )!;

    private static readonly MethodInfo WanderStartPostfixMethod =
        AccessTools.Method(
            typeof(StillGreenhousesButterflyWeatherPatch),
            nameof(WanderStartPostfix)
        )!;

    private static readonly AccessTools.FieldRef<
        AiTaskButterflyWander,
        float
    > WanderDurationRef =
        AccessTools.FieldRefAccess<
            AiTaskButterflyWander,
            float
        >("wanderDuration");

    internal static void Apply(
        Harmony harmony
    )
    {
        lock (ApplyGate)
        {
            HarmonyMethod transpiler = new(
                TranspilerMethod
            );

            foreach (
                string methodName
                in new[]
                {
                    nameof(AiTaskButterflyRest.ShouldExecute),
                    nameof(AiTaskButterflyRest.ContinueExecute)
                }
            )
            {
                MethodInfo original =
                    AccessTools.Method(
                        typeof(AiTaskButterflyRest),
                        methodName
                    )
                    ?? throw new MissingMethodException(
                        typeof(AiTaskButterflyRest).FullName,
                        methodName
                    );

                if (HasOwnedTranspiler(
                        original,
                        harmony.Id
                    ))
                {
                    continue;
                }

                harmony.Patch(
                    original,
                    transpiler: transpiler
                );
            }

            MethodInfo wanderStart =
                AccessTools.Method(
                    typeof(AiTaskButterflyWander),
                    nameof(AiTaskButterflyWander.StartExecute)
                )
                ?? throw new MissingMethodException(
                    typeof(AiTaskButterflyWander).FullName,
                    nameof(AiTaskButterflyWander.StartExecute)
                );

            if (!HasOwnedWanderPostfix(
                    wanderStart,
                    harmony.Id
                ))
            {
                harmony.Patch(
                    wanderStart,
                    postfix: new HarmonyMethod(
                        WanderStartPostfixMethod
                    )
                );
            }
        }
    }

    private static bool HasOwnedTranspiler(
        MethodBase original,
        string harmonyId
    )
    {
        Patches? patches =
            Harmony.GetPatchInfo(original);

        return patches != null
               && patches.Transpilers.Any(patch =>
                   patch.owner == harmonyId
                   && patch.PatchMethod == TranspilerMethod
               );
    }

    private static bool HasOwnedWanderPostfix(
        MethodBase original,
        string harmonyId
    )
    {
        Patches? patches =
            Harmony.GetPatchInfo(original);

        return patches != null
               && patches.Postfixes.Any(patch =>
                   patch.owner == harmonyId
                   && patch.PatchMethod
                      == WanderStartPostfixMethod
               );
    }

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase original
    )
    {
        List<CodeInstruction> patched = new();
        int windReplacements = 0;
        int precipitationReplacements = 0;

        foreach (CodeInstruction instruction in instructions)
        {
            MethodInfo? replacement = null;

            if (instruction.Calls(WindSpeedMethod))
            {
                replacement = ResolveWindSpeedMethod;
                windReplacements++;
            }
            else if (instruction.Calls(PrecipitationMethod))
            {
                replacement = ResolvePrecipitationMethod;
                precipitationReplacements++;
            }

            if (replacement == null)
            {
                patched.Add(instruction);
                continue;
            }

            CodeInstruction loadTask = new(
                OpCodes.Ldarg_0
            );

            loadTask.labels.AddRange(
                instruction.labels
            );

            instruction.labels.Clear();

            patched.Add(loadTask);

            instruction.opcode = OpCodes.Call;
            instruction.operand = replacement;
            patched.Add(instruction);
        }

        if (
            windReplacements != 1
            || precipitationReplacements != 1
        )
        {
            throw new InvalidOperationException(
                "Unexpected butterfly weather call topology in " +
                $"{original.DeclaringType?.FullName}.{original.Name}: " +
                $"windCalls={windReplacements}; " +
                $"precipitationCalls={precipitationReplacements}."
            );
        }

        return patched;
    }

    private static double ResolveWindSpeed(
        WeatherDataReader weather,
        Vec3d position,
        AiTaskButterflyRest task
    ) =>
        IsInsideManagedRoom(task, position)
            ? CalmRoomWindSpeed
            : weather.GetWindSpeed(position);

    private static float ResolvePrecipitation(
        WeatherSystemBase weather,
        Vec3d position,
        AiTaskButterflyRest task
    ) =>
        IsInsideManagedRoom(task, position)
            ? 0f
            : weather.GetPrecipitation(position);

    private static void WanderStartPostfix(
        AiTaskButterflyWander __instance
    )
    {
        // Chase and flee inherit this method but manage their own duration and
        // target state. Only extend the ordinary lowest-priority wander task.
        if (__instance.GetType() != typeof(AiTaskButterflyWander))
        {
            return;
        }

        EntityAgent? entity =
            EntityField.GetValue(__instance)
                as EntityAgent;

        if (
            entity == null
            || !StillGreenhousesServerSystem
                .TryResolveShelteredButterflyRoom(
                    entity,
                    entity.Pos.XYZ,
                    countAsWeatherLookup: false
                )
        )
        {
            return;
        }

        ref float wanderDuration =
            ref WanderDurationRef(__instance);

        wanderDuration = Math.Clamp(
            wanderDuration
            * ManagedRoomWanderDurationMultiplier,
            ManagedRoomMinimumWanderSeconds,
            ManagedRoomMaximumWanderSeconds
        );
    }

    private static bool IsInsideManagedRoom(
        AiTaskBase task,
        Vec3d position
    )
    {
        EntityAgent? entity =
            EntityField.GetValue(task)
                as EntityAgent;

        return entity != null
               && StillGreenhousesServerSystem
                   .TryResolveShelteredButterflyRoom(
                       entity,
                       position
                   );
    }
}
