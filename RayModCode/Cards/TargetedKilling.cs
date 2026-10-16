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
/// 罕见攻击「定向猎杀」：<b>层数在 40~50 之间时才能打出</b>（用户拍板：闭区间），
/// 直接杀死<b>所有</b>生命值低于当前层数的敌人（用户拍板：全体，不是单体）。
/// 升级后区间变成 36~54。
///
/// <para>这是"处决"不是"伤害"：走 <c>CreatureCmd.Kill</c>，<b>不看血量差额、也不吃格挡</b>。
/// 用 <c>force: false</c>（原版默认）—— 让"防死"类效果依然能救人，这是更保守的选择。</para>
///
/// <para>门槛的两个端点做成 <c>DynamicVar</c>，好处是升级后<b>卡面数字会自动跟着变</b>
/// （36 / 54），不需要写 <c>{IfUpgraded}</c> 散文。</para>
///
/// <para>⚠️ <c>CreatureCmd.Kill</c> 在"非 live combat"时<b>静默返回、什么都不做</b>
/// （不抛异常）—— 出问题时不报错，排查时先确认这一点。</para>
///
/// <para><b>2026-10-04 修正</b>：原先这里自己写了 <c>IsPlayable</c>，
/// 于是「探井人」的豁免对这张牌<b>静默失效</b>（不报错，只是不生效）—— 正是坑 30 描述的那个坑。
/// 现在按规矩继承 <see cref="DepthGatedCard"/>（门槛唯一通道），把效果搬进 <c>OnPlayGated</c>。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class TargetedKilling : DepthGatedCard, IDepthCard
{
    private const int BaseEnergyCost = 2;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.AllEnemies;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DynamicVar("MinDepth", 40m),
        new DynamicVar("MaxDepth", 50m)
    ];

    public TargetedKilling() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    /// <summary>层数在 [MinDepth, MaxDepth] 闭区间内才可打出（探井人的豁免由基类统一处理）。</summary>
    protected override bool DepthConditionMet =>
        DepthCmd.DepthInRange(Owner, DynamicVars["MinDepth"].IntValue, DynamicVars["MaxDepth"].IntValue);

    protected override async Task OnPlayGated(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        var combatState = CombatState;

        if (combatState == null)
        {
            return;
        }

        int depth = DepthCmd.GetDepth(Owner);

        // 先 ToList() 快照：HittableEnemies 每次访问都新建列表，
        // 而且杀掉一只之后列表内容会变 —— 遍历中途变脸最容易漏杀。
        foreach (var enemy in combatState.HittableEnemies.ToList())
        {
            // 严格小于：血量和层数相等时杀不掉。
            if (enemy.CurrentHp < depth)
            {
                await CreatureCmd.Kill(enemy);
            }
        }
    }

    protected override void OnUpgrade()
    {
        // 升级：区间 40~50 → 36~54（设计稿）。两端一起放宽。
        DynamicVars["MinDepth"].UpgradeValueBy(-4m);
        DynamicVars["MaxDepth"].UpgradeValueBy(4m);
    }
}
