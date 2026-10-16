using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Ammo;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见攻击「爆破」：造成 30 点伤害。升级后 35。
/// <b>若你的弹药大于 7，这张牌不消耗能量，改为消耗 5 点弹药</b>。
///
/// <para><b>替代支付怎么实现的</b>：原版<b>没有</b>替代支付 API
/// （<c>CardModel.SpendResources</c> 不是 virtual，"用弹药代替能量"这种钩子全树为空），
/// 但 <c>AbstractModel.TryModifyEnergyCostInCombat</c> 可以在条件成立时把费用改成 0，
/// 然后由 <c>OnPlay</c> 自己扣弹药。卡牌自身就活在这条钩子链上
/// （<c>CardModel.ShouldReceiveCombatHooks</c> = 在手牌等战斗牌堆里时成立），所以不需要额外 Power。</para>
///
/// <para>⚠️ <b>费用钩子里的条件和 OnPlay 里的条件必须一模一样</b>。
/// 两处写得不一样就会出现"没免费却照样扣弹药"或者"免费了却没付代价"这种静默 bug。
/// 所以两处都只调用同一个私有方法 <see cref="IsPaidWithAmmo"/>。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Cannon : ModCardTemplate, IAmmoCard
{
    private const int BaseEnergyCost = 3;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.AnyEnemy;
    private const bool ShowInCardLibrary = true;



    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DynamicVar("AmmoPaymentCost", 5),
        new DynamicVar("AmmoPaymentThreshold", 7),
        new DamageVar(30m, ValueProp.Move)
    ];

    public Cannon() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    /// <summary>是否该走"弹药代替能量"这条路（费用钩子与 OnPlay 共用这一个判据）。</summary>
    private bool IsPaidWithAmmo => AmmoCmd.GetAmmo(Owner) > 7;

    public override bool TryModifyEnergyCostInCombat(
        CardModel card, decimal originalCost, out decimal modifiedCost)
    {
        modifiedCost = originalCost;

        // 这条钩子会为手牌里的每一张牌调用，所以先认出自己。
        if (card != this || originalCost <= 0m)
        {
            return false;
        }

        if (!IsPaidWithAmmo)
        {
            return false;
        }

        modifiedCost = 0m;
        return true;
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        // 付出代价：判据与费用钩子完全一致（见类注释的警告）。
        if (IsPaidWithAmmo)
        {
            AmmoCmd.ConsumeAmmo(Owner, 5);
        }

        await DamageCmd.Attack(DynamicVars.Damage.BaseValue)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
            .Execute(choiceContext);
    }

    protected override void OnUpgrade()
    {
        // 升级：30 → 35（设计稿）。
        DynamicVars.Damage.UpgradeValueBy(5m);
    }
}
