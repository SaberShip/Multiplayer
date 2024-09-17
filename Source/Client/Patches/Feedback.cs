using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;
using System.Linq;

namespace Multiplayer.Client.Patches
{
    [HarmonyPatch]
    static class CancelFeedbackNotTargetedAtMe
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SoundStarter), nameof(SoundStarter.PlayOneShot));
            yield return AccessTools.Method(typeof(Command_SetPlantToGrow), nameof(Command_SetPlantToGrow.WarnAsAppropriate));
            yield return AccessTools.Method(typeof(TutorUtility), nameof(TutorUtility.DoModalDialogIfNotKnown));
            yield return AccessTools.Method(typeof(CameraJumper), nameof(CameraJumper.TryHideWorld));
        }

        public static bool Cancel =>
            Multiplayer.Client != null &&
            Multiplayer.ExecutingCmds &&
            !TickPatch.currentExecutingCmdIssuedBySelf;

        static bool Prefix() => !Cancel;
    }

    [HarmonyPatch(typeof(Targeter), nameof(Targeter.BeginTargeting), typeof(TargetingParameters), typeof(Action<LocalTargetInfo>), typeof(Pawn), typeof(Action), typeof(Texture2D), typeof(bool))]
    static class CancelBeginTargeting
    {
        static bool Prefix()
        {
            if (TickPatch.currentExecutingCmdIssuedBySelf && AsyncTimeComp.executingCmdMap != null)
                AsyncTimeComp.keepTheMap = true;

            return !CancelFeedbackNotTargetedAtMe.Cancel;
        }
    }

    [HarmonyPatch(typeof(WorldTargeter), nameof(WorldTargeter.StopTargeting))]
    static class CancelCancelGlobalTargeting
    {
        static bool Prefix()
        {
            return !CancelFeedbackNotTargetedAtMe.Cancel;
        }
    }

    [HarmonyPatch]
    static class CancelMotesNotTargetedAtMe
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MoteMaker), nameof(MoteMaker.MakeStaticMote), new[] { typeof(IntVec3), typeof(Map), typeof(ThingDef), typeof(float) });
            yield return AccessTools.Method(typeof(MoteMaker), nameof(MoteMaker.MakeStaticMote), new[] { typeof(Vector3), typeof(Map), typeof(ThingDef), typeof(float), typeof(bool), typeof(float) });
        }

        static bool Prefix(ThingDef moteDef)
        {
            // Catches ritual effect motes
            if (moteDef.mote.solidTime >= 99999)
                return true;

            return !CancelFeedbackNotTargetedAtMe.Cancel;
        }
    }

    [HarmonyPatch]
    static class CancelFlecksNotTargetedAtMe
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(FleckManager), nameof(FleckManager.CreateFleck));
        }

        static bool Prefix(ref FleckCreationData fleckData)
        {
            if (fleckData.def == FleckDefOf.FeedbackGoto)
                return true;

            return !CancelFeedbackNotTargetedAtMe.Cancel;
        }
    }

    [HarmonyPatch(typeof(Messages), nameof(Messages.Message), new[] { typeof(Message), typeof(bool) })]
    static class HideMessagesNotTargetedAtMe
    {
        private static FieldInfo liveMessages = AccessTools.Field(typeof(Messages), "liveMessages");

        private static MethodInfo addMessageToList =
            typeof(List<Message>).GetMethod(nameof(List<Message>.Add), [typeof(Message)]);

        private static MethodInfo messageRelevant =
            AccessTools.Method(typeof(HideMessagesNotTargetedAtMe), nameof(IsMessageRelevant));

        static IEnumerable<CodeInstruction> Transpiler(ILGenerator gen, IEnumerable<CodeInstruction> e, MethodBase original)
        {
            List<CodeInstruction> insts = e.ToList();
            Label outLabel = gen.DefineLabel();
            Label retLabel = gen.DefineLabel();

            var outJmpFinder = new CodeFinder(original, insts).Start();
            int firstOut = outJmpFinder.Forward(OpCodes.Brfalse_S);
            int secondOut = outJmpFinder.Advance(1).Forward(OpCodes.Brfalse_S);
            insts[firstOut].operand = outLabel;
            insts[secondOut].operand = outLabel;

            var finder = new CodeFinder(original, insts);
            int injectPoint = finder.Start().Forward(OpCodes.Ldsfld, liveMessages);
            insts.Insert(injectPoint,
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, messageRelevant),
                new CodeInstruction(OpCodes.Brfalse, retLabel));
            insts[injectPoint].labels.Add(outLabel);

            var retFinder = new CodeFinder(original, insts);
            int ret = retFinder.End().Backward(OpCodes.Ret);
            insts[ret].labels.Add(retLabel);

            return insts;
        }

        static bool IsMessageRelevant(Message message) {
            if (Multiplayer.Client == null || !Multiplayer.ExecutingCmds || TickPatch.currentExecutingCmdIssuedBySelf)
                return true;

            else if (message.lookTargets.IsValid()) {
                GlobalTargetInfo target = message.lookTargets.PrimaryTarget;

                if (target.HasThing) {
                    Faction faction = target.Thing.Faction;

                    if (faction != null && faction.def.isPlayer) {
                        return target.Thing.Faction == Multiplayer.RealPlayerFaction;
                    }
                }
            }
            
            // Default assume message is relevant
            return true;
        }
    }

    [HarmonyPatch(typeof(DesignatorManager), nameof(DesignatorManager.Deselect))]
    static class CancelDesignatorDeselection
    {
        public static bool Cancel =>
            Multiplayer.Client != null &&
            Multiplayer.ExecutingCmds &&
            !TickPatch.currentExecutingCmdIssuedBySelf;

        static bool Prefix()
        {
            return !Cancel;
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.DeSpawn))]
    static class AlwaysDeselectWhileDespawning
    {
        static MethodInfo IsSelected = AccessTools.Method(typeof(Selector), nameof(Selector.IsSelected));
        static MethodInfo DeselectOnDespawnMethod = AccessTools.Method(typeof(AlwaysDeselectWhileDespawning), nameof(DeselectOnDespawn));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> insts)
        {
            foreach (var inst in insts)
            {
                if (inst.operand == IsSelected)
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, DeselectOnDespawnMethod);
                }

                yield return inst;
            }
        }

        static void DeselectOnDespawn(Thing t)
        {
            if (Multiplayer.Client == null || AsyncTimeComp.prevSelected == null) return;
            AsyncTimeComp.prevSelected.Remove(t);
        }
    }

    [HarmonyPatch(typeof(Bill), nameof(Bill.CreateNoPawnsWithSkillDialog))]
    static class CancelNoPawnWithSkillDialog
    {
        static bool Prefix() =>
            Multiplayer.Client == null ||
            TickPatch.currentExecutingCmdIssuedBySelf;
    }

    [HarmonyPatch]
    static class NoCameraJumpingDuringSimulating
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CameraJumper), nameof(CameraJumper.TrySelect));
            yield return AccessTools.Method(typeof(CameraJumper), nameof(CameraJumper.TryJumpAndSelect));
            yield return AccessTools.Method(typeof(CameraJumper), nameof(CameraJumper.TryJump), new[] {typeof(GlobalTargetInfo), typeof(CameraJumper.MovementMode)});
        }
        static bool Prefix() => !TickPatch.Simulating;
    }

    [HarmonyPatch(typeof(Selector), nameof(Selector.Deselect))]
    static class SelectorDeselectPatch
    {
        public static List<object> deselected;

        static void Prefix(object obj)
        {
            if (deselected != null)
                deselected.Add(obj);
        }
    }

    [HarmonyPatch(typeof(CompBiosculpterPod), nameof(CompBiosculpterPod.OrderToPod))]
    static class NoBiosculpterConfirmationSyncing
    {
        static bool Prefix(CompBiosculpterPod_Cycle cycle, Pawn pawn, Action giveJobAct)
        {
            if (Multiplayer.Client == null || cycle is not CompBiosculpterPod_HealingCycle healingCycle)
                return true; // Alternatively we could return false and invoke giveJobAct

            var healingDescriptionForPawn = healingCycle.GetHealingDescriptionForPawn(pawn);
            string text = healingDescriptionForPawn.NullOrEmpty()
                ? "BiosculpterNoCoditionsToHeal".Translate(pawn.Named("PAWN"), healingCycle.Props.label.Named("CYCLE")).Resolve()
                : "OnCompletionOfCycle".Translate(healingCycle.Props.label.Named("CYCLE")).Resolve() + ":\n\n" + healingDescriptionForPawn;

            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                text,
                giveJobAct,
                healingDescriptionForPawn.NullOrEmpty()));

            return false;
        }
    }

}
