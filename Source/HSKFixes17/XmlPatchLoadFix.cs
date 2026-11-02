using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Xml;
using HarmonyLib;
using Verse;

namespace HSKFixes17
{
    internal static class XmlPatchLoadFix
    {
        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            // RWLayout сверяет весь IL LoadPatches с эталоном 1.6. В 1.7 другой
            // цикл обхода файлов, но защита нужна та же: повреждённый XML не должен
            // обрывать загрузку всех модов. Заменяем только чтение документа.
            var field = AccessTools.Field(typeof(LoadableXmlAsset), "xmlDoc");
            var read = AccessTools.Method(typeof(XmlPatchLoadFix), nameof(ReadDocument));
            var code = instructions.ToList();
            int fixes = 0;
            foreach (CodeInstruction instruction in code)
            {
                if (instruction.opcode != OpCodes.Ldfld || !Equals(instruction.operand, field))
                    continue;
                instruction.opcode = OpCodes.Call;
                instruction.operand = read;
                fixes++;
            }
            if (fixes != 1)
                throw new InvalidOperationException("RWLayout LoadPatches: expected one xmlDoc read, found " + fixes);
            return code;
        }

        private static XmlDocument ReadDocument(LoadableXmlAsset asset)
        {
            if (asset.xmlDoc?.DocumentElement != null)
                return asset.xmlDoc;

            Log.Error("[HSK Fixes 1.7] повреждённый XML-патч пропущен: " + asset.FullFilePath);
            var empty = new XmlDocument();
            empty.AppendChild(empty.CreateElement("Patch"));
            return empty;
        }
    }
}
