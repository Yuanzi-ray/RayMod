using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见攻击「金钱就是力量」：造成相当于<b>当前金币</b>的伤害，然后失去 100 金币。
/// <b>消耗</b>。升级后失去的金币 100 → 80。
///
/// <para><b>顺序不能反</b>：先用"打折前的金币"打出伤害，再扣钱。反过来写伤害会少 100
/// （扣完钱再算，卡面数字也会跟着掉）。</para>
///
/// <para>伤害用三元组显示实算值（<c>基准 0 + 1 × 当前金币</c>），
/// 所以卡面数字会随金币实时变化。</para>
///
/// <para>⚠️ 扣钱必须走 <c>PlayerCmd.LoseGold</c>：直接写 <c>Owner.Gold -= x</c> 会绕过
/// 金币变化事件、统计和音效。而且要自己夹住（<c>LoseGold</c> 不检查够不够，
/// 负数还会反向加钱）—— 这里照抄原版 <c>Debt</c> 的 <c>Min(请求量, 当前金币)</c>。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class MoneyIsPower : ModCardTemplate
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
        new CalculationBaseVar(0m),
        new ExtraDamageVar(1m),
        new CalculatedDamageVar(ValueProp.Move)
            .WithMultiplier((card, _) => (decimal)(card.Owner?.Gold ?? 0)),
        new DynamicVar("GoldLoss", 100m)
    ];

    /// <summary>消耗词条（设计稿明确写了）。</summary>
    public override IEnumerable<CardKeyword> CanonicalKeywords =>
    [
        CardKeyword.Exhaust
    ];

    public MoneyIsPower() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        // 1) 先打伤害 —— 此刻金币还是满的。
        await DamageCmd.Attack(DynamicVars.CalculatedDamage)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
            .Execute(choiceContext);

        // 2) 再扣钱。夹住上限，并且金币为 0 时干脆不调用（LoseGold 会无条件播音效）。
        int loss = Math.Min(DynamicVars["GoldLoss"].IntValue, Owner.Gold);

        if (loss > 0)
        {
            await PlayerCmd.LoseGold(loss, Owner);
        }
    }

    protected override void OnUpgrade()
    {
        // 升级：失去 100 → 80（设计稿）。
        DynamicVars["GoldLoss"].UpgradeValueBy(-20m);
    }
}
