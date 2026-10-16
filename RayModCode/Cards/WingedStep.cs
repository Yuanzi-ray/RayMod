using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 稀有技能「Winged Step」：获得 13 点格挡；<b>手牌中所有费用大于 2 的牌费用变为 0</b>。升级后格挡 16。
///
/// <para><b>费用门槛 = 2</b>（用户 2026-10-07 拍板）。此前本注释写的是 4，与代码
/// （<c>CanonicalVars</c> 里的 <c>CostThreshold = 2</c>、<c>OnPlay</c> 里的 <c>&gt; 2</c>）
/// 以及卡面文案（<c>费用大于{CostThreshold:diff()}</c>）都不一致 —— <b>以 2 为准，注释已订正</b>。</para>
///
/// <para><b>卡名保持英文是故意的</b>：<c>Winged Step</c> 是 EP 名称，用户明确要求<b>不译成中文</b>，
/// 所以 <c>zhs/cards.json</c> 里这一条就是英文。它也是全池唯一"中文文案里出现英文"的地方 ——
/// 不会被 <c>check_loc.ps1</c> 第⑤项（中文零空格）拦下，因为那一项只检查<b>含中文的值</b>。</para>
///
/// <para><b>费用读写用原版现成 API，不需要新工具</b>（R3 准备阶段的结论）：
/// 读当前费用 = <c>EnergyCost.GetResolved()</c>（含全部修正；X 费返回 <c>CapturedXValue</c>），
/// 改成"打到为止 0 费" = <c>EnergyCost.SetUntilPlayed(0)</c>
/// —— 原版范例就是 <c>Eidolon</c>："弃牌堆所有牌 0 费直到打出"。</para>
///
/// <para><b>用户拍板⑩：费用判定用「当前费用」</b>，所以这里用 <c>GetResolved()</c> 而不是基础费用 ——
/// 一张被「发家致富」翻倍过的 3 费牌，此刻费用是 6 → <b>会</b>被这张牌抹成 0。</para>
///
/// <para>⚠️ 只数<b>手牌</b>（设计稿："手牌中所有费用大于2的牌"）—— 抽牌堆/弃牌堆里的不动。
/// 同时注意这张牌自己在 <c>OnPlay</c> 时已经躺在<b>出牌堆</b>里了，所以不会把自己算进去。</para>
///
/// <para>只改"在当前费用 &gt;2"的牌：已经是 0 费的牌不会再叠一层修正（保持卡面干净）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class WingedStep : ModCardTemplate
{
    private const int BaseEnergyCost = 2;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;


    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    /// <summary>加格挡的牌必须写（否则格挡修正类效果会静默失效）。</summary>
    public override bool GainsBlock => true;

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DynamicVar("CostThreshold", 2),
        new BlockVar(13m, ValueProp.Move)
    ];

    public WingedStep() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await CreatureCmd.GainBlock(Owner.Creature, DynamicVars.Block, cardPlay);

        var hand = PileType.Hand.GetPile(Owner).Cards;

        // 先快照再改：GetResolved() 会随我们刚加的修正变化（同步遍历 + 修改最容易写出前后不一致）。
        var targets = hand
            .Where(card => card.EnergyCost.GetResolved() > 2)
            .ToList();

        foreach (var card in targets)
        {
            card.EnergyCost.SetUntilPlayed(0);
        }
    }

    protected override void OnUpgrade()
    {
        // 升级：格挡 13 → 16（设计稿）。
        DynamicVars.Block.UpgradeValueBy(3m);
    }
}
