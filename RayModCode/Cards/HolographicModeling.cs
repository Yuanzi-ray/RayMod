using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Characters;
using RayMod.Powers;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 先古能力牌「全息建模」：<b>2 费</b>，战斗结束时选择一个已有的遗物<b>复制</b>，
/// 该复制品在<b>下一场</b>战斗结束时消失；<b>升级后改为下两场</b>。
///
/// <para><b>怎么拿到它</b>（用户 2026-10-04 拍板，"也挂 DustyTome"）：
/// <c>[RegisterDustyTomeCard]</c> 让<b>原版先古遗物「尘封之书」</b>（Darv 事件发放）把本卡当作莱伊的候选。
/// ⚠️ 同批注册的还有「哺育」，所以一次尘封之书是 <b>两张里随机给一张</b>；
/// 而尘封之书对莱伊<b>只</b>从注册列表里挑（原版先古牌不再出现）。
/// 另外尘封之书无条件发<b>升级版</b>（用户已确认接受）。</para>
///
/// <para><b>口径（用户 2026-10-04 拍板）</b>：复制品与原件<b>完全叠加生效</b>；
/// <b>不禁任何遗物</b> —— 包括初始遗物「下矿」（复制它 ≈ 每回合层数 +6 / 弹药上限 +6）。
/// 同一时间只维持一份复制品（再打一次会先把旧的摘掉）。
/// 完整生命周期见 <c>RayMod.Holo.HoloCmd</c> 的类注释。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
[RegisterDustyTomeCard(typeof(RayModCharacter))]
public sealed class HolographicModeling : ModCardTemplate
{
    private const int BaseEnergyCost = 2;

    private const CardType CardKind = CardType.Power;

    /// <summary><b>Ancient</b>：先古牌，不进任何奖励池，只能由先古遗物发放。</summary>
    private const CardRarity CardRarityValue = CardRarity.Ancient;

    private const TargetType CardTarget = TargetType.Self;

    private const bool ShowInCardLibrary = true;

    /// <summary>未升级：复制品活 1 场战斗（"下一场战斗结束时消失"）。</summary>
    private const int CombatsUnupgraded = 1;

    /// <summary>升级后：活 2 场战斗。</summary>
    private const int CombatsUpgraded = 2;

    /// <summary>占位卡图（等美术时换成 HolographicModeling.png）。</summary>
    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    /// <summary>本卡没有数值占位符（"几场"由升级与否决定，卡面用 <c>{IfUpgraded}</c> 写死文字）。</summary>
    protected override IEnumerable<DynamicVar> CanonicalVars => [];

    public HolographicModeling() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // Amount = 复制品能活几场（Power 的 AfterCombatEnd 会把它记进跑团存档）。
        await PowerCmd.Apply<HolographicModelingPower>(
            choiceContext, Owner.Creature,
            IsUpgraded ? CombatsUpgraded : CombatsUnupgraded,
            Owner.Creature, this);
    }

    // ⚠️ 刻意<b>不覆写 OnUpgrade</b>：升级既不涨费用也不涨别的数值，
    //    只把"活几场"从 1 变成 2，而那个分支在 OnPlay 里读 IsUpgraded 就够了。
}
