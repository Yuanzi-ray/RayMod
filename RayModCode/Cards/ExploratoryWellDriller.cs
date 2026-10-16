using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Characters;
using RayMod.Depth;
using RayMod.Powers;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 稀有 能力「探井人」（<b>费用 2</b>）：<b>每回合第一张"有层数限制的牌"无视它的层数条件</b>。
/// <b>虚无</b>，升级后去掉虚无。
///
/// <para>它什么都不"打" —— 只是一个豁免开关，判定在 <see cref="RayMod.Depth.DepthGate"/>，
/// 各张门槛牌通过 <see cref="RayMod.Depth.DepthGatedCard"/> 基类自动接入
/// （所以「谋生之道」「去咧嘴谷」「无尽黑暗」「定向猎杀」「沙丘」都不需要为它改一行代码）。</para>
///
/// <para>⚠️ 以后写新的层数门槛牌时<b>必须继承 <c>DepthGatedCard</c></b>，
/// 手写 <c>IsPlayable</c> 会让这张牌对那张卡静默失效（不报错，只是不生效）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class ExploratoryWellDriller : ModCardTemplate, IDepthCard
{
    private const int BaseEnergyCost = 2;
    private const CardType CardKind = CardType.Power;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CardsVar("Uses", 2)
    ];

    /// <summary>虚无词条（设计稿明确写了；升级后去掉）。</summary>
    public override IEnumerable<CardKeyword> CanonicalKeywords =>
    [
        CardKeyword.Ethereal
    ];

    public ExploratoryWellDriller() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<ExploratoryWellDrillerPower>(
            choiceContext, Owner.Creature, DynamicVars["Uses"].BaseValue, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：去掉"虚无"（设计稿："去虚无"）。
        RemoveKeyword(CardKeyword.Ethereal);
    }
}
