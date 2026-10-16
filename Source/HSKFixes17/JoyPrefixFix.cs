using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace HSKFixes17
{
    /// <summary>
    /// Замена сломанного префикса к JobGiver_GetJoy.TryGiveJob — одна и та же ошибка у двух модов.
    ///
    /// И Skynet_SK, и Androids ставят подряд два патча, и у второго тип __instance скопирован из
    /// первого:
    ///
    ///     val.Patch(AccessTools.Method(typeof(JobGiver_EatInGatheringArea), "TryGiveJob"),
    ///               ... "CompatPatch_EatInPartyAreaTryGiveJob");   // верно
    ///     val.Patch(AccessTools.Method(typeof(JobGiver_GetJoy), "TryGiveJob"),
    ///               ... "CompatPatch_GetJoyTryGiveJob");           // __instance остался от первого
    ///
    ///     public static bool CompatPatch_GetJoyTryGiveJob(
    ///         ref JobGiver_EatInGatheringArea __instance, ref Job __result, ref Pawn pawn)
    ///
    /// Даже имена методов совпадают: код у этих модов общего происхождения. Harmony на 1.7 такую
    /// привязку отвергает, и цена разная:
    ///
    ///     Skynet    падает сам статический конструктор, тип остаётся неинициализированным, и уже
    ///               поставленный постфикс к Pawn_NeedsTracker.ShouldHaveNeed бросает
    ///               TypeInitializationException на каждую пешку и каждую нужду — 9953 строки
    ///     Androids  оборачивает патчи своим SafeHarmony с try/catch, поэтому теряет ровно этот
    ///               патч и пишет одну строку "[Androids] Failed to patch"
    ///
    /// Логика у обоих одна: если у дефа пешки есть их расширение — работа не выдаётся. __instance в
    /// телах не используется вовсе, поэтому замены его не объявляют. Расширения разные, поэтому и
    /// замен две: Skynet смотрит на SkynetPawnExtension, Androids — на MechanicalPawnProperties.
    /// Оба мода патчат один метод, и после подмены оба префикса встают рядом, как и задумано.
    /// </summary>
    public static class JoyPrefixFix
    {
        private const string BrokenPrefixName = "CompatPatch_GetJoyTryGiveJob";

        private static Type skynetExtension;
        private static Type androidsExtension;

        /// <summary>Опознаёт сломанный патч; сам факт поломки проверяет BrokenPatchFix.</summary>
        public static MethodInfo ReplacementFor(MethodInfo patch)
        {
            if (patch.Name != BrokenPrefixName || patch.DeclaringType == null)
                return null;

            switch (patch.DeclaringType.FullName)
            {
                case "Skynet.HarmonyPatches":
                    return Resolve("Skynet.SkynetPawnExtension", ref skynetExtension,
                        nameof(NoJoyForSkynetPawns));
                case "Androids.HarmonyPatches":
                    return Resolve("Androids.MechanicalPawnProperties", ref androidsExtension,
                        nameof(NoJoyForAndroids));
                default:
                    return null;
            }
        }

        private static MethodInfo Resolve(string extensionName, ref Type cache, string replacement)
        {
            if (cache == null)
            {
                cache = AccessTools.TypeByName(extensionName);
                if (cache == null)
                {
                    Log.Warning("[HSK Fixes 1.7] не найден тип " + extensionName
                        + " — префикс к JobGiver_GetJoy оставлен как есть.");
                    return null;
                }
            }
            return AccessTools.Method(typeof(JoyPrefixFix), replacement);
        }

        public static bool NoJoyForSkynetPawns(ref Job __result, Pawn pawn)
        {
            return Allow(pawn, skynetExtension, ref __result);
        }

        public static bool NoJoyForAndroids(ref Job __result, Pawn pawn)
        {
            return Allow(pawn, androidsExtension, ref __result);
        }

        private static bool Allow(Pawn pawn, Type extension, ref Job result)
        {
            if (!HasExtension(pawn, extension))
                return true;

            result = null;
            return false;
        }

        /// <summary>
        /// Def.HasModExtension есть только обобщённый, а тип у нас в переменной — поэтому идём по
        /// списку расширений сами.
        /// </summary>
        private static bool HasExtension(Pawn pawn, Type extension)
        {
            List<DefModExtension> extensions = pawn?.def?.modExtensions;
            if (extensions == null || extension == null)
                return false;

            for (int i = 0; i < extensions.Count; i++)
            {
                if (extension.IsInstanceOfType(extensions[i]))
                    return true;
            }
            return false;
        }
    }
}
