using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Beasts;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 普通卡「沙地兽突袭」：造成 6 点伤害；场上有沙地兽时，额外造成 6 点伤害。
/// 升级后额外伤害提升至 7。
///
/// <para>判据是"场上是否还有沙地兽"（<see cref="BeastCmd.GetCount"/> &gt; 0），
/// 不是"印记在不在" —— 印记可能因为敌人死亡而消失，但兽还在场（用户 2026-10-03 拍板的规则 6）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class SandBeastRaid : ModCardTemplate, ISandBeastCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Common;
    private const TargetType CardTarget = TargetType.AnyEnemy;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CalculationBaseVar(6m),
        new ExtraDamageVar(6m),
        // 乘数：场上有沙地兽就是 1（⇒ 6 + 6 = 12），没有就是 0（⇒ 6）。
        // ⚠️ 用 card.Owner，不能捕获 this（WithMultiplier 要求 lambda 是 static 的）。
        new CalculatedDamageVar(ValueProp.Move)
            .WithMultiplier((card, _) => BeastCmd.GetCount(card.Owner) > 0 ? 1m : 0m)
    ];

    public SandBeastRaid() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        // 伤害 = 基准 6 + 额外 6 × (场上有沙地兽 ? 1 : 0)，卡面显示的就是这个实算值。
        await DamageCmd.Attack(DynamicVars.CalculatedDamage)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_attack_slash", null, "slash_attack.mp3")
            .Execute(choiceContext);
    }

    protected override void OnUpgrade()
    {
        // 升级：额外伤害 6 → 7（设计稿："额外造成伤害提升至7点"）。
        DynamicVars.ExtraDamage.UpgradeValueBy(1m);
    }
}
