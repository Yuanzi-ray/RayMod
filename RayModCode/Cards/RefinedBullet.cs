using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Ammo;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 初始卡「精制子弹」：<b>1 费</b>（2026-10-04 从 2 费下调），
/// 你的下一次攻击额外造成相当于当前弹药数的伤害，弹药 -1。
/// 升级后变成"下两次"。
///
/// <para>效果由 <see cref="RefinedBulletPower"/> 承载（卡牌进弃牌堆后没地方记"还欠一次"）。</para>
///
/// <para><b>为什么是"下一次攻击"而不是"这张牌直接打伤害"</b>：设计稿写得很明确，
/// 它是<b>放大器</b> —— 弹药越多收益越大，而且要先垫弹药再打它。
/// 注意加成值是<b>攻击结算那一刻</b>读的弹药，所以「装弹」→「精制子弹」→ 攻击 是最优顺序。</para>
///
/// <para><b>2026-10-04（先古批次）：它同时是先古卡「重构子弹」的转化源。</b>
/// <c>[RegisterArchaicToothTranscendence]</c> 让<b>原版遗物「古老牙齿」</b>（第二幕 Orobas 事件发放）
/// 能在莱伊的牌组里找到这张牌，并把它<b>原地替换</b>成「重构子弹」——
/// 替换保留升级等级与附魔（原版 <c>ArchaicTooth.GetTranscendenceTransformedCard</c> 的行为），
/// 这正是用户 2026-10-04 拍板的口径。⚠️ 被替换掉之后牌组里就没有它了，
/// 所以第二次拿到古老牙齿时那个选项会消失（原版逻辑）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
[RegisterCharacterStarterCard(typeof(RayModCharacter), 1)]
[RegisterArchaicToothTranscendence(typeof(RefactoringBullets))]
public sealed class RefinedBullet : ModCardTemplate, IAmmoCard
{
    /// <summary>
    /// 费用。<b>2026-10-04 用户实测后从 2 调成 1</b>（"精制子弹有点太弱了"）——
    /// 它是初始卡，2 费时"先垫弹药再打它再攻击"的三步combo在本角色的 3 能量回合里几乎排不下。
    /// </summary>
    private const int BaseEnergyCost = 1;

    private const CardType CardKind = CardType.Skill;

    /// <summary>Basic：初始卡牌那一组，不进战斗奖励池。</summary>
    private const CardRarity CardRarityValue = CardRarity.Basic;

    private const TargetType CardTarget = TargetType.Self;

    private const bool ShowInCardLibrary = true;

    /// <summary>占位卡图（等美术时换成 RefinedBullet.png）。</summary>
    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    /// <summary>
    /// <c>Uses</c> = 还能生效几次（升级后 1 → 2）。
    ///
    /// <para><c>AmmoCost</c> = 每次生效后扣掉的弹药数 —— 卡面"每次攻击后弹药-N"的那个 N。</para>
    ///
    /// <para>⚠️ <b>这个变量目前只是"已声明、未接通"</b>：真正扣弹药的那行在<b>共享</b>载体
    /// <see cref="RefinedBulletPower"/> 的 <c>AfterCardPlayed</c> 里
    /// （<c>AmmoCmd.ConsumeAmmo(owner, 1)</c>），而「重构子弹」与本卡<b>共用</b>这个 Power。
    /// 卡文件<b>无法</b>把值传过去 —— 必须先给 <c>AddUses</c> 加一个"每次用量的弹药消耗"参数
    /// （那是 <c>Ammo\RefinedBulletPower.cs</c>，属共享文件，本作业不许改），
    /// 再让这里传 <c>ammoCost: DynamicVars["AmmoCost"].IntValue</c>。
    /// 接通之前请<b>不要</b>把卡面的 1 换成 <c>{AmmoCost:diff()}</c>，否则网页工具改它会变成骗人的旋钮。</para>
    /// </summary>
    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CardsVar("Uses", 1),
        new DynamicVar("AmmoCost", 1)
    ];

    public RefinedBullet() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // Amount = 还能生效几次、系数 = ×1（见 RefinedBulletPower）。
        // ⚠️ 走 AddUses 而不是直接 PowerCmd.Apply：Amount 与"每次用量的系数队列"必须一起动。
        // ⚠️ 弹药消耗（DynamicVars["AmmoCost"]）**还没接通**：这个重载没有"每次用量的弹药消耗"
        //    参数，扣弹药写在 RefinedBulletPower.AfterCardPlayed 里（共享文件，本作业不许改）。
        //    接通后这里要补 `ammoCost: DynamicVars["AmmoCost"].IntValue`。
        await RefinedBulletPower.AddUses(
            choiceContext, Owner.Creature, this, DynamicVars["Uses"].IntValue, multiplier: 1);
    }

    protected override void OnUpgrade()
    {
        // 升级：下 1 次 → 下 2 次（设计稿："你的下两次攻击…"）。
        DynamicVars["Uses"].UpgradeValueBy(1m);
    }
}
