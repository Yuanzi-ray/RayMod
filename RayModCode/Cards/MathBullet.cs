using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using RayMod.Ammo;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见技能「乘算子弹」：<b>弹药翻倍</b>，消耗。升级后费用 1 → 0。
///
/// <para>走 <c>AmmoCmd.SetAmmo</c> —— 它会夹在上限内（<c>AmmoState.cs</c> 用 <c>Math.Clamp(value, 0, Cap)</c>），
/// 所以"翻倍"不会突破上限：5/7 翻倍是 7，不是 10。</para>
///
/// <para>没有动态变量（翻倍是"当前值 × 2"，不是某个可显示的固定数）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class MathBullet : ModCardTemplate, IAmmoCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    /// <summary>消耗词条（设计稿写了）。</summary>
    public override IEnumerable<CardKeyword> CanonicalKeywords =>
    [
        CardKeyword.Exhaust
    ];

    public MathBullet() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        AmmoCmd.SetAmmo(Owner, AmmoCmd.GetAmmo(Owner) * 2);

        return Task.CompletedTask;
    }

    protected override void OnUpgrade()
    {
        // 升级：费用 -1（设计稿："升级后费用-1"）。
        EnergyCost.UpgradeBy(-1);
    }
}
