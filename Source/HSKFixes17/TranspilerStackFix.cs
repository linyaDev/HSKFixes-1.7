using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace HSKFixes17
{
    public static class TranspilerStackFix
    {
        private static MethodInfo alienTranspiler;

        public static void TrySwap(MethodBase original, ref HarmonyMethod patch)
        {
            MethodInfo method = patch?.method;
            if (method == null)
                return;

            string replacement;
            if (method.DeclaringType?.FullName == "AlienRace.HarmonyPatches"
                && method.Name == "ApplyBirthOutcomeTranspiler"
                && original.DeclaringType?.FullName == "RimWorld.PregnancyUtility"
                && original.Name == "ApplyBirthOutcome"
                && original is MethodInfo birth && birth.ReturnType == typeof(Thing))
            {
                alienTranspiler = method;
                replacement = nameof(AlienBirth);
            }
            else if (method.DeclaringType?.FullName == "AssortedTweaks.Patch_WorkGiver_ConstructDeliverResources"
                && method.Name == "ResourceDeliverJobFor_Transpiler"
                && original.DeclaringType?.FullName == "RimWorld.WorkGiver_ConstructDeliverResources"
                && original.Name == "ResourceDeliverJobFor")
                replacement = nameof(DeliverResources);
            else
                return;

            // Keep the registering mod's ordering constraints and priority.
            patch = new HarmonyMethod(method: AccessTools.Method(typeof(TranspilerStackFix), replacement))
            {
                priority = patch.priority,
                before = patch.before,
                after = patch.after,
            };
            Log.Message("[HSK Fixes 1.7] исправлен стек транспайлера "
                + method.DeclaringType.FullName + "." + method.Name);
        }

        public static IEnumerable<CodeInstruction> AlienBirth(IEnumerable<CodeInstruction> instructions,
            ILGenerator generator, MethodBase original)
        {
            // HAR recursively calls ApplyBirthOutcome for additional babies. It used to return
            // void; in 1.7 it returns Thing. Discard only the extra births' unused results.
            var patched = ((IEnumerable<CodeInstruction>)alienTranspiler.Invoke(null,
                new object[] { instructions, generator, original })).ToList();
            int fixes = 0;
            foreach (CodeInstruction instruction in patched)
            {
                yield return instruction;
                if (instruction.opcode == OpCodes.Call && Equals(instruction.operand, original))
                {
                    yield return new CodeInstruction(OpCodes.Pop);
                    fixes++;
                }
            }
            if (fixes == 0)
                Log.Warning("[HSK Fixes 1.7] HAR: рекурсивный вызов рождения не найден.");
        }

        public static IEnumerable<CodeInstruction> DeliverResources(IEnumerable<CodeInstruction> instructions)
        {
            // Original tweak turns 'beq continue; leave end' into 'br continue; leave end'.
            // Unlike beq, br consumes no operands. Consume both compared references first.
            // Match the original field and control flow, without assuming a closure local number.
            var code = instructions.ToList();
            int fixes = 0;
            for (int i = 0; i + 5 < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Ldsfld
                    || !(code[i].operand is FieldInfo field)
                    || field.DeclaringType?.FullName != "RimWorld.FloatMenuMakerMap"
                    || field.Name != "makingFor"
                    || !code[i + 1].IsLdloc()
                    || code[i + 2].opcode != OpCodes.Ldfld
                    || code[i + 3].opcode != OpCodes.Ldfld
                    || (code[i + 4].opcode != OpCodes.Beq && code[i + 4].opcode != OpCodes.Beq_S)
                    || (code[i + 5].opcode != OpCodes.Leave && code[i + 5].opcode != OpCodes.Leave_S))
                    continue;

                object target = code[i + 4].operand;
                code[i + 4].opcode = OpCodes.Pop;
                code[i + 4].operand = null;
                code.InsertRange(i + 5, new[]
                {
                    new CodeInstruction(OpCodes.Pop),
                    new CodeInstruction(OpCodes.Br, target),
                });
                fixes++;
                i += 7;
            }
            if (fixes == 0)
                Log.Warning("[HSK Fixes 1.7] AssortedTweaks: переход доставки ресурсов не найден.");
            return code;
        }
    }
}
