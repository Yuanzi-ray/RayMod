using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 普通卡「狙击镜」：造成 6 点伤害；你的下一张攻击牌可以<b>免费</b>打出。升级后 8 点伤害。
///
/// <para><b>不用自己写 Power</b>：原版就有 <see cref="FreeAttackPower"/>
///（"下一张攻击牌费用为 0"，见 <c>src\Core\Models\Powers\FreeAttackPower.cs</c>），
/// 原版卡 <c>Unrelenting</c> 就是这么用的（<c>Unrelenting.cs:31</c>）。
/// 它会自己盯着"下一张打出的攻击牌"，打完自动扣掉一层。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class SniperScope : ModCardTemplate
{
    private const int BaseEnergyCost = 2;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Common;
    private const TargetType CardTarget = TargetType.AnyEnemy;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DamageVar(6m, ValueProp.Move)
    ];

    public SniperScope() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        await DamageCmd.Attack(DynamicVars.Damage.BaseValue)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_starry_impact")   // 枪械卡：点状命中（原来是 blunt 闷响）
            .Execute(choiceContext);

        // Amount = 还能免几张（1 张）。
        await PowerCmd.Apply<FreeAttackPower>(
            choiceContext, Owner.Creature, 1m, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：伤害 6 → 8（免费次数维持 1 张）。
        DynamicVars.Damage.UpgradeValueBy(2m);
    }
}
