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
/// 罕见攻击「无尽黑暗」：<b>0 费</b>，层数大于 60 时才能打出，对所有敌人造成 70 点伤害。
/// 升级后 80。
///
/// <para>层数门槛的写法照抄原版「谋生之道」那类牌：覆写 <c>IsPlayable</c>
/// （<c>CardModel.cs:823</c>，全树唯一读取点是 <c>CanPlay</c> 的 <c>:1759</c>）。</para>
///
/// <para>群体攻击照抄原版 <c>Sow.cs:24</c>：目标类型 <c>AllEnemies</c> +
/// <c>.TargetingAllOpponents(CombatState)</c>（注意是"全体"而不是逐个换目标）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class EndlessDarkness : DepthGatedCard, IDepthCard
{
    private const int BaseEnergyCost = 0;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.AllEnemies;
    private const bool ShowInCardLibrary = true;


    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DynamicVar("DepthRequirement", 50),
        new DamageVar(70m, ValueProp.Move)
    ];

    public EndlessDarkness() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    /// <summary>层数大于 60 才满足条件（「探井人」的豁免由基类统一处理）。</summary>
    protected override bool DepthConditionMet => DepthCmd.IsDepthGreaterThan(Owner, 50);

    protected override async Task OnPlayGated(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await DamageCmd.Attack(DynamicVars.Damage.BaseValue)
            .FromCard(this, cardPlay)
            .TargetingAllOpponents(CombatState)
            .WithHitFx("vfx/vfx_attack_slash")
            .Execute(choiceContext);
    }

    protected override void OnUpgrade()
    {
        // 升级：70 → 80（设计稿）。
        DynamicVars.Damage.UpgradeValueBy(10m);
    }
}
