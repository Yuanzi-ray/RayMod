using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Characters;
using RayMod.Depth;
using RayMod.Powers;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见能力「资金」：<b>层数每累计变动 12，抽 1 张牌</b>。升级后添加<b>固有</b>。
///
/// <para>从<b>打出这张牌那一刻</b>开始计数（用户 2026-10-03 拍板 Q10）。
/// 计的是层数变动的<b>数量</b>（绝对值之和），不是变动的次数 —— 细节见
/// <see cref="FundsPower"/> 的注释。</para>
///
/// <para>它是"层数推得越高越顺"的引擎：前行（每回合 +5）、塌方（一次 +12）、
/// 深入这类牌都会稳定喂它。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Funds : ModCardTemplate, IDepthCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Power;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CardsVar("Draw", 1),
        // ★ 真值由 Powers/FundsPower 在能力被施加时从这张卡读走。
        new DynamicVar("DepthPerDraw", 12)
    ];

    public Funds() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<FundsPower>(
            choiceContext, Owner.Creature, DynamicVars["Draw"].BaseValue, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：添加"固有"（设计稿："加固有"）。
        AddKeyword(CardKeyword.Innate);
    }
}
