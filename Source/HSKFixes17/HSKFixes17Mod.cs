using System;
using Verse;

namespace HSKFixes17
{
    /// <summary>
    /// Точка входа мода: правки, нужные Hardcore SK и модам его состава именно на RimWorld 1.7.
    ///
    /// Порядок в конструкторе не случаен. Он выполняется внутри CreateModClasses, и мод стоит до
    /// пака (loadBefore в About.xml), поэтому отсюда можно опередить игру: собрать Mod-класс Core SK
    /// и зарегистрировать его прежде, чем она позовёт их упавший конструктор, и поставить перехват
    /// Harmony до статических конструкторов Skynet, Minerals и Survival Tools Lite.
    ///
    ///     CEPresenceReport   одна строка в лог о том, что стоит на месте Combat Extended;
    ///     SKControllerRevive Mod-класс Core SK, который 1.7 не создаёт вовсе;
    ///     SKSettingsRevive   нижний слой того же: если подъём не удался, хотя бы настройки;
    ///     BrokenPatchFix     подмена патчей с несовместимым __instance;
    ///     PatchClassFix      то же для патчей, объявленных атрибутами и поставленных PatchAll;
    ///     AIRobotWorkTabFix  приоритеты роботов, которые терялись при загрузке сейва;
    ///     ResearchTreeFix    прокрутка дерева исследований, стиравшая картинку.
    ///
    /// Каждый шаг в своём try: падение одного не должно уносить остальные — ровно та ошибка,
    /// из-за которой всё это и понадобилось.
    /// </summary>
    public class HSKFixes17Mod : Mod
    {
        private static bool done;

        public HSKFixes17Mod(ModContentPack content) : base(content)
        {
            if (done)
                return;
            done = true;

            Run("отчёт о Combat Extended", CEPresenceReport.Report);
            Run("подъём Mod-класса Core SK", SKControllerRevive.ReviveIfDead);
            Run("заплатка настроек Core SK", SKSettingsRevive.Install);
            Run("подмена сломанных патчей", BrokenPatchFix.Install);
            Run("подмена сломанных патч-классов", PatchClassFix.Install);
            Run("приоритеты роботов", AIRobotWorkTabFix.Install);
            Run("дерево исследований", ResearchTreeFix.Install);
        }

        private static void Run(string what, Action step)
        {
            try
            {
                step();
            }
            catch (Exception e)
            {
                Log.Warning("[HSK Fixes 1.7] " + what + " не сработал: " + e.Message);
            }
        }
    }
}
