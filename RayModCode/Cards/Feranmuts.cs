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
/// 稀有能力「巨兽」：<b>每回合多抽 1 张牌</b>、<b>每回合沙地兽上限 +1</b>，
/// 并且<b>每回合开始时选择 2 张手牌变为「沙地兽」</b>。升级后添加<b>保留</b>。
///
/// <para>用户 2026-10-04 拍板："每回合都变" —— 所以每个回合开始都会弹一次选牌界面。
/// 这条在本项目里是**第一次**出现"回合开始要玩家做选择"，手感上如果觉得烦，
/// 改成"随机 2 张"只是一行的事。</para>
///
/// <para><b>用户 2026-10-04 更正：上限 +1 也是每回合都加</b>
/// （之前只做了"打出时 +1"，从第二回合起就不涨了）。所以这里打出时给的那一次
/// 相当于"本回合的那一份"，之后每回合开始由 <see cref="FeranmutsPower"/> 再 +1。</para>
///
/// <para>⚠️ 上限会被夹在 <c>DepthConstants.MinionCapMax</c>（= 6，用户确认过的硬顶）——
/// 涨到 6 之后这一条就白给了（角标仍会继续涨，那是"加过多少"的记录）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Feranmuts : ModCardTemplate, ISandBeastCard
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
        new CardsVar("Cap", 1),
        // ★ 下面两个的真值由 Powers/FeranmutsPower 在能力被施加时从这张卡读走。
        new CardsVar("ExtraDraw", 1),
        new CardsVar("Transform", 2)
    ];

    public Feranmuts() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<FeranmutsPower>(
            choiceContext, Owner.Creature, DynamicVars["Cap"].BaseValue, Owner.Creature, this);

        // 上限在这里加（用卡牌自己的 choiceContext），而不是在 Power 的 BeforeApplied 里 ——
        // 那里拿不到真正的 PlayerChoiceContext，而 ModifyCap 降上限时会去杀多余的兽。
        await BeastCmd.ModifyCap(choiceContext, Owner, DynamicVars["Cap"].IntValue);
    }

    protected override void OnUpgrade()
    {
        // 升级：添加"保留"（设计稿："添加保留词条"）。数值不变。
        AddKeyword(CardKeyword.Retain);
    }
}
