using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace HSKFixes17
{
    /// <summary>
    /// Заплатка не про Combat Extended: восстанавливает настройки Core SK, когда его Mod-класс
    /// не смог создаться на RimWorld 1.7.
    ///
    /// Что происходит. Конструктор SK.Controller начинается с двух вызовов
    /// UnityEngine.Application.SetStackTraceLogType — в 1.7 этого метода в Unity-прослойке нет,
    /// и конструктор падает на первой же строке:
    ///
    ///     MissingMethodException: Method not found:
    ///     'Void UnityEngine.Application.SetStackTraceLogType(UnityEngine.LogType, UnityEngine.StackTraceLogType)'
    ///
    /// Поэтому Controller.Settings остаётся null, а его читают 17 файлов пака. Например
    /// SK.RaidHelperComponent держит поле `private ProjectSettings c = Controller.Settings` и
    /// обращается к нему в MapComponentTick — то есть NullReferenceException каждые 250 тиков,
    /// всю игру.
    ///
    /// Что делает заплатка. После CreateModClasses, если Settings так и не появились, читает их
    /// с диска тем же способом, что и ванильный Mod.GetSettings: ReadModSettings с именем папки
    /// мода и именем класса. Настройки игрока при этом не теряются — берётся его же файл.
    ///
    /// Теперь это нижний слой: сам Mod-класс поднимает SKControllerRevive, и он же применяет
    /// патчи пака. Этот файл остаётся на случай, если подъём не удался — тогда настройки всё
    /// равно будут на месте, и пак хотя бы перестанет сыпать NullReferenceException.
    ///
    /// Ставится безусловно, в отличие от остального в этом моде: ошибка возникает и с CE, и без
    /// него. Жить ей тут до тех пор, пока у HSKFixes не появится ветка 1.7 — там её настоящее
    /// место, а вырезание CE — временная мера.
    /// </summary>
    public static class SKSettingsRevive
    {
        private const string ControllerTypeName = "SK.Controller";
        private const string SettingsTypeName = "SK.ProjectSettings";
        private const string SettingsFieldName = "Settings";

        /// <summary>
        /// Момент важен. Наш Mod-конструктор выполняется внутри CreateModClasses, поэтому
        /// вешать патч на него бесполезно: Harmony подменяет метод, но уже идущий вызов
        /// доходит до конца по старому коду, и постфикс для этого запуска не срабатывает
        /// (на этом я один раз уже ошибся). LoadModXML вызывается следующим и ещё не начат,
        /// а конструктор Core SK к тому времени отработал — или упал.
        ///
        /// Этого момента хватает всем, кто читает настройки: до разбора XML, до проверки
        /// дефов (где падает SkillDecayExtension.ConfigErrors) и задолго до карты.
        /// </summary>
        public static void Install()
        {
            var harmony = new Harmony("linya.hskfixes17");
            harmony.Patch(
                AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.LoadModXML)),
                new HarmonyMethod(typeof(SKSettingsRevive), nameof(ReviveIfDead)));
        }

        /// <summary>Повторный вызов безвреден: если настройки на месте, метод сразу выходит.</summary>
        public static void ReviveIfDead()
        {
            // Сначала полный подъём Mod-класса: он выставит и настройки, и патчи пака. Если он
            // почему-то не вышел, ниже остаётся прежняя узкая заплатка — только настройки.
            SKControllerRevive.ReviveIfDead();

            try
            {
                Type controller = AccessTools.TypeByName(ControllerTypeName);
                if (controller == null)
                    return; // пака нет — не наше дело

                FieldInfo settingsField = AccessTools.Field(controller, SettingsFieldName);
                if (settingsField == null || settingsField.GetValue(null) != null)
                    return; // конструктор отработал, как на 1.6

                Type settingsType = AccessTools.TypeByName(SettingsTypeName);
                if (settingsType == null)
                    return;

                string folderName = FolderNameOf(controller.Assembly);
                if (folderName == null)
                    return;

                MethodInfo read = AccessTools.Method(typeof(LoadedModManager), "ReadModSettings")
                    ?.MakeGenericMethod(settingsType);
                object settings = read?.Invoke(null, new object[] { folderName, controller.Name });
                if (settings == null)
                    return;

                settingsField.SetValue(null, settings);
                Log.Message("[HSK Fixes 1.7] настройки Core SK восстановлены: его Mod-класс не создался"
                    + " на 1.7 (нет UnityEngine.Application.SetStackTraceLogType), и без них пак"
                    + " сыпал NullReferenceException каждые 250 тиков. Патчи Core SK при этом"
                    + " по-прежнему не применены — это отдельная задача.");
            }
            catch (Exception e)
            {
                Log.Warning("[HSK Fixes 1.7] не удалось восстановить настройки Core SK: " + e.Message);
            }
        }

        /// <summary>Имя папки мода, которому принадлежит сборка — его ждёт ReadModSettings.</summary>
        private static string FolderNameOf(Assembly assembly)
        {
            foreach (ModContentPack mod in LoadedModManager.RunningMods)
            {
                var loaded = mod.assemblies?.loadedAssemblies;
                if (loaded != null && loaded.Contains(assembly))
                    return mod.FolderName;
            }
            return null;
        }
    }
}
