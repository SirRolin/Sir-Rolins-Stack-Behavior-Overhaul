using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace StackBehaviorOverhaul
{
    [StaticConstructorOnStartup]
    internal static class ModStartup
    {
        static ModStartup()
        {
            new Harmony("SirRolin.StackBehaviorOverhaul").PatchAll(Assembly.GetExecutingAssembly());
        }
    }

    [HarmonyPatch]
    internal static class ThingDestroyPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Thing), nameof(Thing.Destroy), new[] { typeof(DestroyMode) });
            yield return AccessTools.Method(typeof(ThingWithComps), nameof(Thing.Destroy), new[] { typeof(DestroyMode) });
        }

        static bool Prefix(Thing __instance)
        {
            if (ExplosiveDetonatePatch.IsDetonating(__instance) ||
                ExplosiveDetonatePatch.ExplodesOnDestroy(__instance))
                return true;

            if (ThingTakeDamagePatch.IsProcessingStackDamage(__instance))
                return false;

            int maxHitPoints = __instance.MaxHitPoints;
            if (__instance.stackCount <= 1 || __instance.HitPoints > 0 || maxHitPoints <= 0)
                return true;

            while (__instance.HitPoints <= 0 && __instance.stackCount > 1)
            {
                __instance.stackCount--;
                __instance.HitPoints += maxHitPoints;
            }

            return __instance.HitPoints <= 0;
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.TakeDamage))]
    internal static class ThingTakeDamagePatch
    {
        private static readonly Dictionary<Thing, int> ProcessingStackDamage = new Dictionary<Thing, int>();

        private struct StackDamageState
        {
            public bool Active;
            public int MaxHitPoints;
            public int StackCount;
        }

        static void Prefix(Thing __instance, ref DamageInfo dinfo, out StackDamageState __state)
        {
            __state = default(StackDamageState);
            int maxHitPoints = __instance.MaxHitPoints;
            if (dinfo.Def != null &&
                dinfo.Def.isExplosive &&
                __instance.def.stackLimit > 1 &&
                __instance.def.stackLimit > 0 &&
                dinfo.Amount > 0f)
            {
                float scaling = __instance.stackCount * StackBehaviorOverhaulSettings.Current.ExplosionScaling /
                    __instance.def.stackLimit;
                dinfo.SetAmount(dinfo.Amount * scaling);
            }

            if (__instance.stackCount <= 1 || maxHitPoints <= 0 || __instance.HitPoints <= 0 || dinfo.Amount <= 0f)
                return;

            long combinedHitPoints = (long)__instance.HitPoints + (long)(__instance.stackCount - 1) * maxHitPoints;
            __instance.HitPoints = (int)Math.Min(int.MaxValue, combinedHitPoints);
            __state = new StackDamageState
            {
                Active = true,
                MaxHitPoints = maxHitPoints,
                StackCount = __instance.stackCount
            };
            BeginStackDamage(__instance);
        }

        static void Postfix(Thing __instance, ref StackDamageState __state)
        {
            if (!__state.Active)
                return;

            EndStackDamage(__instance);
            __state.Active = false;
            if (__instance.Destroyed || __instance.stackCount <= 0)
                return;

            long remainingHitPoints = __instance.HitPoints;
            if (remainingHitPoints <= 0)
            {
                __instance.stackCount = 1;
                __instance.Destroy(DestroyMode.Vanish);
                return;
            }

            int remainingStackCount = (int)Math.Min(
                __state.StackCount,
                (remainingHitPoints + __state.MaxHitPoints - 1) / __state.MaxHitPoints);
            __instance.stackCount = remainingStackCount;
            __instance.HitPoints = (int)(remainingHitPoints - (long)(remainingStackCount - 1) * __state.MaxHitPoints);
        }

        static Exception Finalizer(Thing __instance, ref StackDamageState __state, Exception __exception)
        {
            if (__state.Active)
            {
                EndStackDamage(__instance);
                __state.Active = false;
            }

            return __exception;
        }

        internal static bool IsProcessingStackDamage(Thing thing)
        {
            return ProcessingStackDamage.ContainsKey(thing);
        }

        private static void BeginStackDamage(Thing thing)
        {
            ProcessingStackDamage.TryGetValue(thing, out int depth);
            ProcessingStackDamage[thing] = depth + 1;
        }

        private static void EndStackDamage(Thing thing)
        {
            if (!ProcessingStackDamage.TryGetValue(thing, out int depth))
                return;

            if (depth <= 1)
                ProcessingStackDamage.Remove(thing);
            else
                ProcessingStackDamage[thing] = depth - 1;
        }
    }

    [HarmonyPatch]
    internal static class ThingSplitOffPatch
    {
        private struct SplitState
        {
            public int OriginalStackCount;
            public int SplitCount;
        }

        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Thing), nameof(Thing.SplitOff), new[] { typeof(int) });
            yield return AccessTools.Method(typeof(ThingWithComps), nameof(Thing.SplitOff), new[] { typeof(int) });
        }

        static void Prefix(Thing __instance, int count, out SplitState __state)
        {
            __state = new SplitState
            {
                OriginalStackCount = __instance.stackCount,
                SplitCount = count
            };
        }

        static void Postfix(Thing __instance, Thing __result, SplitState __state)
        {
            if (__result != null &&
                __state.SplitCount > 0 &&
                __state.SplitCount < __state.OriginalStackCount &&
                __instance.stackCount > 0)
            {
                __instance.HitPoints = __instance.MaxHitPoints;
            }
        }
    }

    [HarmonyPatch]
    internal static class ThingTryAbsorbStackPatch
    {
        private struct MergeState
        {
            public bool Valid;
            public int ThisStackCount;
            public int ThisHitPoints;
            public int OtherHitPoints;
            public int MaxHitPoints;
        }

        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Thing), nameof(Thing.TryAbsorbStack), new[] { typeof(Thing), typeof(bool) });
        }

        static void Prefix(Thing __instance, Thing other, out MergeState __state)
        {
            __state = default(MergeState);
            if (other == null ||
                __instance.def.stackLimit <= 1 ||
                other.def != __instance.def ||
                other.MaxHitPoints != __instance.MaxHitPoints ||
                __instance.MaxHitPoints <= 0)
            {
                return;
            }

            __state = new MergeState
            {
                Valid = true,
                ThisStackCount = __instance.stackCount,
                ThisHitPoints = __instance.HitPoints,
                OtherHitPoints = other.HitPoints,
                MaxHitPoints = __instance.MaxHitPoints
            };
        }

        static void Postfix(Thing __instance, bool __result, MergeState __state)
        {
            if (!__result || !__state.Valid || __instance.stackCount <= __state.ThisStackCount)
                return;

            long combinedHitPoints = (long)__state.ThisHitPoints + __state.OtherHitPoints - __state.MaxHitPoints;
            __instance.HitPoints = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, combinedHitPoints));

            while (__instance.HitPoints <= 0 && __instance.stackCount > 1)
            {
                __instance.stackCount--;
                __instance.HitPoints += __state.MaxHitPoints;
            }

            if (__instance.HitPoints <= 0)
                __instance.Destroy(DestroyMode.Vanish);
        }
    }

    internal static class StackMarketValue
    {
        internal static float AdjustedPerItemValue(Thing thing, float currentValue)
        {
            if (thing == null ||
                thing.stackCount <= 1 ||
                thing.def.stackLimit <= 1 ||
                thing.HitPoints >= thing.MaxHitPoints ||
                thing.MaxHitPoints <= 0)
            {
                return currentValue;
            }

            int currentHitPoints = thing.HitPoints;
            float fullHealthValue;
            try
            {
                thing.HitPoints = thing.MaxHitPoints;
                fullHealthValue = thing.MarketValue;
            }
            finally
            {
                thing.HitPoints = currentHitPoints;
            }

            return (float)(((double)currentValue + (double)fullHealthValue * (thing.stackCount - 1)) /
                thing.stackCount);
        }

    }

    [HarmonyPatch(typeof(Thing), "get_MarketValue")]
    internal static class ThingMarketValuePatch
    {
        static void Postfix(Thing __instance, ref float __result)
        {
            __result = StackMarketValue.AdjustedPerItemValue(__instance, __result);
        }
    }

    [HarmonyPatch]
    internal static class TradeableMatchingDamagedStackPatch
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(TransferableUtility), "TradeableMatching",
                new[] { typeof(Thing), typeof(List<Tradeable>) });
        }

        static bool Prefix(Thing __0, List<Tradeable> __1, ref Tradeable __result)
        {
            Thing thing = __0;
            if (thing == null || __1 == null || thing.def.stackLimit <= 1 || thing.MaxHitPoints <= 0)
                return true;

            bool thingDamaged = thing.HitPoints < thing.MaxHitPoints;
            foreach (Tradeable tradeable in __1)
            {
                Thing existing = tradeable.AnyThing;
                if (existing == null ||
                    existing.def != thing.def ||
                    existing.MaxHitPoints <= 0)
                {
                    continue;
                }

                if (thingDamaged != (existing.HitPoints < existing.MaxHitPoints))
                {
                    __result = null;
                    return false;
                }
            }

            return true;
        }
    }

    [HarmonyPatch]
    internal static class TradeDealSplitDamagedStackPatch
    {
        private static readonly MethodInfo AddToTradeablesMethod = AccessTools.Method(
            typeof(TradeDeal), "AddToTradeables", new[] { typeof(Thing), typeof(Transactor) });

        static MethodBase TargetMethod()
        {
            return AddToTradeablesMethod;
        }

        static void Prefix(TradeDeal __instance, Thing t, Transactor trans)
        {
            if (t == null ||
                t.stackCount <= 1 ||
                t.def.stackLimit <= 1 ||
                t.MaxHitPoints <= 0 ||
                t.HitPoints >= t.MaxHitPoints)
            {
                return;
            }

            ThingOwner owner = t.holdingOwner;
            bool wasSpawned = t.Spawned;
            Map map = t.Map;
            IntVec3 position = t.Position;
            Rot4 rotation = t.Rotation;
            int originalHitPoints = t.HitPoints;
            Thing damagedItem = t.SplitOff(1);

            bool attached;
            if (owner != null)
            {
                attached = owner.TryAdd(damagedItem, false);
            }
            else if (wasSpawned && map != null)
            {
                GenSpawn.Spawn(damagedItem, position, map, rotation, WipeMode.VanishOrMoveAside, false, false);
                attached = damagedItem.Spawned;
            }
            else
            {
                attached = false;
            }

            if (!attached)
            {
                t.stackCount++;
                t.HitPoints = originalHitPoints;
                return;
            }

            AddToTradeablesMethod.Invoke(__instance, new object[] { damagedItem, trans });
        }
    }

    [HarmonyPatch]
    internal static class ExplosiveDetonatePatch
    {
        private static readonly Dictionary<Thing, Stack<int>> ActiveDetonations = new Dictionary<Thing, Stack<int>>();

        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(CompExplosive), "Detonate", new[] { typeof(Map), typeof(bool) });
        }

        static void Prefix(CompExplosive __instance)
        {
            Thing source = __instance.parent;
            if (source == null)
                return;

            if (!ActiveDetonations.TryGetValue(source, out Stack<int> stackCounts))
            {
                stackCounts = new Stack<int>();
                ActiveDetonations.Add(source, stackCounts);
            }

            stackCounts.Push(source.stackCount);
        }

        static Exception Finalizer(CompExplosive __instance, Exception __exception)
        {
            Thing source = __instance.parent;
            if (source != null &&
                ActiveDetonations.TryGetValue(source, out Stack<int> stackCounts))
            {
                stackCounts.Pop();
                if (stackCounts.Count == 0)
                    ActiveDetonations.Remove(source);
            }

            return __exception;
        }

        internal static bool IsDetonating(Thing thing)
        {
            return thing != null && ActiveDetonations.ContainsKey(thing);
        }

        internal static bool ExplodesOnDestroy(Thing thing)
        {
            if (!(thing is ThingWithComps thingWithComps))
                return false;

            CompExplosive explosive = thingWithComps.GetComp<CompExplosive>();
            return explosive != null && explosive.Props.explodeOnDestroyed;
        }
    }
}
