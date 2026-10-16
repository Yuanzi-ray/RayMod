using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Characters;
using RayMod.Powers;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 稀有能力「奇异宝藏」：<b>每当你打出一张费用为 0 的牌</b>，就
/// <b>随机把抽牌堆一张牌变成"打到之前 0 费"</b>，并<b>随机把一张手牌变成"打到之前费用翻倍"</b>。
/// 升级后费用 1 → 0。
///
/// <para>设计稿原文写的是"每当你打出 0 费用为零的牌时" —— 明显是笔误，按
/// "打出一张<b>费用为零</b>的牌"实现（和这张牌升级后自己变成 0 费正好自洽：
/// 升级后打出它自己<b>不</b>触发自己，因为触发读的是"打出的那张牌"的费用，
/// 而它自己结算时还在出牌堆上、费用是 0 —— ⚠️ 见 <see cref="StrangeTreasurePower"/> 里的说明）。</para>
///
/// <para>两面效果都是"<b>打到之前</b>"语义 → 用 <c>EnergyCost.SetUntilPlayed</c>
/// （坑 38：另外两个 <c>SetThisTurn</c> / <c>SetThisCombat</c> 的手感都不对）。</para>
///
/// <para>逻辑在 <see cref="StrangeTreasurePower"/>（挂着才生效）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class StrangeTreasure : ModCardTemplate
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Power;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    /// <summary>本卡没有数值占位符（两张被改费用的牌都是随机的）。</summary>
    protected override IEnumerable<DynamicVar> CanonicalVars => [];

    public StrangeTreasure() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<StrangeTreasurePower>(
            choiceContext, Owner.Creature, 1m, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：费用 1 → 0（设计稿："费用改为零"）。
        EnergyCost.UpgradeBy(-1);
    }
}
