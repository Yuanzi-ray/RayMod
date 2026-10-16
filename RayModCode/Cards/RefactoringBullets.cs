using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Ammo;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 先古卡「重构子弹」：<b>1 费攻击</b>，造成 <b>30</b> 点伤害，
/// 你的下<b>两张</b>攻击牌额外造成相当于当前弹药数的伤害；<b>升级后系数变 ×2</b>。
///
/// <para><b>怎么拿到它</b>（用户 2026-10-04 拍板，"走 ArchaicTooth 这条"）：
/// 原版先古遗物「<b>古老牙齿</b>」（第二幕 <c>Orobas</c> 事件发放）会把牌组里的
/// 「精制子弹」<b>原地替换</b>成它 —— 映射声明在 <see cref="RefinedBullet"/> 上
/// （<c>[RegisterArchaicToothTranscendence]</c>，RitsuLib 的补丁会接管原版的
/// <c>GetTranscendenceStarterCard</c> / <c>GetTranscendenceTransformedCard</c>）。
/// 替换<b>保留升级等级与附魔</b>，正是拍板要的口径。所以本卡<b>不需要自己的事件或遗物</b>。</para>
///
/// <para><b>效果本体与「精制子弹」同源</b>：都是 <see cref="RefinedBulletPower"/> 在承载
/// （下 N 次攻击 + 弹药数、每次 -1 弹药），这里只是 <c>uses = 2</c> 且系数不同。
/// 两张牌各自的系数按"每次用量"记在同一个队列里，所以手牌里同时有精制子弹和重构子弹时，
/// <b>每次加成按打出它的那张牌算</b>（不是"后打的覆盖前面的"）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class RefactoringBullets : ModCardTemplate, IAmmoCard
{
    private const int BaseEnergyCost = 1;

    private const CardType CardKind = CardType.Attack;

    /// <summary><b>Ancient</b>：先古牌，不进任何奖励池（原版三处显式排除），
    /// 只能由「古老牙齿」这种先古遗物发放。</summary>
    private const CardRarity CardRarityValue = CardRarity.Ancient;

    private const TargetType CardTarget = TargetType.AnyEnemy;

    private const bool ShowInCardLibrary = true;

    /// <summary>本体伤害（设计稿 30，升级<b>不加</b>伤害 —— 升级只改附加效果的系数）。</summary>
    private const decimal BaseDamage = 30m;

    /// <summary>覆盖"下两张"攻击牌（升级也只改系数，不改张数）。</summary>
    private const int Uses = 2;

    /// <summary>升级后的弹药系数（未升级 ×1）。</summary>
    private const int UpgradedMultiplier = 2;

    /// <summary>占位卡图（等美术时换成 RefactoringBullets.png）。</summary>
    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DamageVar(BaseDamage, ValueProp.Move)
    ];

    public RefactoringBullets() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        // 1) 本体 30 点伤害。
        await DamageCmd.Attack(DynamicVars.Damage.BaseValue)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_attack_blunt", null, "heavy_blunt_attack.mp3")
            .Execute(choiceContext);

        // 2) 下两张攻击牌：额外造成 弹药 × 系数（升级后 ×2）。
        // ⚠️ 走 AddUses（唯一入口），Amount 与系数队列一起进。
        await RefinedBulletPower.AddUses(
            choiceContext, Owner.Creature, this, Uses, multiplier: IsUpgraded ? UpgradedMultiplier : 1);
    }

    // ⚠️ 刻意<b>不覆写 OnUpgrade</b>：升级既不涨伤害也不涨张数，
    //    只把系数 1 → 2，而那个分支在 OnPlay 里读 IsUpgraded 就够了
    //    （「井」那张牌同款写法）。
}
