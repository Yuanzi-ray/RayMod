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
/// 罕见攻击「沙地兽狂潮」：造成 4 点伤害，<b>本场战斗每召唤过一只沙地兽，伤害 +5</b>。
/// 升级后基础伤害 4 → 6。
///
/// <para>用三元组把"实算伤害"显示在卡面上：<c>基准 4 + 5 × 本场召唤数</c>。
/// 计数读 <c>BeastState.SummonedThisCombat</c> —— <b>含已被替换/退场的</b>，
/// 和「沙地兽之盾」同一个来源。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class SandBeastCraze : ModCardTemplate, ISandBeastCard
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
        new CalculationBaseVar(4m),
        new ExtraDamageVar(5m),
        new CalculatedDamageVar(ValueProp.Move)
            .WithMultiplier((card, _) => BeastCmd.TryGetState(card.Owner)?.SummonedThisCombat ?? 0)
    ];

    public SandBeastCraze() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        await DamageCmd.Attack(DynamicVars.CalculatedDamage)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
            .Execute(choiceContext);
    }

    protected override void OnUpgrade()
    {
        // 升级：基础伤害 4 → 6（设计稿："基础伤害提升至6"）。每只的 +5 不变。
        DynamicVars.CalculationBase.UpgradeValueBy(2m);
    }
}
