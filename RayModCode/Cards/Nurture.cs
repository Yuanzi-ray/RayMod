using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Beasts;
using RayMod.Characters;
using RayMod.Powers;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 先古能力牌「哺育」：<b>2 费</b>，每当你召唤沙地兽时，把<b>等量</b>的「沙地兽」卡加入手牌；
/// <b>升级后费用变 1</b>。
///
/// <para><b>怎么拿到它</b>（用户 2026-10-04 拍板，"走 DustyTome 这条"）：
/// <c>[RegisterDustyTomeCard]</c> 把本卡注册成<b>原版先古遗物「尘封之书」</b>在莱伊身上的候选，
/// 而尘封之书由<b>原版共享先古事件 Darv</b> 发放（任何一幕都可能遇到）。
/// ⚠️ 尘封之书是<b>无条件升级</b>后加入牌组的（<c>DustyTome.AfterObtained</c> 里 <c>CardCmd.Upgrade</c>），
/// 所以玩家拿到的永远是 1 费版本 —— 用户 2026-10-04 已确认接受。
/// 另外注册是"盖过原版"的语义：只要莱伊注册了候选，尘封之书就<b>只</b>从注册列表里挑，
/// 原版先古牌不会再出现（目前注册了「哺育」与「全息建模」两张，各 50%）。</para>
///
/// <para>口径与实现细节（"不包括沙地兽这张牌"指什么、数量按本次算）见
/// <see cref="NurturePower"/> 的类注释。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
[RegisterDustyTomeCard(typeof(RayModCharacter))]
public sealed class Nurture : ModCardTemplate, ISandBeastCard
{
    private const int BaseEnergyCost = 2;

    private const CardType CardKind = CardType.Power;

    /// <summary><b>Ancient</b>：先古牌，不进任何奖励池，只能由先古遗物发放。</summary>
    private const CardRarity CardRarityValue = CardRarity.Ancient;

    private const TargetType CardTarget = TargetType.Self;

    private const bool ShowInCardLibrary = true;

    /// <summary>占位卡图（等美术时换成 Nurture.png）。</summary>
    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    /// <summary>本卡没有数值占位符（给几张 = 本次召唤了几只，卡面写不出来）。</summary>
    protected override IEnumerable<DynamicVar> CanonicalVars => [];

    public Nurture() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<NurturePower>(
            choiceContext, Owner.Creature, 1m, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：2 费 → 1 费（设计稿）。
        EnergyCost.UpgradeBy(-1);
    }
}
