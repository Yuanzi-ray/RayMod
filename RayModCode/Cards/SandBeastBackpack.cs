using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Ammo;
using RayMod.Beasts;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见技能「沙地兽背包」：<b>获得相当于本场战斗召唤过沙地兽数量的弹药</b>。升级后费用 2 → 1。
///
/// <para>读 <c>BeastState.SummonedThisCombat</c>（本场<b>累计</b>召唤数，含已被替换/退场的），
/// 再走 <c>AmmoCmd.AddAmmo</c> —— 它会硬夹在上限内，所以溢出不会浪费在别处，就是单纯被上限截断。</para>
///
/// <para>没有动态变量：数量是"本场累计"，卡面无法用一个固定数字表述
/// （原版的 <c>Calculated*Var</c> 只覆盖伤害/格挡两种，弹药没有对应的动态变量）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class SandBeastBackpack : ModCardTemplate, ISandBeastCard, IAmmoCard
{
    private const int BaseEnergyCost = 2;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    public SandBeastBackpack() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    /// <summary>
    /// ★ 2026-10-07（用户要求）：卡面显示**实得的弹药数**。
    ///
    /// <para>乘数 lambda 在出牌/显示时求值，只读 <see cref="BeastState.SummonedThisCombat"/>，
    /// <b>不碰 <c>DynamicVars</c></b> —— 避免重蹈"初始化期读 DynamicVars 导致卡牌全不显示"的坑。
    /// 取值公式与 <see cref="OnPlay"/> 完全一致（基准 0 + 额外 1 × 本场召唤数）。</para>
    /// </summary>
    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CalculationBaseVar(0m),
        new CalculationExtraVar(1m),
        new CalculatedBlockVar(ValueProp.Move)
            .WithMultiplier((card, _) => BeastCmd.TryGetState(card.Owner)?.SummonedThisCombat ?? 0)
    ];

    protected override Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        int summoned = BeastCmd.TryGetState(Owner)?.SummonedThisCombat ?? 0;

        if (summoned > 0)
        {
            AmmoCmd.AddAmmo(Owner, summoned);
        }

        return Task.CompletedTask;
    }

    protected override void OnUpgrade()
    {
        // 升级：费用 2 → 1（设计稿："升级后费用-1"）。
        EnergyCost.UpgradeBy(-1);
    }
}
