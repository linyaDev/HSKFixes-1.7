using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace HSKFixes17
{
    /// <summary>
    /// Одна точка перехвата для чужих патчей с несовместимым __instance.
    ///
    /// Болезнь одна и та же у разных модов пака: патч-метод объявляет __instance одного типа, а
    /// висит на методе другого. Harmony 1.7 такую привязку отвергает:
    ///
    ///     Unrelated classes cannot describe the same non-null object ...
    ///     supplied Verse.Pawn, requested RimWorld.Pawn_StoryTracker
    ///
    /// На Harmony из 1.6 то же самое проходит молча — проверено отдельным тестом: патч dummy-метода
    /// с намеренно несовпадающим __instance там встаёт без возражений. Поэтому пак с этим жил, а на
    /// 1.7 ошибка вылезла сразу у трёх его модов.
    ///
    /// Цена всегда больше одного патча. Моды ставят свои патчи подряд, в одном статическом
    /// конструкторе, и исключение уносит весь остаток списка:
    ///
    ///     SurvivalToolsLite  18-й из 20 — теряются смена оружия у призванных и износ инструмента
    ///                        в карьере Quarry
    ///     Minerals           2-й из 3 — теряется лёд при генерации карты и, что хуже, идущий
    ///                        следом PatchAll(), то есть вся атрибутная часть мода
    ///     Skynet_SK          хуже всех: падает сам конструктор, тип остаётся
    ///                        неинициализированным, и уже поставленный постфикс к
    ///                        Pawn_NeedsTracker.ShouldHaveNeed бросает на каждой пешке и каждой
    ///                        нужде — 37 строк на пешку
    ///     Androids           та же ошибка в том же методе, но мод оборачивает патчи своим
    ///                        try/catch, поэтому теряет только её
    ///
    /// Патчи, объявленные атрибутами и поставленные через PatchAll, этот перехват не видит: там
    /// Harmony.Patch не вызывается. Ими занимается PatchClassFix.
    ///
    /// Как работает. Префикс на Harmony.Patch смотрит каждый предлагаемый патч. Сначала проверяется
    /// сама болезнь — есть ли у метода параметр __instance, который не принимает объект цели
    /// (original.DeclaringType). Это ровно то, на что смотрит Harmony, так что ложных срабатываний
    /// не бывает: поправят подпись в моде — проверка перестанет давать true сама собой.
    ///
    /// Только после этого ищется замена среди тех, что мы знаем (STLWorkTagFix, MineralsIceFix,
    /// JoyPrefixFix).
    /// Чужие сломанные патчи, которых в списке нет, не трогаем: пусть падают как раньше, их мод
    /// сообщит об этом сам. Подменять то, чьей логики мы не читали, было бы хуже ошибки.
    ///
    /// Замена всегда повторяет логику оригинала и ничего не добавляет — задача в том, чтобы патч
    /// встал, а не в том, чтобы его улучшить. Дальше конструктор мода идёт своим ходом и ставит
    /// остаток списка сам.
    ///
    /// Патчить Harmony.Patch можно — тоже проверено тестом, вместе со сквозным эффектом подмены.
    /// Префикс выполняется на каждом патче в игре, поэтому проверки идут от самых дешёвых.
    /// </summary>
    public static class BrokenPatchFix
    {
        private const string HarmonyId = "linya.hskfixes17";

        private static readonly Func<MethodInfo, MethodInfo>[] Swaps =
        {
            STLWorkTagFix.ReplacementFor,
            MineralsIceFix.ReplacementFor,
            JoyPrefixFix.ReplacementFor,
        };

        public static void Install()
        {
            // Подпись стабильна и в 1.6, и в 1.7: Patch(MethodBase, HarmonyMethod x4).
            MethodInfo patchMethod = AccessTools.Method(typeof(Harmony), nameof(Harmony.Patch),
                new[]
                {
                    typeof(MethodBase), typeof(HarmonyMethod), typeof(HarmonyMethod),
                    typeof(HarmonyMethod), typeof(HarmonyMethod)
                });
            if (patchMethod == null)
            {
                Log.Warning("[HSK Fixes 1.7] не найден Harmony.Patch — подмены сломанных патчей не будет.");
                return;
            }

            new Harmony(HarmonyId).Patch(patchMethod,
                new HarmonyMethod(typeof(BrokenPatchFix), nameof(SwapBrokenPatches)));
        }

        private static void SwapBrokenPatches(MethodBase original, ref HarmonyMethod prefix,
            ref HarmonyMethod postfix)
        {
            TrySwap(original, ref prefix);
            TrySwap(original, ref postfix);
        }

        private static void TrySwap(MethodBase original, ref HarmonyMethod patch)
        {
            MethodInfo method = patch?.method;
            if (method == null || original == null)
                return;
            if (!HasIncompatibleInstance(method, original))
                return;

            foreach (var swap in Swaps)
            {
                MethodInfo replacement = swap(method);
                if (replacement == null)
                    continue;

                patch = new HarmonyMethod(replacement);
                Log.Message("[HSK Fixes 1.7] патч " + method.DeclaringType?.Name + "." + method.Name
                    + " к " + original.DeclaringType?.Name + "." + original.Name + " подменён:"
                    + " в оригинале __instance объявлен несовместимым типом, и Harmony 1.7 отвергала"
                    + " его, а вместе с ним — остаток списка патчей этого мода. Логика сохранена.");
                return;
            }
        }

        /// <summary>
        /// Ровно проверка Harmony: тип __instance должен принимать объект, на котором вызывается
        /// патчимый метод. Для статических целей DeclaringType всё равно годится — именно его
        /// Harmony и подставляет.
        /// </summary>
        private static bool HasIncompatibleInstance(MethodInfo patch, MethodBase original)
        {
            Type target = original.DeclaringType;
            if (target == null)
                return false;

            foreach (ParameterInfo parameter in patch.GetParameters())
            {
                if (parameter.Name != "__instance")
                    continue;

                Type declared = parameter.ParameterType;
                if (declared.IsByRef)
                    declared = declared.GetElementType();
                return declared != null && !declared.IsAssignableFrom(target);
            }
            return false;
        }
    }
}
