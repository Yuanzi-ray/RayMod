using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models.Powers;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 稀有技能「旅行」：<b>层数小于 9 时才能打出</b>，<b>阻止下 1 次你受到的生命值损伤</b>。<b>消耗</b>。
/// 升级后费用 2 → 1。
///
/// <para><b>它其实是原版「缓冲」（<see cref="BufferPower"/>）的换皮</b>：
/// 原版那张牌的描述就是"阻止下一次你受到的生命值损伤"，
/// 所以效果直接施加原版的 Power，<b>不自己写一套</b> —— 少一处可能出错的地方，
/// 而且悬停、图标、本地化全是现成的（powers.json 里不需要新键）。</para>
///
/// <para><b>用户 2026-10-04 明确："旅行也挡层数自伤"。</b>
/// 这一条是自动成立的，不需要额外写：层数自伤走
/// <c>CreatureCmd.Damage(..., DamageProps.nonCardUnpowered, ...)</c>
/// （<c>DepthPower.ApplyEndOfTurnDepthDamage</c>，两条现在都在<b>回合结束</b>触发），
/// 而<b>可格挡伤害同样会走 HP 损失管线</b> —— <c>CreatureCmd.Damage</c> 内部先扣格挡，
/// 再用<b>剩余量</b>调 <c>Hook.ModifyHpLost(..., HpLossHookPhase.AfterOsty, ...)</c>，
/// 那里会调到 <c>BufferPower.ModifyHpLostAfterOstyLate</c>（它不看 props，只看目标是不是自己）
/// —— 把金额改成 0 再自减 1 层。
/// 所以"20 层起 -6"和"51 层起 -7"都各会被挡住<b>一次</b>
/// （它们是<b>两次独立的伤害调用</b>，所以缓冲只吃掉第一笔，这是原版行为）。</para>
///
/// <para>⚠️ <b>2026-10-04 把它们改成可格挡后新增的细节（已核对原版实现）</b>：
/// 若格挡把某一条<b>完全吸收</b>（剩余量为 0），<c>BufferPower</c> 的返回值不发生
/// 变化（0 → 0），于是它<b>不会</b>被记进 <c>Hook.ModifyHpLost</c> 的 modifiers，
/// <c>AfterModifyingHpLostAfterOsty</c> 也就不触发 —— 即<b>不会白扣一次「旅行」</b>。
/// 这是原版 <c>BufferPower</c> 刻意改用 "Late" 变体换来的行为
/// （它自己的注释："more player-friendly for them to trigger first so that this power
/// doesn't have to decrement"），不是我们实现的，但正好是我们要的效果。</para>
///
/// <para>门槛走 <see cref="DepthGatedCard"/>：层数门槛的唯一通道，
/// 「探井人」的豁免对它自动生效（别自己写 <c>IsPlayable</c>，见坑 30）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Travel : DepthGatedCard, IDepthCard
{
    private const int BaseEnergyCost = 2;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;


    // 阻止几次生命值损伤原先写死成 `private const int BufferCharges = 1;` ——
    // 卡面因此只能写死"下 1 次"。现在它是动态变量 BufferCharges（见 CanonicalVars）。

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    /// <summary>
    /// <c>MaxPlayableDepth</c> = 层数门槛（"层数小于 N 时才能打出"）。
    /// <c>BufferCharges</c> = 阻止几次生命值损伤（设计稿"下 1 次"；原先写死在 const 里，卡面改不动）。
    /// </summary>
    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DynamicVar("MaxPlayableDepth", 9),
        new DynamicVar("BufferCharges", 1)
    ];

    /// <summary>消耗词条（设计稿明确写了）。</summary>
    public override IEnumerable<CardKeyword> CanonicalKeywords =>
    [
    ];

    public Travel() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    /// <summary>层数小于 9 才满足条件。</summary>
    protected override bool DepthConditionMet => DepthCmd.IsDepthLessThan(Owner, 9);

    protected override async Task OnPlayGated(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<BufferPower>(
            choiceContext, Owner.Creature, DynamicVars["BufferCharges"].IntValue, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：费用 2 → 1（设计稿："费用减1"）。
        EnergyCost.UpgradeBy(-1);
    }
}
