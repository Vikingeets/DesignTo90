using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace DesignTo90
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.voidchicken.DesignTo90";
        public const string NAME = "DesignTo90";
        public const string VERSION = "0.0.1";

        private Harmony harmony;
        private static ManualLogSource logger;

        private void Awake()
        {
            logger = Logger;
            harmony = new Harmony(GUID);
            harmony.PatchAll(typeof(EnableFullBuild));
            harmony.PatchAll(typeof(NodeBuildLimiter));
            harmony.PatchAll(typeof(FrameLimiter));
            harmony.PatchAll(typeof(ShellLimiter));
            harmony.PatchAll(typeof(NodeLimitReAuto));
            Logger.LogMessage("DesignTo90 patches complete");
        }

        private void OnDestroy()
        {
            harmony.UnpatchSelf();
        }

        private static bool AllowedByLatitiude (DysonNode node)
        {
            if (node == null) return true;
            return Mathf.RoundToInt(Mathf.Asin(Mathf.Clamp01(Mathf.Abs(node.pos.normalized.y))) * Mathf.Rad2Deg) <= Mathf.RoundToInt(GameMain.history.dysonNodeLatitude);
        }

        [HarmonyPatch]
        public static class EnableFullBuild
        {
            private static readonly FieldInfo dysonNodeLatitude = AccessTools.Field(typeof(GameHistoryData), nameof(GameHistoryData.dysonNodeLatitude));

            public static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(UIDysonBrush_Frame), "CheckCondition");
                yield return AccessTools.Method(typeof(UIDysonBrush_Node), "_OnUpdate");
                yield return AccessTools.Method(typeof(DysonBlueprintData), nameof(DysonBlueprintData.CheckLatLimit));
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach (CodeInstruction instruction in instructions)
                {
                    if (instruction.LoadsField(dysonNodeLatitude))
                    {
                        yield return new CodeInstruction(OpCodes.Pop);
                        yield return new CodeInstruction(OpCodes.Ldc_R4, 90f);
                        continue;
                    }
                    else yield return instruction;
                }
            }
        }

        [HarmonyPatch]
        public static class NodeBuildLimiter
        {
            public static MethodBase TargetMethod()
            {
                return AccessTools.PropertyGetter(typeof(DysonNode), nameof(DysonNode.spReqOrder));
            }

            public static void Postfix(ref int __result, DysonNode __instance)
            {
                if (DSPGame.IsMenuDemo) return;
                if (__result <= 0)
                    return;
                if (!AllowedByLatitiude(__instance))
                    __result = 0;
            }
        }

        [HarmonyPatch]
        public static class FrameLimiter
        {
            private static readonly FieldInfo spMax = AccessTools.Field(typeof(DysonFrame), nameof(DysonFrame.spMax));

            private static int CheckSpMax(DysonFrame dysonFrame)
            {
                if (DSPGame.IsMenuDemo) return dysonFrame.spMax;
                if (!AllowedByLatitiude(dysonFrame.nodeA) || !AllowedByLatitiude(dysonFrame.nodeB)) return 0;
                else return dysonFrame.spMax;
            }

            public static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(DysonNode), nameof(DysonNode.ConstructSp));
                yield return AccessTools.Method(typeof(DysonNode), nameof(DysonNode.RecalcSpReq));
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach (CodeInstruction instruction in instructions)
                {
                    if (instruction.LoadsField(spMax))
                        yield return CodeInstruction.Call(typeof(FrameLimiter), nameof(CheckSpMax));
                    else yield return instruction;
                }
            }
        }

        [HarmonyPatch]
        public static class ShellLimiter
        {
            private static readonly FieldInfo cpPerVertex = AccessTools.Field(typeof(DysonShell), nameof(DysonShell.cpPerVertex));

            private static int CheckCpPerVertex(DysonShell dysonShell)
            {
                if (DSPGame.IsMenuDemo) return dysonShell.cpPerVertex;
                List<DysonNode> nodes = dysonShell.nodes;
                if (nodes?.Any(node => !AllowedByLatitiude(node)) ?? false) return 0;
                else return dysonShell.cpPerVertex;
            }

            public static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(DysonNode), nameof(DysonNode.ConstructCp));
                // theoretically, this could cause the needed cp to go negative
                // but that shouldn't be possible unless there are already sails somewhere they shouldn't be
                // and the game should be able to gracefully handle the scenario regardless
                yield return AccessTools.Method(typeof(DysonNode), nameof(DysonNode.RecalcCpReq));
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach (CodeInstruction instruction in instructions)
                {
                    if (instruction.LoadsField(cpPerVertex))
                        yield return CodeInstruction.Call(typeof(ShellLimiter), nameof(CheckCpPerVertex));
                    else yield return instruction;
                }
            }
        }

        [HarmonyPatch]
        public static class NodeLimitReAuto
        {
            public static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(GameHistoryData), nameof(GameHistoryData.UnlockTechFunction));
            }

            public static void Postfix(int func)
            {
                if (func != 26) return; // this a const in the code anywhere?
                if (GameMain.data.dysonSpheres == null) return;
                foreach (DysonSphere dysonSphere in GameMain.data.dysonSpheres)
                {
                    if (dysonSphere == null || dysonSphere.layerCount <= 0) continue;

                    //const int autoNodesAfterExpansion = DysonSphere.kAutoNodeMax;
                    int autoNodesAfterExpansion = Mathf.Clamp(dysonSphere.totalConstructedNodeCount / 2, 1, DysonSphere.kAutoNodeMax) - dysonSphere.autoNodeCount;
                    for (int i = 0; i < autoNodesAfterExpansion; i++)
                        dysonSphere.PickAutoNode();

                    foreach (DysonSphereLayer layer in dysonSphere.layersIdBased)
                        foreach (DysonNode node in layer?.nodePool ?? Enumerable.Empty<DysonNode>())
                        {
                            node?.RecalcSpReq();
                            node?.RecalcCpReq();
                        }
                }
            }
        }
    }
}
