using Verse;

// Два типа, которые пак называет в своих дефах, но которых нет нигде: ни в Combat Extended любой
// версии, ни в ванилле.
//
//     Defs/ThingDefs/VDummy.xml            <li Class="CombatExtended.CompProperties_Smoke"/>
//     Defs/BodyParts/BodyParts_Cybernetic.xml  <li Class="CombatExtended.SmokeInhalationImmuneExtension" />
//
// Проверено по трём сборкам CE — апстрим 1.6.7.0.0, апстрим 1.7 и собственная сборка пака — и по
// ванильной Assembly-CSharp: этих имён нет ни в одной. Видимо остатки от версии CE, где они были.
// С установленной CE это стоит дефа целиком:
//
//     Exception loading def from file BodyParts_Cybernetic.xml: Could not find type named
//     CombatExtended.SmokeInhalationImmuneExtension from node <li Class="...
//
// Поэтому они здесь, а не в заглушке CE. Это поломка самого пака: она не зависит от того,
// установлена CE или нет, и лечить её надо в обоих случаях. Раньше оба типа лежали в NoCE и потому
// помогали только при отсутствующей CE.
//
// Папка CE/ рядом — только раскладка исходников. Сборка одна и грузится всегда; условной её делать
// нельзя именно по причине выше.
//
// Пространство имён чужое, CombatExtended, и это безопасно: CE этих имён не объявляет, так что
// конфликта типов не возникает ни с ней, ни без неё.
namespace CombatExtended
{
    /// <summary>Инертный компонент: деф должен грузиться, механики за ним никакой нет.</summary>
    public class CompProperties_Smoke : CompProperties
    {
        public CompProperties_Smoke() { compClass = typeof(HSKFixes17.CompInertHSK); }
    }

    /// <summary>Расширение дефа — только носитель данных, читать его всё равно некому.</summary>
    public class SmokeInhalationImmuneExtension : DefModExtension { }
}

namespace HSKFixes17
{
    /// <summary>
    /// ThingComp абстрактный, поэтому compClass на него поставить нельзя — нужен свой пустой.
    /// В заглушке CE такой есть (CompInert), но она при установленной CE не загружается.
    /// </summary>
    public class CompInertHSK : ThingComp
    {
    }
}
