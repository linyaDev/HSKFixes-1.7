using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace HSKFixes17
{
    /// <summary>
    /// Дерево исследований на 1.7: при прокрутке окно «стирается» — на месте уехавших узлов
    /// остаётся пустота.
    ///
    /// ПРИЧИНА — один отброшенный аргумент. Мод прокручивает дерево вызовом, скопированным из
    /// внутренностей Unity:
    ///
    ///     public static void BeginScrollView(Rect position, Vector2 scrollPosition, Rect viewRect)
    ///     {
    ///         PushAction(position,
    ///                    new Vector2(-scrollPosition.x - viewRect.x, -scrollPosition.y - viewRect.y),
    ///                    Vector2.zero, arg4: false);
    ///     }
    ///
    /// где PushAction — это UnityEngine.GUIClip.Push, добытый рефлексией. А заглушка 1.7 второй
    /// аргумент выбрасывает:
    ///
    ///     internal static void Internal_Push(Rect screenRect, Vector2 scrollOffset,
    ///                                        Vector2 renderOffset, bool resetOffset)
    ///     {
    ///         GuiBackend.Current?.PushGroup(screenRect);     // смещение не передано
    ///     }
    ///
    /// Прямоугольник обрезания ставится верно, содержимое не сдвигается. Дерево всегда рисуется в
    /// позиции прокрутки «ноль»: мод считает, что уехал, и рисует узлы нового участка, а пиксели
    /// остаются на месте — там, где узлов по его расчётам больше нет, остаётся фон.
    ///
    /// ЧТО ДЕЛАЕМ. Нужный примитив у движка есть, и ванильный скролл им пользуется:
    ///
    ///     Vector2 contentOffset = new Vector2(position.x - scrollPosition.x - viewRect.x,
    ///                                         position.y - scrollPosition.y - viewRect.y);
    ///     GuiBackend.Current?.PushScrollView(clipRect, contentOffset);
    ///
    /// Формула совпадает с намерением мода буквально, поэтому его BeginScrollView/EndScrollView
    /// перенаправлены на публичные GUI.BeginScrollView/GUI.EndScrollView. Полосы прокрутки
    /// запрошены стилем none — мод рисует свои сам, — а handleScrollWheel выключен, чтобы ванильный
    /// скролл не перехватывал колесо у мода.
    ///
    /// ПРО ЗУМ, которого здесь нет. Сперва я решил, что чернота от масштабирования: колесо в этом
    /// моде не прокручивает, а зумит, и зум сделан подменой GUI.matrix и UI.screenWidth. Масштаб
    /// был закреплён на 100% — и это не помогло, ползунки стирали картинку по-прежнему. Причина
    /// была выше, а колесо выглядело виноватым лишь потому, что HandleZoom заодно пересчитывает
    /// ScrollPosition, то есть тоже упиралось в потерянное смещение.
    ///
    /// Закрепление снято, и теперь понятно, что оно и не требовалось: GUI.matrix на 1.7 не
    /// игнорируется — сеттер передаёт её движку, а SetMatrix объявлен в интерфейсе бэкенда
    /// (UnityStubs.Gui.IGuiBackend) наравне с PushGroup, PushScrollView и PushClipRect.
    ///
    /// Чинить по-настоящему должен автор мода: в его About.xml заявлена только 1.6.
    /// </summary>
    public static class ResearchTreeFix
    {
        private const string HarmonyId = "linya.hskfixes17";
        private const string ClipUtilityTypeName = "ResearchTreeSK.GUIClipUtility";

        public static void Install()
        {
            Type clip = AccessTools.TypeByName(ClipUtilityTypeName);
            if (clip == null)
                return; // мод не установлен — нечего и чинить

            MethodInfo begin = AccessTools.Method(clip, "BeginScrollView");
            MethodInfo end = AccessTools.Method(clip, "EndScrollView");
            if (begin == null || end == null)
            {
                Log.Warning("[HSK Fixes 1.7] у ResearchTreeSK.GUIClipUtility не найдены"
                    + " BeginScrollView/EndScrollView — прокрутка дерева оставлена как есть.");
                return;
            }

            Harmony harmony = new Harmony(HarmonyId);
            harmony.Patch(begin, new HarmonyMethod(typeof(ResearchTreeFix), nameof(BeginScrollView)));
            harmony.Patch(end, new HarmonyMethod(typeof(ResearchTreeFix), nameof(EndScrollView)));

            Log.Message("[HSK Fixes 1.7] прокрутка дерева исследований исправлена: мод сдвигает"
                + " содержимое через GUIClip.Push, а заглушка 1.7 смещение выбрасывает, и дерево"
                + " рисовалось всегда в позиции ноль. Переведено на GUI.BeginScrollView, который"
                + " пользуется PushScrollView движка. Масштабирование не трогаем.");
        }

        /// <summary>
        /// Имена параметров совпадают с оригиналом — Harmony связывает их по именам.
        /// </summary>
        public static bool BeginScrollView(Rect position, Vector2 scrollPosition, Rect viewRect)
        {
            GUI.BeginScrollView(position, scrollPosition, viewRect, GUIStyle.none, GUIStyle.none);
            return false;
        }

        public static bool EndScrollView()
        {
            GUI.EndScrollView(false);
            return false;
        }
    }
}
