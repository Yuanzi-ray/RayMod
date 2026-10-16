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
/// 罕见攻击「伏击」：造成 2 点伤害 3 次；<b>你每有 1 层弹药，每段伤害 +1</b>。升级后段数 4。
///
/// <para>用户 2026-10-03 拍板：加的是<b>每段伤害</b>（不是加段数）。
/// 所以 7 层弹药时是 3 段 × 9 点 = 27，而不是 10 段 × 2 点。</para>
///
/// <para>多段用 <c>WithHitCount</c>（原版 <c>TwinStrike</c> 的写法）。
/// ⚠️ <c>WithHitCount</c> 吃的是 <b>int</b>，而伤害是 <b>decimal</b> —— 两者别混。</para>
///
/// <para>每段伤害用三元组显示实算值（<c>基准 2 + 1 × 弹药数</c>），
/// 段数走一个普通 <c>DynamicVar("Hits")</c>（升级 +1）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Ambush : ModCardTemplate, IAmmoCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.AnyEnemy;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CalculationBaseVar(2m),
        new ExtraDamageVar(1m),
        new CalculatedDamageVar(ValueProp.Move)
            .WithMultiplier((card, _) => AmmoCmd.GetAmmo(card.Owner)),
        new DynamicVar("Hits", 3m)
    ];

    public Ambush() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        await DamageCmd.Attack(DynamicVars.CalculatedDamage)
            .WithHitCount(DynamicVars["Hits"].IntValue)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_attack_slash")
            .Execute(choiceContext);
    }

    protected override void OnUpgrade()
    {
        // 升级：段数 3 → 4（设计稿）。"每层弹药 +1 伤害"不变。
        DynamicVars["Hits"].UpgradeValueBy(1m);
    }
}
