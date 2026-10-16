using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace HSKFixes17
{
    /// <summary>
    /// То же, что BrokenPatchFix, но для патчей, объявленных атрибутами и поставленных через
    /// PatchAll.
    ///
    /// Почему нужен второй перехват. BrokenPatchFix висит на Harmony.Patch, а PatchAll туда не
    /// заходит: он идёт по типам сборки и зовёт CreateClassProcessor(type).Patch(). Так патчат
    /// Rimatomics и Dubs Mint Menus, и у обоих нашлись классы с тем же изъяном — __instance
    /// объявлен типом, которого патчимый метод не предоставляет:
    ///
    ///     Rimatomics     Harmony_Site_ShouldRemoveMapNow и
    ///                    Harmony_SettlementBase_ShouldRemoveMapNow: оба Postfix берут
    ///                    DestroyedSettlement, а патчат Site и Settlement. Тип скопирован из
    ///                    третьего класса, Harmony_DestroyedSettlement_ShouldRemoveMapNow, где он
    ///                    верен. Падает весь статический конструктор мода.
    ///     DubsMintMenus  Patch_InterfaceTryActivate.Prefix берёт MainButtonWorker, а патчит
    ///                    MainTabsRoot.HandleLowPriorityShortcuts. Тоже уносит конструктор.
    ///
    /// Как работает. Префикс на PatchClassProcessor.Patch читает приватное поле containerType —
    /// класс, который сейчас обрабатывается. Если это один из известных, мы ставим исправленный
    /// патч сами, их же Harmony-экземпляром, и возвращаем false: их регистрация не выполняется и
    /// падать нечему. Остальные классы идут обычным путём.
    ///
    /// Сопоставление идёт по имени типа и имени сборки, а не по полному имени: у Dubs класс вложен
    /// в HarmonyPatches, и FullName выглядит как DubsMintMenus.HarmonyPatches+Patch_InterfaceTryActivate,
    /// тогда как Harmony в сообщении об ошибке печатает его без обрамляющего класса. На этом я один
    /// раз уже промахнулся — запись не срабатывала молча.
    ///
    /// Замены повторяют логику оригиналов. У Dubs __instance не используется вовсе, поэтому
    /// вызывается их же метод с null на этом месте — логика остаётся их целиком. У Rimatomics
    /// __instance нужен, но только ради Map, а для этого довольно MapParent, от которого происходят
    /// и Site, и Settlement; тело воспроизведено: пока на карте есть удар ICBM, карту не удалять.
    /// </summary>
    public static class PatchClassFix
    {
        private const string HarmonyId = "linya.hskfixes17";

        private static FieldInfo containerTypeField;
        private static FieldInfo harmonyField;
        private static MethodInfo dubsPrefix;
        private static FieldInfo icbmStrike;

        /// <summary>Класс патчей, который мы ставим за мод сами.</summary>
        private sealed class Entry
        {
            public string Assembly;
            public string TypeName;
            public Func<Harmony, bool> Apply;
        }

        private static readonly Entry[] Entries =
        {
            new Entry
            {
                Assembly = "Rimatomics", TypeName = "Harmony_Site_ShouldRemoveMapNow",
                Apply = h => KeepMapPatch(h, typeof(Site), "Rimatomics.Harmony_Site_ShouldRemoveMapNow"),
            },
            new Entry
            {
                Assembly = "Rimatomics", TypeName = "Harmony_SettlementBase_ShouldRemoveMapNow",
                Apply = h => KeepMapPatch(h, typeof(Settlement), "Rimatomics.Harmony_SettlementBase_ShouldRemoveMapNow"),
            },
            new Entry
            {
                Assembly = "DubsMintMenus", TypeName = "Patch_InterfaceTryActivate",
                Apply = FixMintMenusShortcut,
            },
        };

        public static void Install()
        {
            MethodInfo patch = AccessTools.Method(typeof(PatchClassProcessor), nameof(PatchClassProcessor.Patch));
            containerTypeField = AccessTools.Field(typeof(PatchClassProcessor), "containerType");
            harmonyField = AccessTools.Field(typeof(PatchClassProcessor), "instance");

            if (patch == null || containerTypeField == null || harmonyField == null)
            {
                Log.Warning("[HSK Fixes 1.7] не найдены внутренности PatchClassProcessor —"
                    + " подмены атрибутных патчей не будет.");
                return;
            }

            new Harmony(HarmonyId).Patch(patch,
                new HarmonyMethod(typeof(PatchClassFix), nameof(ReplaceKnownBrokenClass)));
        }

        /// <summary>
        /// Выполняется на каждом патч-классе в игре, поэтому сперва самая дешёвая проверка —
        /// простое имя типа.
        /// </summary>
        private static bool ReplaceKnownBrokenClass(PatchClassProcessor __instance,
            ref List<MethodInfo> __result)
        {
            Type container;
            Harmony harmony;
            try
            {
                container = containerTypeField.GetValue(__instance) as Type;
                harmony = harmonyField.GetValue(__instance) as Harmony;
            }
            catch (Exception)
            {
                return true;
            }

            if (container == null || harmony == null)
                return true;

            for (int i = 0; i < Entries.Length; i++)
            {
                Entry entry = Entries[i];
                if (container.Name != entry.TypeName)
                    continue;
                if (container.Assembly.GetName().Name != entry.Assembly)
                    continue;
                if (!entry.Apply(harmony))
                    return true;

                __result = new List<MethodInfo>();
                return false;
            }
            return true;
        }

        private static bool KeepMapPatch(Harmony harmony, Type owner, string patchClass)
        {
            if (icbmStrike == null)
            {
                icbmStrike = AccessTools.Field(AccessTools.TypeByName("Rimatomics.DubDef"), "ICBMStrike");
                if (icbmStrike == null)
                {
                    Log.Warning("[HSK Fixes 1.7] не найдено Rimatomics.DubDef.ICBMStrike — класс "
                        + patchClass + " оставлен как есть.");
                    return false;
                }
            }

            // Именно DeclaredMethod: у Site и Settlement свои переопределения, и патчить нужно их,
            // а не базовый MapParent.ShouldRemoveMapNow.
            MethodInfo target = AccessTools.DeclaredMethod(owner, "ShouldRemoveMapNow");
            if (target == null)
            {
                Log.Warning("[HSK Fixes 1.7] не найден " + owner.Name + ".ShouldRemoveMapNow — класс "
                    + patchClass + " оставлен как есть.");
                return false;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(typeof(PatchClassFix), nameof(KeepMapWithIcbmStrike)));
            Report(patchClass, owner.Name + ".ShouldRemoveMapNow");
            return true;
        }

        /// <summary>Их логика: пока на карте есть удар ICBM, карту удалять нельзя.</summary>
        public static void KeepMapWithIcbmStrike(MapParent __instance, ref bool __result)
        {
            if (!__result)
                return;

            ThingDef strike = icbmStrike.GetValue(null) as ThingDef;
            Map map = __instance?.Map;
            if (strike == null || map == null)
                return;

            if (map.listerThings.ThingsOfDef(strike).Count > 0)
                __result = false;
        }

        private static bool FixMintMenusShortcut(Harmony harmony)
        {
            if (dubsPrefix == null)
            {
                dubsPrefix = AccessTools.Method("DubsMintMenus.HarmonyPatches+Patch_InterfaceTryActivate:Prefix")
                    ?? AccessTools.Method("DubsMintMenus.Patch_InterfaceTryActivate:Prefix");
                if (dubsPrefix == null)
                {
                    Log.Warning("[HSK Fixes 1.7] не найден Patch_InterfaceTryActivate.Prefix у Dubs Mint Menus —"
                        + " класс оставлен как есть.");
                    return false;
                }
            }

            MethodInfo target = AccessTools.Method(typeof(MainTabsRoot), "HandleLowPriorityShortcuts");
            if (target == null)
                return false;

            harmony.Patch(target, new HarmonyMethod(typeof(PatchClassFix), nameof(MintWheelShortcut)));
            Report("DubsMintMenus.Patch_InterfaceTryActivate", "MainTabsRoot.HandleLowPriorityShortcuts");
            return true;
        }

        /// <summary>
        /// Их же метод, вызванный с null вместо неиспользуемого __instance: логика остаётся
        /// полностью их, включая чтение их настроек.
        /// </summary>
        public static bool MintWheelShortcut()
        {
            try
            {
                return (bool)dubsPrefix.Invoke(null, new object[] { null });
            }
            catch (Exception e)
            {
                Log.WarningOnce("[HSK Fixes 1.7] префикс Dubs Mint Menus завершился ошибкой: "
                    + (e.InnerException ?? e).Message, 0x4D4D4D01);
                return true;
            }
        }

        private static void Report(string patchClass, string target)
        {
            Log.Message("[HSK Fixes 1.7] класс патчей " + patchClass + " заменён своим на " + target
                + ": в оригинале __instance объявлен несовместимым типом, и Harmony 1.7 отвергала"
                + " его вместе со всем статическим конструктором мода. Логика сохранена.");
        }
    }
}
