using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Ammo;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见技能「以弹为盾」：获得相当于<b>弹药上限</b>的格挡。升级后额外 +5。
///
/// <para>用 <c>CalculatedBlockVar</c> 让卡面显示实算值：<c>基准 0 + 1 × 弹药上限</c>
/// ⇒ 显示的就是上限（升级后是上限 +5）。所以上限被「重大升级」「无限子弹」抬高时，
/// 卡面数字会跟着涨 —— 这正是想要的"上限即格挡"。</para>
///
/// <para>注意是<b>上限</b>不是当前弹药：弹药被打空了这张牌依然给满格挡。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class AttackIsDefense : ModCardTemplate, IAmmoCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    public override bool GainsBlock => true;

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        // 基准 0：升级时 +5 就变成"上限 +5"。
        new CalculationBaseVar(0m),
        new CalculationExtraVar(1m),
        new CalculatedBlockVar(ValueProp.Move)
            .WithMultiplier((card, _) => AmmoCmd.GetCap(card.Owner))
    ];

    public AttackIsDefense() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await CreatureCmd.GainBlock(
            Owner.Creature,
            DynamicVars.CalculatedBlock.Calculate(cardPlay.Target),
            DynamicVars.CalculatedBlock.Props,
            cardPlay);
    }

    protected override void OnUpgrade()
    {
        // 升级：获得"上限 +5"（设计稿："获得相当于弹药上限+5的格挡"）。
        DynamicVars.CalculationBase.UpgradeValueBy(5m);
    }
}
