using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;

namespace HSKFixes17
{
    internal static class MeleeHediffStackFix
    {
        private static MethodInfo packTranspiler;

        internal static bool TryApply(Harmony harmony, Type patchClass)
        {
            if (patchClass.FullName != "SK.Patch_Verb_MeleeApplyHediff")
                return false;

            packTranspiler = AccessTools.DeclaredMethod(patchClass, "Transpiler");
            if (packTranspiler == null)
                throw new MissingMethodException(patchClass.FullName, "Transpiler");

            harmony.Patch(AccessTools.DeclaredMethod(typeof(Verb_MeleeApplyHediff),
                "ApplyMeleeDamageToTarget"), transpiler: new HarmonyMethod(
                    typeof(MeleeHediffStackFix), nameof(Transpiler)));
            return true;
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = ((IEnumerable<CodeInstruction>)packTranspiler.Invoke(null,
                new object[] { instructions, generator })).ToList();

            // Пак вставляет nop; ldloc.0 (DamageResult); pawn; tool.hediff; part;
            // CanApplyHediff; brfalse AddPart. Проверка принимает только последние три
            // значения: DamageResult остаётся на стеке на ОБОИХ путях ветвления.
            // Убираем лишь эту лишнюю загрузку, сохраняя проверку иммунитета пака,
            // его цель перехода и все остальные изменения исходного транспайлера.
            int fixes = 0;
            for (int i = 7; i + 1 < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Call
                    || !(code[i].operand is MethodInfo check)
                    || check.DeclaringType != packTranspiler.DeclaringType
                    || check.Name != "CanApplyHediff"
                    || code[i - 7].opcode != OpCodes.Nop
                    || code[i - 6].opcode != OpCodes.Ldloc_0
                    || code[i - 5].opcode != OpCodes.Ldloc_1
                    || code[i - 4].opcode != OpCodes.Ldarg_0
                    || code[i - 3].opcode != OpCodes.Ldfld
                    || !(code[i - 3].operand is FieldInfo tool) || tool.Name != "tool"
                    || code[i - 2].opcode != OpCodes.Ldfld
                    || !(code[i - 2].operand is FieldInfo hediff) || hediff.Name != "hediff"
                    || code[i - 1].opcode != OpCodes.Ldloc_3
                    || code[i + 1].opcode != OpCodes.Brfalse)
                    continue;

                code[i - 5].labels.AddRange(code[i - 6].labels);
                code[i - 5].blocks.InsertRange(0, code[i - 6].blocks);
                code.RemoveAt(i - 6);
                fixes++;
                i--;
            }
            if (fixes != 1)
                throw new InvalidOperationException("Core SK melee hediff: expected one surplus "
                    + "DamageResult load, found " + fixes);
            return code;
        }
    }
}
