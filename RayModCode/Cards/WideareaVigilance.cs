using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Beasts;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 普通卡「广域警觉」：造成 12 点伤害，召唤一只沙地兽。<b>升级后召唤 2 只</b>（伤害不变）。
///
/// <para>★ 2026-10-07 用户改动：伤害 6 → <b>12</b>；升级从"伤害 6→9"改为"<b>召唤 1 → 2 只</b>"。
/// 召唤数走 <c>CardsVar("Summon")</c> + <c>UpgradeValueBy</c>，而卡面文案用的是
/// <c>{Summon}</c> 占位符 —— 所以升级后卡面会<b>自动</b>显示"召唤2只"，不需要额外改文案。</para>
///
/// <para>设计稿的类名是 <c>WIDEAREA_VIGILANCE</c>（连写，中间没有第二个大写），
/// 所以类名必须写成 <see cref="WideareaVigilance"/> —— 写成 <c>WideAreaVigilance</c>
/// 会推导出 <c>RAY_MOD_CARD_WIDE_AREA_VIGILANCE</c>，本地化键就对不上了。</para>
///
/// <para>它是"攻击 + 召唤"的合体牌：召唤走 <see cref="BeastCmd.Summon"/>，
/// 所以层数 &gt;50 时伤害照打、只是不召唤（用户 2026-10-03 确认的口径）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class WideareaVigilance : ModCardTemplate, ISandBeastCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Common;
    private const TargetType CardTarget = TargetType.AnyEnemy;
    private const bool ShowInCardLibrary = true;

    /// <summary>占位卡图（等正式美术时换成 WideareaVigilance.png，路径已按类名拼好）。</summary>
    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        // 2026-10-07：伤害 6 → 12（用户要求"增强到12点伤害"）。
        new DamageVar(12m, ValueProp.Move),
        new CardsVar("Summon", 1)
    ];

    public WideareaVigilance() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        // 1) 先结算伤害（设计稿顺序："造成12点伤害，召唤沙地兽"）。
        await DamageCmd.Attack(DynamicVars.Damage.BaseValue)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
            .Execute(choiceContext);

        // 2) 再召唤。
        await BeastCmd.Summon(
            choiceContext, Owner, DynamicVars["Summon"].IntValue, cardSource: this);
    }

    protected override void OnUpgrade()
    {
        // ★ 2026-10-07 用户改动：升级不再加伤害，改为**多召唤 1 只**（1 → 2）。
        // 卡面 {Summon} 会随之自动显示"2"。
        DynamicVars["Summon"].UpgradeValueBy(1m);
    }
}
