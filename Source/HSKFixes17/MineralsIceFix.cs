using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace HSKFixes17
{
    /// <summary>
    /// Замена сломанного постфикса Minerals — мода из списка пака (его сборка даже использует SK).
    ///
    /// Что в логе:
    ///
    ///     Error in static constructor of Minerals.HarmonyPatches: ... Unrelated classes cannot
    ///     describe the same non-null object: original virtual Void
    ///     RimWorld.GenStep_Plants::Generate(Map map, GenStepParams parms), patch static Void
    ///     Minerals.HarmonyPatches::initNewMapIce(GenStep_RockChunks __instance, Map map)
    ///     ... supplied RimWorld.GenStep_Plants, requested RimWorld.GenStep_RockChunks
    ///
    /// Их конструктор ставит три вещи подряд:
    ///
    ///     1. постфикс initNewMapRocks на GenStep_RockChunks.Generate — тип совпадает, встаёт
    ///     2. постфикс initNewMapIce  на GenStep_Plants.Generate      — здесь исключение
    ///     3. PatchAll()                                              — не выполняется вовсе
    ///
    /// Третий пункт дороже второго: в PatchAll уходит вся атрибутная часть мода — подмена
    /// минералов в падающих скайфоллерах (SkyfallerUtil.SpawnSkyfaller) и перехват
    /// GenStep_PreciousLump.Generate. То есть из-за одного неверного типа в подписи мод терял
    /// почти весь свой C#-слой, а не только лёд.
    ///
    /// Подмена тривиальна и ничего не изобретает: __instance в их методе не используется — тело
    /// целиком это `mapBuilder.initIce(map)`. Поэтому наш постфикс берёт только map и вызывает их
    /// же метод, передавая null вместо неиспользуемого __instance. Логика остаётся их.
    /// </summary>
    public static class MineralsIceFix
    {
        private const string PatchesTypeName = "Minerals.HarmonyPatches";
        private const string BrokenPostfixName = "initNewMapIce";

        private static MethodInfo original;

        /// <summary>Опознаёт сломанный патч; сам факт поломки проверяет BrokenPatchFix.</summary>
        public static MethodInfo ReplacementFor(MethodInfo patch)
        {
            if (patch.Name != BrokenPostfixName)
                return null;
            if (patch.DeclaringType == null || patch.DeclaringType.FullName != PatchesTypeName)
                return null;

            // Передаём null — значит сначала убедимся, что __instance действительно не нужен:
            // параметров ровно два и второй это Map.
            ParameterInfo[] parameters = patch.GetParameters();
            if (parameters.Length != 2 || parameters[1].ParameterType != typeof(Map))
                return null;

            original = patch;
            return AccessTools.Method(typeof(MineralsIceFix), nameof(IcePostfix));
        }

        /// <summary>Их же метод, без неиспользуемого __instance.</summary>
        public static void IcePostfix(Map map)
        {
            try
            {
                original?.Invoke(null, new object[] { null, map });
            }
            catch (Exception e)
            {
                Log.Warning("[HSK Fixes 1.7] Minerals.initNewMapIce завершился ошибкой: "
                    + (e.InnerException ?? e).Message);
            }
        }
    }
}
