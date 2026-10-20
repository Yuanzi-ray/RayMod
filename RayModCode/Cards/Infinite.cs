using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Ammo;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 稀有技能「无限」：<b>本回合内，弹药数量可以超过上限</b>。<b>消耗</b>。升级后费用 1 → 0。
///
/// <para><b>这是"弹药不能超过上限"的唯一例外</b>（用户 2026-10-03 定的基础规则是硬夹，
/// 见 <see cref="AmmoState"/>）。开关本体就是
/// <see cref="AmmoState.OverCapThisTurn"/>，由 <see cref="AmmoCmd.SetOverCapThisTurn"/> 打开，
/// 由 <c>AmmoPower.AfterPlayerTurnStart</c> 在<b>下个回合开始时</b>关掉并把超出的部分夹回上限
/// —— 字面语义"本回合内"。</para>
///
/// <para><b>三个写入路径都认这个开关</b>（<c>AddAmmo</c> / <c>SetAmmo</c> / <c>ModifyCap</c>）：
/// 所以这个回合里「快速装填」「填装形态」「重大升级」给的弹药全都不会被上限吃掉，
/// 弹药能一路堆到几十点 —— 配合「狙击」「霰弹枪」「以弹为盾」是一波爆发。</para>
///
/// <para>⚠️ 它是<b>消耗</b>牌：开完就没了，所以这是"一次性爆发回合"而不是常驻状态。
/// 代价是打完之后弹药会被夹回上限 —— 超出的部分不会留到下个回合。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Infinite : ModCardTemplate, IAmmoCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    /// <summary>本卡没有数值占位符。</summary>
    protected override IEnumerable<DynamicVar> CanonicalVars => [];

    /// <summary>消耗词条（设计稿明确写了）。</summary>
    public override IEnumerable<CardKeyword> CanonicalKeywords =>
    [
        CardKeyword.Exhaust
    ];

    public Infinite() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        AmmoCmd.SetOverCapThisTurn(Owner, true);

        // ★ 2026-10-12：HUD 演出（上限那位变异 → 一刀劈开 → 上限变 ∞）。
        //   放在 SetOverCapThisTurn **之后**：那一步已经 SyncToPower 推过一次 HUD，
        //   演出再接管那串字（先藏起来自己画，最后亮回真数字）。
        Hud.HudInfinityFx.Play(Owner);

        // ★ 2026-10-20：**世界层**（她身后炸开一个大 ∞ + 脚下冲击波 + 音效 + 一记抖屏）。
        //   和上面那一路**同一拍起手** —— HUD 只管"上限那个字"，屏幕主体靠这一层，
        //   否则整场演出只有角落 230px 在动（用户："现在观感依旧不够"）。
        Art.CardFieldVfx.SpawnInfinityBurst(Owner);

        await Task.CompletedTask;
    }

    protected override void OnUpgrade()
    {
        // 升级：费用 1 → 0（设计稿："费用变为零"）。
        EnergyCost.UpgradeBy(-1);
    }
}
