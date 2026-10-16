using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Ammo;
using RayMod.Characters;
using RayMod.Powers;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 稀有能力「子弹模式」：<b>你打出的牌改为消耗等量的弹药，不能用能量支付</b>；
/// 弹药不足的牌打不出来。升级后添加<b>固有</b>。
///
/// <para><b>⚠️ 用户 2026-10-04 明确：设计稿写错了</b> —— 原文"有弹药时，所有牌将优先消耗等量的弹药，
/// 而不是能量"应读作<b>"不可用能量支付"</b>。（这也和 <see cref="AmmoState"/> 类注释里
/// 早就写着的"「子弹模式」是特例：开启后该场战斗只能用弹药支付"对得上。）</para>
///
/// <para>实现细节（特别是"为什么不能用 <c>GetResolved()</c> 算弹药费用"、
/// 以及手动出牌的否决点为什么是 <c>ShouldPlay</c>）全部记在
/// <see cref="BulletModePower"/> 的类注释里 —— 那是这套东西最容易写错的地方。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class BulletMode : ModCardTemplate, IAmmoCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Power;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    /// <summary>本卡没有数值占位符。</summary>
    /// <summary>
    /// 每回合白给的弹药数。★ 真值由 <c>Powers/BulletModePower</c> 在能力被施加时
    /// 从这张卡的 <c>DynamicVars["AmmoPerTurn"]</c> 读走 —— 所以改这里会真的改行为。
    /// </summary>
    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DynamicVar("AmmoPerTurn", 3)
    ];

    public BulletMode() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<BulletModePower>(
            choiceContext, Owner.Creature, 1m, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：添加"固有"（设计稿）。
        AddKeyword(CardKeyword.Innate);
    }
}
