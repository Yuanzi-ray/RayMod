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
/// 罕见能力「前行」：每回合开始时层数 +5。升级后 +6。
///
/// <para>能力牌的写法照抄原版 <c>Unmovable.cs</c>：<c>CardType.Power</c> +
/// <c>TargetType.Self</c>，<c>OnPlay</c> 只做一件事 —— 把载体 Power 挂到玩家身上，
/// 数值（这里 = 每回合加多少层）作为 Amount 传进去，逻辑全在 Power 的回合钩子里。</para>
///
/// <para>再打一张会叠加（Amount 相加），也就是每回合 +10。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Forward : ModCardTemplate, IDepthCard
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
        new CardsVar("Gain", 5)
    ];

    public Forward() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<ForwardPower>(
            choiceContext, Owner.Creature, DynamicVars["Gain"].BaseValue, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：每回合 +5 → +6。
        DynamicVars["Gain"].UpgradeValueBy(1m);
    }
}
