using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
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
        public const string VERSION = "1.0.0";

        private Harmony harmony;
        private static ManualLogSource logger;

        private void Awake()
        {
            logger = Logger;
            harmony = new Harmony(GUID);
            harmony.PatchAll(typeof(EnableFullBuild));
            harmony.PatchAll(typeof(NodeLimiter));
            harmony.PatchAll(typeof(FrameLimiter));
            harmony.PatchAll(typeof(ShellLimiter));
            harmony.PatchAll(typeof(HandleNewLimit));
        }

        private void OnDestroy()
        {
            harmony.UnpatchSelf();
        }

        private static int NodeLatitude(DysonNode node) => Mathf.RoundToInt(Mathf.Asin(Mathf.Clamp01(Mathf.Abs(node.pos.normalized.y))) * Mathf.Rad2Deg);

        private static bool AllowedByLatitiude (DysonNode node)
        {
            if (node == null) return true;
            return NodeLatitude(node) <= Mathf.RoundToInt(GameMain.history.dysonNodeLatitude);
        }

        [HarmonyPatch]
        public static class EnableFullBuild
        {
            public static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(UIDysonBrush_Frame), "CheckCondition");
                yield return AccessTools.Method(typeof(UIDysonBrush_Node), "_OnUpdate");
                yield return AccessTools.Method(typeof(DysonBlueprintData), nameof(DysonBlueprintData.CheckLatLimit));
            }

            private static readonly FieldInfo dysonNodeLatitude = AccessTools.Field(typeof(GameHistoryData), nameof(GameHistoryData.dysonNodeLatitude));

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach (CodeInstruction instruction in instructions)
                {
                    if (instruction.LoadsField(dysonNodeLatitude))
                    {
                        yield return new CodeInstruction(OpCodes.Pop);
                        yield return new CodeInstruction(OpCodes.Ldc_R4, 90f);
                    }
                    else yield return instruction;
                }
            }
        }

        [HarmonyPatch]
        public static class NodeLimiter
        {
            public static MethodBase TargetMethod()
            {
                return AccessTools.PropertyGetter(typeof(DysonNode), nameof(DysonNode.spReqOrder));
            }

            public static void Postfix(ref int __result, DysonNode __instance)
            {
                if (DSPGame.IsMenuDemo) return;
                lock (__instance)
                {
                    if (__result <= 0)
                        return;
                    if (!AllowedByLatitiude(__instance))
                        __result = 0;
                }
            }
        }

        [HarmonyPatch]
        public static class FrameLimiter
        {
            public static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(DysonNode), nameof(DysonNode.ConstructSp));
                yield return AccessTools.Method(typeof(DysonNode), nameof(DysonNode.RecalcSpReq));
            }

            private static readonly FieldInfo spMax = AccessTools.Field(typeof(DysonFrame), nameof(DysonFrame.spMax));

            private static int CheckSpMax(DysonFrame dysonFrame)
            {
                if (DSPGame.IsMenuDemo) return dysonFrame.spMax;
                if (!AllowedByLatitiude(dysonFrame.nodeA) || !AllowedByLatitiude(dysonFrame.nodeB)) return 0;
                else return dysonFrame.spMax;
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
            public static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(DysonNode), nameof(DysonNode.ConstructCp));
                // theoretically, this could cause the needed cp to go negative
                // but that shouldn't be possible unless there are already cells somewhere they shouldn't be
                // and the game should be able to gracefully handle the scenario regardless
                yield return AccessTools.Method(typeof(DysonNode), nameof(DysonNode.RecalcCpReq));
            }

            private static readonly FieldInfo cpPerVertex = AccessTools.Field(typeof(DysonShell), nameof(DysonShell.cpPerVertex));

            private static int CheckCpPerVertex(DysonShell dysonShell)
            {
                if (DSPGame.IsMenuDemo) return dysonShell.cpPerVertex;
                if (dysonShell.nodes?.Any(node => !AllowedByLatitiude(node)) ?? false) return 0;
                else return dysonShell.cpPerVertex;
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
        public static class HandleNewLimit
        {
            public static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(GameHistoryData), nameof(GameHistoryData.UnlockTechFunction));
            }

            private const int latitudeTechUnlock = 26;  // is this defined in the code anywhere?

            private static void CheckRecalculation(DysonNode node, HashSet<DysonNode> recalculateSet)
            {
                int newLimit = Mathf.RoundToInt(GameMain.history.dysonNodeLatitude);
                int oldLimit = newLimit - 15;
                int nodeLatitude = NodeLatitude(node);
                // only check nodes in the new range
                if (nodeLatitude > newLimit || nodeLatitude <= oldLimit) return;
                recalculateSet.Add(node);
                foreach (DysonFrame frame in node.frames?.Where(frame => frame != null) ?? Enumerable.Empty<DysonFrame>())
                {
                    // add any connected nodes, but only ones that are already buildable
                    if (frame.nodeA != null && NodeLatitude(frame.nodeA) <= oldLimit)  // ones in the new range will be added regardless
                        recalculateSet.Add(frame.nodeA);
                    if (frame.nodeB != null && NodeLatitude(frame.nodeB) <= oldLimit)
                        recalculateSet.Add(frame.nodeB);
                }
                foreach (DysonShell shell in node.shells ?? Enumerable.Empty<DysonShell>())
                {
                    // if any part of the shell pokes past the limit it won't get built
                    if (shell?.nodes?.Any(shellNode => NodeLatitude(shellNode) > newLimit) ?? true) continue;
                    recalculateSet.UnionWith(shell.nodes);
                }
            }

            public static void Postfix(int func)
            {
                if (func != latitudeTechUnlock) return;
                foreach (DysonSphere dysonSphere in GameMain.data.dysonSpheres?.Where(sphere => sphere?.layerCount > 0) ?? Enumerable.Empty<DysonSphere>())
                {
#if SIMPLE_RECALC
                    foreach (DysonSphereLayer layer in dysonSphere.layersIdBased)
                        foreach (DysonNode node in layer?.nodePool?.Where(node => node != null) ?? Enumerable.Empty<DysonNode>())
                        {
                            node.RecalcSpReq();
                            node.RecalcCpReq();
                        }
#else
                    HashSet<DysonNode> toRecalculate = new HashSet<DysonNode>();

                    foreach (DysonSphereLayer layer in dysonSphere.layersIdBased)
                        foreach (DysonNode node in layer?.nodePool?.Where(node => node != null) ?? Enumerable.Empty<DysonNode>())
                            CheckRecalculation(node, toRecalculate);
                    toRecalculate.Remove(null);    // shouldn't happen, but just to be sure
                    foreach (DysonNode node in toRecalculate)
                    {
                        node.RecalcSpReq();
                        node.RecalcCpReq();
                    }
#endif
                    int autoNodesAfterExpansion = Mathf.Clamp(dysonSphere.totalConstructedNodeCount / 2, 1, DysonSphere.kAutoNodeMax) - dysonSphere.autoNodeCount;
                    for (int i = 0; i < autoNodesAfterExpansion; i++)
                        dysonSphere.PickAutoNode();
                }
            }
        }
    }
}
