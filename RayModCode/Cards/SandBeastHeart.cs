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
/// 罕见攻击「沙地兽之心」：把所有沙地兽印记<b>集中到一个敌人</b>身上，再对其造成 15 点伤害。
/// 升级后 20。
///
/// <para><b>两件事的顺序有意义</b>：先集中印记、再打伤害。印记的效果是
/// "该敌人受到的攻击牌伤害 +10%/层"，集中之后这一击就能吃到<b>全部</b>层数的加成 ——
/// 这正是这张牌的设计意图（把散在多个敌人身上的增伤收拢成一记爆发）。
/// 反过来写就只剩"搬家"，没有爆发。</para>
///
/// <para>层数搬家 + 记账搬家都在 <see cref="BeastCmd.TransferAllMarks"/> 里，
/// 卡牌这边只负责"选谁"和"打多少"。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class SandBeastHeart : ModCardTemplate, ISandBeastCard
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
        new DamageVar(15m, ValueProp.Move)
    ];

    public SandBeastHeart() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        // 1) 所有印记集中到目标身上（层数 + "将来由谁负责摘掉"一起搬）。
        await BeastCmd.TransferAllMarks(choiceContext, Owner, cardPlay.Target, this);

        // 2) 再打伤害：此刻目标身上已经是全部层数，这一击吃满加成。
        await DamageCmd.Attack(DynamicVars.Damage.BaseValue)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
            .Execute(choiceContext);
    }

    protected override void OnUpgrade()
    {
        // 升级：15 → 20（设计稿）。
        DynamicVars.Damage.UpgradeValueBy(5m);
    }
}
