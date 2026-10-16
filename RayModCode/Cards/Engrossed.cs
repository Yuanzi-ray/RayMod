using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Ammo;
using RayMod.Characters;
using RayMod.Powers;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 稀有能力「入神」：<b>需要消耗弹药的攻击伤害变为 1.5 倍</b>；
/// <b>对同一个敌人连续攻击时，每次攻击使下一次对该敌人的伤害 +10%</b>（无上限，换目标清零）。
/// 升级后添加<b>固有</b>。
///
/// <para><b>用户 2026-10-04 拍板</b>：⑫"需要消耗子弹的攻击"= <b>只有本次真正扣了弹药的攻击</b>
/// （不是所有实现了 <c>IAmmoCard</c> 的牌）；⑧ 叠加部分是"每张攻击牌一层 +10%、无上限、换目标清零"。</para>
///
/// <para>两条效果都在 <see cref="EngrossedPower"/> 里（那里的两条钩子顺序有讲究，别乱改）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Engrossed : ModCardTemplate, IAmmoCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Power;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    /// <summary>
    /// ⚠️ 卡面**渲染不了小数**（<c>DynamicVar.ToHighlightedString</c> 内部是
    /// <c>int value = (int)PreviewValue;</c>，1.5 会显示成 "1"）。
    /// 所以"×1.5"按**整数百分比**存：变量 50 → 代码 <c>1 + 50/100m</c>，语义完全一致。
    /// </summary>
    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DynamicVar("AmmoBonusPercent", 50),
        new DynamicVar("PerStackBonusPercent", 10)
    ];

    public Engrossed() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<EngrossedPower>(
            choiceContext, Owner.Creature, 1m, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：添加"固有"（设计稿）。
        AddKeyword(CardKeyword.Innate);
    }
}
