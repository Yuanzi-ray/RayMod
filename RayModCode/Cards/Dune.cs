using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见攻击「沙丘」：<b>X 费</b>，层数大于 50 时才能打出；造成 X 点伤害 X 次。
/// 升级后每一项都变成 X+2。
///
/// <para>X 费要两件事：覆写 <see cref="HasEnergyCostX"/> 为 true，
/// 并且<b>构造器费用传 0</b>（那只是占位，实际消耗走 X 逻辑）。
/// 取值必须用 <c>ResolveEnergyXValue()</c>（它吃"改 X"的遗物），
/// <b>不要</b>直接读 <c>CapturedXValue</c>。它返回 <b>int</b>。</para>
///
/// <para>⚠️ X 同时进"伤害"和"段数"，所以不能像原版 <c>Whirlwind</c> 那样只传
/// <c>DamageVar</c>（那个是静态的、出牌时改不了）—— 只能自己算完再传给
/// <c>Attack(decimal)</c>。卡面上的"X"是<b>字面字符</b>（照抄原版
/// <c>WHIRLWIND.description</c>："对所有敌人造成{Damage:diff()}点伤害X次。"），
/// 升级的 +2 用 <c>{IfUpgraded:show:+2}</c> 表达。</para>
///
/// <para><b>2026-10-04 修正</b>：原先这里自己写了 <c>IsPlayable</c>，
/// 于是「探井人」的豁免对这张牌<b>静默失效</b>（不报错，只是不生效）—— 正是坑 30 描述的那个坑。
/// 现在按规矩继承 <see cref="DepthGatedCard"/>（门槛唯一通道），把效果搬进 <c>OnPlayGated</c>。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Dune : DepthGatedCard, IDepthCard
{
    /// <summary>X 费卡一律传 0（占位，真实费用由 X 逻辑决定）。</summary>
    private const int BaseEnergyCost = 0;

    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.AllEnemies;
    private const bool ShowInCardLibrary = true;


    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override bool HasEnergyCostX => true;

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DynamicVar("RequiredDepth", 50),
        // 升级时 +2，同时加到伤害和段数上。
        new DynamicVar("Bonus", 0m)
    ];

    public Dune() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    /// <summary>层数大于 50 才满足条件（探井人的豁免由基类统一处理）。</summary>
    protected override bool DepthConditionMet => DepthCmd.IsDepthGreaterThan(Owner, 50);

    protected override async Task OnPlayGated(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        int x = ResolveEnergyXValue();
        int bonus = DynamicVars["Bonus"].IntValue;
        int hits = x + bonus;
        decimal damage = x + bonus;

        // X = 0 时 WithHitCount(0) 是安全的（循环不执行），不用特判。
        await DamageCmd.Attack(damage)
            .WithHitCount(hits)
            .FromCard(this, cardPlay)
            .TargetingAllOpponents(CombatState)
            .WithHitFx("vfx/vfx_giant_horizontal_slash")
            .Execute(choiceContext);
    }

    protected override void OnUpgrade()
    {
        // 升级：X → X+2（伤害与段数同时 +2，设计稿）。
        DynamicVars["Bonus"].UpgradeValueBy(2m);
    }
}
