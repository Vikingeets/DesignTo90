using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System.Collections.Generic;
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
            harmony.PatchAll(typeof(NodeLimitReAuto));
            Logger.LogMessage("DesignTo90 patches complete");
        }

        private void OnDestroy()
        {
            harmony.UnpatchSelf();
        }

        [HarmonyPatch]
        public static class EnableFullBuild
        {
            public static FieldInfo dysonNodeLatitude = AccessTools.Field(typeof(GameHistoryData), nameof(GameHistoryData.dysonNodeLatitude));

            public static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(UIDysonBrush_Frame), "CheckCondition");
                yield return AccessTools.Method(typeof(UIDysonBrush_Node), "_OnUpdate");
                yield return AccessTools.Method(typeof(DysonBlueprintData), nameof(DysonBlueprintData.CheckLatLimit));
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                foreach (var instruction in instructions)
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
                if (Mathf.RoundToInt(Mathf.Asin(Mathf.Clamp01(Mathf.Abs(__instance.pos.normalized.y))) * Mathf.Rad2Deg) > Mathf.RoundToInt(GameMain.history.dysonNodeLatitude))
                    __result = 0;
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
                if (GameMain.data.dysonSpheres ==  null) return;
                foreach (DysonSphere dysonSphere in GameMain.data.dysonSpheres)
                {
                    if (dysonSphere == null || dysonSphere.layerCount <= 0) continue;
                    //const int autoNodesAfterExpansion = DysonSphere.kAutoNodeMax;
                    const int autoNodesAfterExpansion = 1;
                    for (int i = 0; i < autoNodesAfterExpansion; i++)
                        dysonSphere.PickAutoNode();
                }
            }
        }
    }
}
