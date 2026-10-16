using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HSKFixes17
{
    /// <summary>
    /// Поднимает Mod-класс Core SK, который на RimWorld 1.7 не создаётся.
    ///
    /// Конструктор SK.Controller начинается с двух вызовов
    /// UnityEngine.Application.SetStackTraceLogType — метода, которого в новом движке нет:
    ///
    ///     MissingMethodException: Method not found: 'Void UnityEngine.Application
    ///     .SetStackTraceLogType(UnityEngine.LogType, UnityEngine.StackTraceLogType)'
    ///
    /// Падает он на первой строке, то есть не делается вообще ничего из остального, а остального
    /// там весь C#-слой пака:
    ///
    ///     Instance, Settings                     17 файлов пака читают Controller.Settings;
    ///                                            без них SK.RaidHelperComponent сыпал
    ///                                            NullReferenceException каждые 250 тиков
    ///     ErrorcheckQueue                        три проверки дефов пака
    ///     HarmonyBase.InitPatches, ModPatches    собственно патчи Core SK
    ///     ErrorCheck, InitSkillDecay             две отложенные задачи
    ///
    /// Почему не патчим их конструктор. Любой патч Harmony — префикс, постфикс, финализатор —
    /// сперва читает и переносит IL оригинала, а там стоит вызов отсутствующего метода. Что
    /// сделает с таким вызовом Harmony на новом рантайме, заранее не известно; зато известно, что
    /// до первой строки конструктор дойти успевает, а дальше нет. Поэтому объект собирается сам:
    /// создаём экземпляр без вызова конструктора и повторяем то, что он делает после упавшей
    /// строки. Инстансных полей у Controller нет (проверено по IL) — только статика и Content
    /// базового Mod, который мы и выставляем.
    ///
    /// Каждый шаг в своём try: если патчи пака не встанут на 1.7, настройки всё равно останутся
    /// на месте, а в логе будет видно, что именно не вышло. Конструктор пака такой возможности не
    /// даёт — у него всё или ничего.
    ///
    /// Момент — наш собственный Mod-конструктор, то есть внутри CreateModClasses и раньше, чем
    /// игра дойдёт до SK.Controller: мод грузится до пака. Готовый объект регистрируется в
    /// runningModClasses, а игра создаёт Mod-класс только если его типа там ещё нет — значит их
    /// конструктор не вызывается вообще. Поэтому и красной строки "Error while instantiating a mod
    /// of type SK.Controller" в логе больше нет: она не скрыта, ей просто нечего сообщать.
    ///
    /// Фаза при этом ровно та, в которой пак делал всё это сам, — ни раньше, ни позже.
    /// Если порядок модов окажется другим и пак успеет первым, вызов повторится из хука
    /// SKSettingsRevive на LoadModXML. На 1.6, где конструктор работает, Instance не null и метод
    /// сразу выходит.
    ///
    /// Это включает патчи Core SK на новом движке — то, что раньше было сознательно отложено.
    /// Если они окажутся вредны, выключается удалением одного вызова в HSKFixesMod.
    /// </summary>
    public static class SKControllerRevive
    {
        private const string ControllerTypeName = "SK.Controller";
        private const string SettingsTypeName = "SK.ProjectSettings";
        private const string HarmonyBaseTypeName = "SK.HarmonyBase";
        private const string ModPatchesTypeName = "SK.ModPatches";

        private static bool done;

        public static void ReviveIfDead()
        {
            if (done)
                return;

            Type controller = AccessTools.TypeByName(ControllerTypeName);
            if (controller == null)
                return; // пака нет — не наше дело

            PropertyInfo instanceProperty = AccessTools.Property(controller, "Instance");
            if (instanceProperty == null || instanceProperty.GetValue(null, null) != null)
                return; // конструктор отработал, как на 1.6

            done = true;

            object instance = CreateWithoutConstructor(controller);
            if (instance == null)
                return;

            if (!SetContent(instance, controller.Assembly))
                return;

            instanceProperty.GetSetMethod(true)?.Invoke(null, new[] { instance });
            Register(controller, instance);

            object settings = LoadSettings(controller, instance);
            EnqueueErrorChecks(controller, instance);

            if (settings != null && ModsConfig.ActiveModsInLoadOrder.Any(
                    mod => mod.Name != null && mod.Name.Contains("ModStreamer")))
            {
                AccessTools.Field(settings.GetType(), "makeIncidentDesperate")?.SetValue(settings, true);
            }

            ApplyPackPatches(controller);
            RunStatic(ModPatchesTypeName, "Init", "правки Core SK под другие моды");

            QueueLongEvent(controller, instance, "ErrorCheck");
            QueueLongEvent(controller, instance, "InitSkillDecay");

            Log.Message("[HSK Fixes 1.7] Mod-класс Core SK поднят вручную: на 1.7 его конструктор"
                + " падает на первой строке (нет UnityEngine.Application.SetStackTraceLogType) и не"
                + " делает ничего — ни настроек, ни патчей пака. Собран объект, прочитаны настройки"
                + " игрока, применены патчи Core SK.");
        }

        /// <summary>
        /// Экземпляр без конструктора. Метод ищется рефлексией: в .NET Framework это
        /// FormatterServices.GetUninitializedObject, в современном — RuntimeHelpers с тем же
        /// именем, и жёсткая ссылка на любой из них сломалась бы на другом рантайме.
        /// </summary>
        private static object CreateWithoutConstructor(Type type)
        {
            string[] holders =
            {
                "System.Runtime.CompilerServices.RuntimeHelpers",
                "System.Runtime.Serialization.FormatterServices",
            };

            foreach (string holder in holders)
            {
                MethodInfo method = AccessTools.TypeByName(holder)
                    ?.GetMethod("GetUninitializedObject", BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                    continue;
                try
                {
                    object instance = method.Invoke(null, new object[] { type });
                    if (instance != null)
                        return instance;
                }
                catch (Exception e)
                {
                    Log.Warning("[HSK Fixes 1.7] " + holder + ".GetUninitializedObject не сработал: "
                        + (e.InnerException ?? e).Message);
                }
            }

            Log.Error("[HSK Fixes 1.7] не удалось создать экземпляр " + type.FullName
                + " без конструктора — Mod-класс Core SK остаётся не поднятым.");
            return null;
        }

        /// <summary>
        /// Базовый Mod хранит ModContentPack в приватном поле intContent, и его выставляет
        /// именно конструктор. Без этого не прочитать настройки: GetSettings берёт оттуда
        /// FolderName.
        /// </summary>
        private static bool SetContent(object instance, Assembly assembly)
        {
            ModContentPack owner = LoadedModManager.RunningMods.FirstOrDefault(
                mod => mod.assemblies?.loadedAssemblies != null
                    && mod.assemblies.loadedAssemblies.Contains(assembly));
            FieldInfo field = AccessTools.Field(typeof(Mod), "intContent");

            if (owner == null || field == null)
            {
                Log.Error("[HSK Fixes 1.7] не найден ModContentPack пака или поле Mod.intContent —"
                    + " Mod-класс Core SK остаётся не поднятым.");
                return false;
            }

            field.SetValue(instance, owner);
            return true;
        }

        /// <summary>
        /// Регистрируем в runningModClasses: оттуда игра берёт список Mod-классов для окна
        /// настроек, и оттуда же их достаёт LoadedModManager.GetMod.
        /// </summary>
        private static void Register(Type controller, object instance)
        {
            try
            {
                var dictionary = AccessTools.Field(typeof(LoadedModManager), "runningModClasses")
                    ?.GetValue(null) as IDictionary;
                if (dictionary != null && !dictionary.Contains(controller))
                    dictionary[controller] = instance;
            }
            catch (Exception e)
            {
                Log.Warning("[HSK Fixes 1.7] не удалось зарегистрировать Mod-класс Core SK: " + e.Message);
            }
        }

        /// <summary>Settings = GetSettings&lt;ProjectSettings&gt;(), как в их конструкторе.</summary>
        private static object LoadSettings(Type controller, object instance)
        {
            try
            {
                Type settingsType = AccessTools.TypeByName(SettingsTypeName);
                FieldInfo field = AccessTools.Field(controller, "Settings");
                MethodInfo getSettings = AccessTools.Method(typeof(Mod), "GetSettings")
                    ?.MakeGenericMethod(settingsType);
                if (settingsType == null || field == null || getSettings == null)
                    return null;

                object settings = getSettings.Invoke(instance, null);
                field.SetValue(null, settings);
                return settings;
            }
            catch (Exception e)
            {
                Log.Error("[HSK Fixes 1.7] не удалось прочитать настройки Core SK: "
                    + (e.InnerException ?? e).Message);
                return null;
            }
        }

        /// <summary>Три проверки дефов, которые конструктор кладёт в свою очередь.</summary>
        private static void EnqueueErrorChecks(Type controller, object instance)
        {
            try
            {
                var queue = AccessTools.Field(controller, "ErrorcheckQueue")?.GetValue(null)
                    as Queue<Func<string>>;
                if (queue == null)
                    return;

                foreach (string name in new[] { "PlaceWorker", "DefaultStuff", "DuplicateCompReportWorkspeed" })
                {
                    MethodInfo method = AccessTools.Method(controller, name);
                    if (method != null)
                        queue.Enqueue((Func<string>)Delegate.CreateDelegate(typeof(Func<string>), instance, method));
                }
            }
            catch (Exception e)
            {
                Log.Warning("[HSK Fixes 1.7] проверки дефов Core SK не поставлены в очередь: " + e.Message);
            }
        }

        /// <summary>
        /// То же, что SK.HarmonyBase.InitPatches, но классами по одному.
        ///
        /// У пака там instance.PatchAll(своя сборка) плюс SpecialPatches.Init(). Harmony.PatchAll
        /// идёт по типам сборки и бросает на первом же классе, который не встал, — остаток списка
        /// теряется целиком. Так и вышло: SK.CEPatchPatch просил тип CE, которого не было в
        /// заглушке, и ни один патч Core SK не применился. Тип добавлен, но причина общая: на новом
        /// движке любой класс может не встать, и из-за одного терять все остальные не стоит.
        ///
        /// Поэтому проходим те же типы в том же порядке (AccessTools.GetTypesFromAssembly — то же,
        /// что внутри PatchAll) и зовём на каждом CreateClassProcessor().Patch() в своём try.
        /// Фильтр по атрибуту обязателен и воспроизведён ниже: сам процессор неаннотированный тип
        /// НЕ пропускает — почему, написано у HasHarmonyAttribute. Не воспроизводится только их
        /// отчёт о HarmonyDebug — это диагностика для разработчиков пака, не патчи.
        ///
        /// Если Harmony не отдаст свой экземпляр, зовём InitPatches как есть — лучше хрупко, чем
        /// никак.
        /// </summary>
        private static void ApplyPackPatches(Type controller)
        {
            Harmony harmony = null;
            try
            {
                harmony = AccessTools.Property(AccessTools.TypeByName(HarmonyBaseTypeName), "instance")
                    ?.GetValue(null, null) as Harmony;
            }
            catch (Exception e)
            {
                Log.Warning("[HSK Fixes 1.7] не удалось получить Harmony пака: " + e.Message);
            }

            if (harmony == null)
            {
                RunStatic(HarmonyBaseTypeName, "InitPatches", "патчи Core SK");
                return;
            }

            int applied = 0;
            var failed = new List<string>();
            foreach (Type type in AccessTools.GetTypesFromAssembly(controller.Assembly))
            {
                if (!HasHarmonyAttribute(type))
                    continue;

                try
                {
                    if (harmony.CreateClassProcessor(type).Patch() != null)
                        applied++;
                }
                catch (Exception e)
                {
                    failed.Add(type.FullName + " (" + (e.InnerException ?? e).Message + ")");
                }
            }

            if (failed.Count == 0)
            {
                Log.Message("[HSK Fixes 1.7] патчи Core SK применены: " + applied + " классов.");
            }
            else
            {
                Log.Warning("[HSK Fixes 1.7] патчи Core SK применены частично: " + applied
                    + " классов встали, " + failed.Count + " нет. Остальные при этом не потеряны —"
                    + " у пака одно исключение убрало бы весь остаток списка. Не встали:"
                    + Environment.NewLine + "  "
                    + string.Join(Environment.NewLine + "  ", failed.ToArray()));
            }

            RunStatic("SK.SpecialPatches", "Init", "особые патчи Core SK");
        }

        /// <summary>
        /// Фильтр PatchAll: только классы с атрибутом Harmony. Пропустить его нельзя, и это не
        /// придирка — PatchClassProcessor в новой Harmony не выходит на неаннотированном типе, а
        /// сразу ищет у него вспомогательные методы по имени. У любого наследника JobDriver
        /// находится унаследованный инстансный Cleanup(JobCondition), который Harmony зовёт как
        /// статический, и получается "Non-static method requires a target." Без фильтра так
        /// «падали» 50 джоб-драйверов пака и два приватных вложенных класса, которых PatchAll
        /// никогда и не касался.
        ///
        /// Своя проверка, а не HarmonyMethodExtensions.HasHarmonyAttribute: в игре 1.6 стоит
        /// Harmony 2.3.6, против которой собран проект, и там этого метода ещё нет (в 2.4.1 из
        /// копии 1.7 — есть).
        ///
        /// Условие совпадает в главном: оригинал — это GetFromType(type).Count > 0, а GetFromType
        /// берёт type.GetCustomAttributes(inherit: true). То есть смотрятся атрибуты **самого
        /// типа**, а не его методов, — класс без class-level [HarmonyPatch] PatchAll не видит, и мы
        /// тоже. Патчей это не теряет: на Core_SK.dll счёт сошёлся, 120 классов.
        ///
        /// Отличие одно, и нам оно безразлично. Harmony опознаёт атрибут «по утиному»: ищет поле
        /// info, у которого FieldType.FullName == "HarmonyLib.HarmonyMethod", без оглядки на
        /// сборку. Так узнаются атрибуты мода, принёсшего свою копию Harmony. Наше
        /// `is HarmonyAttribute` такие бы отвергло — но мы применяем фильтр только к сборке пака,
        /// а она ссылается на ту же Harmony, что загружена.
        /// </summary>
        private static bool HasHarmonyAttribute(Type type)
        {
            try
            {
                object[] attributes = type.GetCustomAttributes(true);
                for (int i = 0; i < attributes.Length; i++)
                {
                    if (attributes[i] is HarmonyAttribute)
                        return true;
                }
            }
            catch (Exception)
            {
                // тип с неразрешимым атрибутом — не наш патч-класс
            }
            return false;
        }

        private static void RunStatic(string typeName, string methodName, string what)
        {
            try
            {
                MethodInfo method = AccessTools.Method(AccessTools.TypeByName(typeName), methodName);
                if (method == null)
                {
                    Log.Warning("[HSK Fixes 1.7] не найден " + typeName + "." + methodName
                        + " — " + what + " не применены.");
                    return;
                }
                method.Invoke(null, null);
            }
            catch (Exception e)
            {
                Log.Error("[HSK Fixes 1.7] " + what + " не применены (" + typeName + "."
                    + methodName + "): " + (e.InnerException ?? e));
            }
        }

        /// <summary>
        /// Отложенная задача, как в их конструкторе. Аргументы собираются по фактической подписи
        /// QueueLongEvent: на новом движке у неё может быть другое число параметров, а имя и
        /// первые два аргумента стабильны.
        /// </summary>
        private static void QueueLongEvent(Type controller, object instance, string methodName)
        {
            try
            {
                MethodInfo target = AccessTools.Method(controller, methodName);
                if (target == null)
                    return;

                var action = (Action)Delegate.CreateDelegate(typeof(Action), instance, target);

                MethodInfo queue = typeof(LongEventHandler).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "QueueLongEvent"
                        && m.GetParameters().Length >= 2
                        && m.GetParameters()[0].ParameterType == typeof(Action)
                        && m.GetParameters()[1].ParameterType == typeof(string));
                if (queue == null)
                    return;

                ParameterInfo[] parameters = queue.GetParameters();
                object[] args = new object[parameters.Length];
                args[0] = action;
                args[1] = methodName;
                for (int i = 2; i < parameters.Length; i++)
                {
                    args[i] = parameters[i].HasDefaultValue
                        ? parameters[i].DefaultValue
                        : (parameters[i].ParameterType.IsValueType
                            ? Activator.CreateInstance(parameters[i].ParameterType)
                            : null);
                }

                queue.Invoke(null, args);
            }
            catch (Exception e)
            {
                Log.Warning("[HSK Fixes 1.7] задача " + methodName + " Core SK не поставлена: "
                    + (e.InnerException ?? e).Message);
            }
        }
    }
}
