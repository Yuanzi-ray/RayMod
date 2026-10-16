using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 普通卡「禁止明火」：造成 16 点伤害；若当前层数大于 9，则失去 5 点生命。
/// 升级后 20 点伤害（自伤不变）。
///
/// <para>"失去生命"不是攻击伤害 —— 照抄原版 <c>Bloodletting</c> / <c>Hemokinesis</c>：
/// 用 <c>CreatureCmd.Damage</c> 打自己，props 传
/// <c>Unblockable | Unpowered | Move</c>（挡不住、不吃力量加成）。</para>
///
/// <para>设计上它和「广域警觉」是一对：都吃层数门槛，把层数当作"代价"而不是纯收益。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class NoFire : ModCardTemplate, IDepthCard
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
        new DynamicVar("SelfHarmDepth", 9),
        new DamageVar(16m, ValueProp.Move),
        new HpLossVar(5m)
    ];

    public NoFire() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        await DamageCmd.Attack(DynamicVars.Damage.BaseValue)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_attack_slash", null, "slash_attack.mp3")
            .Execute(choiceContext);

        // 层数 > 9 才自伤（阈值型，非"仅该档"）。
        if (DepthCmd.IsDepthGreaterThan(Owner, 9))
        {
            await CreatureCmd.Damage(
                choiceContext,
                Owner.Creature,
                DynamicVars.HpLoss.BaseValue,
                ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move,
                this,
                cardPlay);
        }
    }

    protected override void OnUpgrade()
    {
        // 升级：伤害 16 → 20（设计稿："造成20点伤害…失去5点生命"，自伤维持 5）。
        DynamicVars.Damage.UpgradeValueBy(4m);
    }
}
