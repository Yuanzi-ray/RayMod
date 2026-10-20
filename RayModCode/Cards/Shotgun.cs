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
/// 稀有攻击「霰弹枪」：对所有敌人造成 5 点伤害；<b>消耗所有弹药</b>，
/// 每消耗 1 层就再来一次。升级后每次伤害 +2（5 → 7）。
///
/// <para>段数 = 1 + 消耗掉的弹药数。用户拍板的口径：先消耗、再打，
/// 所以这一枪自己的弹药全部算进段数。</para>
///
/// <para><b>★ 2026-10-09：Uncommon → Rare（用户拍板）。</b>
/// 卡图同时按"稀有度分层"重画 —— 见 <c>HANDOFF-莱伊Mod-美术线.md</c> §7.7-J
/// 「Rare 必须比 Common 更满」（每张至少 3~4 个次要元素 + 场景）。
/// ⚠️ 稀有度只影响<b>卡池归属与卡框</b>，效果/费用一个字节都没动。</para>
///
/// <para><b>全体 + 多段能组合</b>（这条特意查证过）：<c>WithHitCount(n)</c> 配
/// <c>TargetingAllOpponents</c> 会让<b>每个敌人各挨 n 下</b>，原版铁证是
/// <c>DaggerSpray</c>（全体各 2 下）和 <c>Whirlwind</c>（X 段 × 全体）。
/// ⚠️ 三种 <c>Targeting*</c> 互斥，重复调用会抛异常；且
/// <c>TargetingAllOpponents</c> 必须在 <c>FromCard</c> <b>之后</b>。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Shotgun : ModCardTemplate, IAmmoCard
{
    private const int BaseEnergyCost = 2;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.AllEnemies;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DamageVar(5m, ValueProp.Move)
    ];

    public Shotgun() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // "每颗再来一次"：先消耗，返回值就是额外段数。
        int extraHits = AmmoCmd.ConsumeAllAmmo(Owner);

        await DamageCmd.Attack(DynamicVars.Damage.BaseValue)
            .WithHitCount(1 + extraHits)
            .FromCard(this, cardPlay)
            .TargetingAllOpponents(CombatState)
            // 2026-10-20（第二轮）：命中特效整段不设 —— 原版 vfx_starry_impact 的紫白爆闪
            // 会盖掉 Art\CardFieldVfx.Shotgun.cs 自己画的"从随机方向飞来的霰弹"。
            // WithHitFx 的三个参全可空，不调用 ⇒ HitVfx/HitSfx 为 null ⇒ 不再播任何原版命中特效。
            .Execute(choiceContext);
    }

    protected override void OnUpgrade()
    {
        // 升级：每次 7 → 8（设计稿）。
        DynamicVars.Damage.UpgradeValueBy(2m);
    }
}
