using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Beasts;
using RayMod.Characters;
using RayMod.Powers;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见能力「沙地兽与她」：<b>沙地兽印记效果 +20%</b>（每层 10% → 12%），<b>沙地兽上限 +1</b>。
/// 升级后费用 2 → 1。
///
/// <para>两件事分两处做：</para>
/// <list type="bullet">
/// <item><b>印记 +20%</b>：挂 <see cref="SandBeastHerPower"/>，它在被施加时把
/// <c>BeastState.MarkBonusAbility</c> 打开 —— 那个字段当初就是为这张牌预留的
/// （注释在 <c>BeastState.cs:49</c>），印记增伤那边直接读它。</item>
/// <item><b>上限 +1</b>：是打出时的一次性动作，走 <c>BeastCmd.ModifyCap</c>。
/// ⚠️ 上限被夹在 [0, <c>MinionCapMax</c>=6]（用户 2026-10-03 决定保持 6），
/// 所以**已经顶到 6 时这张牌的上限部分不生效**（印记 +20% 照常生效）。</item>
/// </list>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class SandBeastHer : ModCardTemplate, ISandBeastCard
{
    private const int BaseEnergyCost = 2;
    private const CardType CardKind = CardType.Power;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    /// <summary>
    /// 沙地兽上限增量（卡面「上限+{CapGain:diff()}」显示的就是这个 1）。
    ///
    /// <para>⚠️ <b>印记 +20% 那个数还写死在共享文件里</b>，本卡这里没法传值：
    /// 印记倍率在 <c>Beasts\BeastConstants.cs</c> 的 <c>MarkBonusAbilityMultiplier = 1.2m</c>，
    /// 由 <c>Beasts\SandBeastMarkPower.cs</c> 读取；本卡只能通过
    /// <see cref="SandBeastHerPower"/> 打开 <c>BeastState.MarkBonusAbility</c> 这个开关。
    /// 所以那 20% 暂时仍是卡面字面数字（改它要动共享文件：Power 从 cardSource 读本卡的
    /// DynamicVar，再经 BeastState 传给印记 Power）。</para>
    /// </summary>
    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DynamicVar("CapGain", 1)
    ];

    public SandBeastHer() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // 1) 印记 +20%：Power 的 BeforeApplied 会打开 BeastState.MarkBonusAbility。
        //    ⚠️ 这个 20% 在共享文件里（BeastConstants.MarkBonusAbilityMultiplier），
        //    卡面暂时还写死"20%"—— 原因与改法见 CanonicalVars 的注释。
        await PowerCmd.Apply<SandBeastHerPower>(
            choiceContext, Owner.Creature, 1m, Owner.Creature, this);

        // 2) 上限 +1（会被夹到 6）：增量走变量，卡面显示的就是它。
        await BeastCmd.ModifyCap(choiceContext, Owner, DynamicVars["CapGain"].IntValue);
    }

    protected override void OnUpgrade()
    {
        // 升级：费用 2 → 1（设计稿："升级后费用变为1"）。
        EnergyCost.UpgradeBy(-1);
    }
}
