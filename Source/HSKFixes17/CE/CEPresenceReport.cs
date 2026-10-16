using System.Reflection;
using Verse;

namespace HSKFixes17
{
    /// <summary>
    /// Одна строка в лог о том, что мод видит на месте Combat Extended.
    ///
    /// Зачем она здесь, а не в NoCE. Сборка из NoCE грузится только при отсутствующей CE, поэтому
    /// с установленной CE мод молчал вовсе: по логу нельзя было отличить «отошёл в сторону» от
    /// «не загрузился». Эта сборка грузится при активном паке в любом случае, так что отчёт её.
    ///
    /// Три случая, и каждый говорит разное:
    ///
    ///     заглушка          молчим — о ней уже сообщил NoCEMod, он же её и опознал
    ///     настоящая CE      одна строка: мод отошёл в сторону, работают только правки пака
    ///     ничего            ошибка: папка NoCE не подключилась, а CE нет. Дефы Core SK,
    ///                       называющие типы CE, не загрузятся, и это надо видеть
    ///
    /// Последний случай — то, что раньше ловил загрузчик в корне мода. Он оттуда убран (сам жил
    /// в NoCE и при её отсутствии не запускался бы), а проверка нужна, и теперь она в месте,
    /// которое грузится всегда.
    /// </summary>
    public static class CEPresenceReport
    {
        private const string ShimAssemblyName = "CombatExtended";
        private const string ShimMarkerType = "CombatExtended.PatchTransitionShim";

        public static void Report()
        {
            Assembly loaded = FindLoadedAssembly(ShimAssemblyName);

            if (loaded == null)
            {
                Log.Error("[HSK Fixes 1.7] сборки CombatExtended нет вовсе — ни настоящей CE, ни"
                    + " заглушки. Значит папка NoCE не подключилась: дефы Core SK, называющие типы"
                    + " CE, не загрузятся, и пак потеряет оружие, броню и патроны.");
                return;
            }

            if (loaded.GetType(ShimMarkerType) != null)
                return; // заглушка — о ней отчитывается NoCEMod

            Log.Message("[HSK Fixes 1.7] Combat Extended на месте — заглушка не используется."
                + " Мод делает только то, что лечит сам пак.");
        }

        /// <summary>
        /// По имени сборки, а не по packageId: у стимовских подписок к packageId дописывается
        /// _steam, и проверка по идентификатору промахивается.
        /// </summary>
        private static Assembly FindLoadedAssembly(string assemblyName)
        {
            foreach (var mod in LoadedModManager.RunningMods)
            {
                var list = mod.assemblies?.loadedAssemblies;
                if (list == null)
                    continue;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].GetName().Name == assemblyName)
                        return list[i];
                }
            }
            return null;
        }
    }
}
