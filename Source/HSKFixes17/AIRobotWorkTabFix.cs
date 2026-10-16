using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HSKFixes17
{
    /// <summary>
    /// Misc Robots плюс Work Tab: приоритеты роботов из сейва выбрасывались.
    ///
    /// Что происходит. AIRobot зовёт EnableAndInitialize() прямо из конструктора своих
    /// Pawn_WorkSettings, то есть до того, как игра присвоит pawn.workSettings. Префикс Work Tab
    /// уводит каждый вызов SetPriority в свой трекер, а его сеттер в конце делает:
    ///
    ///     Pawn pawn = _parent.Pawn;
    ///     if (pawn != null)
    ///         pawn.workSettings.Notify_UseWorkPrioritiesChanged();   // workSettings ещё null
    ///
    /// pawn проверен, workSettings — нет. Отсюда две записи в логе, и обе про одно:
    ///
    ///     Thrown error while setting priority. This can be an issue with another mod!
    ///         Object reference not set to an instance of an object.
    ///         at WorkTab.WorkPriority.set_Item(Int32 hour, Int32 value)
    ///
    ///     Exception in LookDictionary(label=Priorities): System.InvalidOperationException:
    ///         Tried to add different values for the same key.
    ///         at WorkTab.PriorityManager.ExposeData()
    ///
    /// Вторая — настоящая потеря данных, и видно это только вместе с первой. Словарь
    /// PriorityManager.priorities статический, а индексатор PriorityManager[pawn] создаёт трекер
    /// на месте, если его нет. Конструктор робота срабатывает при загрузке карты, то есть раньше
    /// ResolveAllCrossReferences, и к моменту разбора сейва в словаре уже лежит свежий трекер с
    /// нулями. Scribe_Collections.BuildDictionary словарь не чистит, добавить второе значение для
    /// того же ключа не может — и сохранённые приоритеты робота пропадают. Строк ровно столько,
    /// сколько роботов: catch в BuildDictionary стоит на каждом элементе, а AIRobot глушит свои
    /// повторы счётчиком countLogPriority.
    ///
    /// Что делает правка. Префикс на сам префикс Work Tab: пока pawn.workSettings не указывает на
    /// этот объект — то есть пока он ещё строится, — зеркалить приоритет в Work Tab не нужно, и мы
    /// возвращаем false. Оригинальный Pawn_WorkSettings.SetPriority при этом выполняется как
    /// обычно, а ванильное значение AIRobot и так уже записал сам: его SetPriority сперва пишет
    /// prioritiesReflect[w], и только потом зовёт базовый метод, ради чужих патчей. Так что
    /// терять нечего, а трекер Work Tab создаётся позже — из сейва, если он там есть, иначе
    /// лениво из ванильных приоритетов, как у Work Tab и задумано.
    ///
    /// Clamp до 4 повторён, потому что он в том же префиксе: Work Tab разрешает приоритеты выше
    /// ванильных и обрезает их перед передачей в оригинал.
    ///
    /// Порядок установки неважен. Мы патчим метод, а сгенерированная Harmony обёртка зовёт его по
    /// MethodInfo, поэтому наш перехват срабатывает и если Work Tab поставит свой патч позже.
    ///
    /// ВНИМАНИЕ: на 14.10.2026 это НЕ РАБОТАЕТ, и обе ошибки остаются в логе. Префикс встаёт —
    /// Harmony перечисляет его в стеке, — но пропускает вызов дальше:
    ///
    ///     - PREFIX linya.hskfixes17: WorkTab.Pawn_WorkSettings_SetPriority.Prefix <- ...
    ///       SkipWhileWorkSettingsUnassigned
    ///     at WorkTab.Pawn_Extensions.SetPriority(...)      <- тело Work Tab всё равно выполнилось
    ///
    /// Значит условие ниже вернуло true, то есть pawn.workSettings уже указывал на создаваемый
    /// объект, хотя по моей модели времени он ещё должен быть null. Модель неверна.
    ///
    /// Первое, что стоит проверить при возврате к задаче: не делает ли ванильный конструктор
    /// Pawn_WorkSettings(Pawn pawn) на 1.7 присваивание pawn.workSettings = this. Если да, условие
    /// бессмысленно по построению и признак «объект ещё строится» надо искать другой — например по
    /// тому, инициализирован ли уже массив приоритетов.
    ///
    /// Оставлено намеренно: вреда нет (в рабочем случае префикс возвращает true и ничего не
    /// меняет), а разбор ещё не закончен. Цена проблемы — три строки в логе и сброшенные
    /// приоритеты роботов из сейва.
    /// </summary>
    public static class AIRobotWorkTabFix
    {
        private const string HarmonyId = "linya.hskfixes17";
        private const string WorkTabPrefix = "WorkTab.Pawn_WorkSettings_SetPriority:Prefix";

        private static FieldInfo pawnField;

        public static void Install()
        {
            MethodInfo target = AccessTools.Method(WorkTabPrefix);
            if (target == null)
                return; // Work Tab не установлен — нечего и чинить

            pawnField = AccessTools.Field(typeof(Pawn_WorkSettings), "pawn");
            if (pawnField == null)
            {
                Log.Warning("[HSK Fixes 1.7] не найдено поле Pawn_WorkSettings.pawn —"
                    + " приоритеты роботов оставлены как есть.");
                return;
            }

            new Harmony(HarmonyId).Patch(target,
                new HarmonyMethod(typeof(AIRobotWorkTabFix), nameof(SkipWhileWorkSettingsUnassigned)));

            Log.Message("[HSK Fixes 1.7] префикс Work Tab к Pawn_WorkSettings.SetPriority обёрнут:"
                + " пока объект настроек ещё строится (так делает Misc Robots), приоритет не"
                + " зеркалится в Work Tab. Иначе робот получал трекер с нулями до разбора сейва, и"
                + " сохранённые приоритеты в него уже не попадали.");
        }

        /// <summary>
        /// Выполняется на каждом изменении приоритета в игре, поэтому тело короткое: одно чтение
        /// поля и сравнение ссылок.
        /// </summary>
        public static bool SkipWhileWorkSettingsUnassigned(Pawn_WorkSettings __instance,
            ref int priority)
        {
            Pawn pawn;
            try
            {
                pawn = pawnField.GetValue(__instance) as Pawn;
            }
            catch (Exception)
            {
                return true;
            }

            // Обычный случай: объект настроек уже принадлежит пешке.
            if (pawn != null && pawn.workSettings == __instance)
                return true;

            if (priority > 4)
                priority = 4;
            return false;
        }
    }
}
