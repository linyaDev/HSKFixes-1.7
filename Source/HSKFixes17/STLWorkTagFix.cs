using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace HSKFixes17
{
    /// <summary>
    /// Замена сломанного префикса Survival Tools Lite — мода из состава пака.
    ///
    /// Что в логе:
    ///
    ///     Last patch that went wrong is: Pawn_WorkTagIsDisabled. Exception message: Unrelated
    ///     classes cannot describe the same non-null object: original Boolean
    ///     Verse.Pawn::WorkTagIsDisabled(WorkTags w), patch static Boolean
    ///     SurvivalToolsLite.HarmonyPatches::Prefix_Pawn_WorkTagIsDisabled(Pawn_StoryTracker
    ///     __instance, Boolean __result) ... supplied Verse.Pawn, requested Pawn_StoryTracker
    ///
    /// Префикс просит __instance типа Pawn_StoryTracker, а патчится Pawn.WorkTagIsDisabled.
    /// Патчи STL стоят подряд в одном try внутри статического конструктора, и этот — 18-й из 20,
    /// поэтому исключение забирает с собой и то, что идёт после:
    ///
    ///     Pawn_DraftController.Drafted (set)      Postfix_WeaponSwitcher — смена оружия у
    ///                                             призванных пешек
    ///     Fluffy_Breakdowns.JobDriver_Maintenance MakeNewToils_Postfix (мода нет — пропуск)
    ///     Quarry.JobDriver_MineQuarry.Mine        Postfix_JobDriver_MineQuarry_Mine — износ и
    ///                                             скорость инструмента в карьере; Quarry в
    ///                                             списке есть, так что потеря настоящая
    ///
    /// Логика оригинала сохранена целиком: в hardcoreMode метод возвращает false для любой пешки и
    /// любого WorkTags. У них __result объявлен по значению, то есть присваивание было мёртвым — но
    /// пропуск оригинала и так оставляет false, так что поведение то же. Поле hardcoreMode по
    /// умолчанию false: без галочки в настройках префикс не делает ничего.
    ///
    /// Почему это лежит в HSK, а не в NoCE. Сборка STL жёстко ссылается на CombatExtended
    /// (CompInventory, CompAmmoUser), так что без заглушки игра отвергла бы её целиком — до своего
    /// патча STL просто не доживает. Но сама ошибка к CE не относится: она про подпись и строгость
    /// Harmony, и с настоящим CE на 1.7 возникнет точно так же.
    /// </summary>
    public static class STLWorkTagFix
    {
        private const string PatchesTypeName = "SurvivalToolsLite.HarmonyPatches";
        private const string BrokenPrefixName = "Prefix_Pawn_WorkTagIsDisabled";
        private const string SettingsTypeName = "SurvivalToolsLite.SurvivalToolsSettings";
        private const string HardcoreFieldName = "hardcoreMode";

        private static FieldInfo hardcoreMode;

        /// <summary>
        /// Опознаёт сломанный патч. Сам факт поломки проверяет BrokenPatchFix — здесь только
        /// «наш ли это метод» и есть ли всё нужное для замены.
        /// </summary>
        public static MethodInfo ReplacementFor(MethodInfo patch)
        {
            if (patch.Name != BrokenPrefixName)
                return null;
            if (patch.DeclaringType == null || patch.DeclaringType.FullName != PatchesTypeName)
                return null;

            if (hardcoreMode == null)
            {
                hardcoreMode = AccessTools.Field(AccessTools.TypeByName(SettingsTypeName), HardcoreFieldName);
                if (hardcoreMode == null || hardcoreMode.FieldType != typeof(bool) || !hardcoreMode.IsStatic)
                {
                    Log.Warning("[HSK Fixes 1.7] не найдено поле настроек " + SettingsTypeName + "."
                        + HardcoreFieldName + " — префикс Survival Tools Lite оставлен как есть.");
                    return null;
                }
            }

            return AccessTools.Method(typeof(STLWorkTagFix), nameof(HardcoreWorkTagPrefix));
        }

        /// <summary>Та же логика, правильная подпись.</summary>
        public static bool HardcoreWorkTagPrefix(ref bool __result)
        {
            try
            {
                if (!(bool)hardcoreMode.GetValue(null))
                    return true;
            }
            catch (Exception)
            {
                return true; // настройки недоступны — ведём себя как ванилла
            }

            __result = false;
            return false;
        }
    }
}
