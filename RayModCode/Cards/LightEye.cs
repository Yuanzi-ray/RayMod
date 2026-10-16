using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见技能「追光者之瞳」：抽 3 张牌；选择一张手牌放到<b>抽牌堆顶</b>，
/// 且那张牌在打出前<b>费用为 0</b>；层数大于 29 时再抽 2 张。升级后抽 3 → 4。
///
/// <para>"放回抽牌堆顶"照抄原版 <c>PhotonCut</c>：<c>CardPileCmd.Add(card, PileType.Draw,
/// CardPilePosition.Top)</c>。顺序也是照抄它 —— <b>先抽、再选</b>，
/// 这样刚抽到的牌也在候选集里。</para>
///
/// <para>⚠️ <b>0 费的写法是 <c>EnergyCost.SetUntilPlayed(0)</c></b>（失效条件只有 <c>WhenPlayed</c>）——
/// "第一次打出免费、之后恢复原价"。<b>不要</b>用 <c>SetToFreeThisTurn()</c>
/// （回合结束就失效，而牌在抽牌堆里当回合根本打不出来 → 效果作废）或
/// <c>SetToFreeThisCombat()</c>（整场都免费 → 太强）。两个坑都踩过，见 <c>OnPlay</c> 里的注释。</para>
///
/// <para>⚠️ 选牌提示词用 <c>SelectionScreenPrompt</c>，它读本地化键
/// <c>&lt;卡ID&gt;.selectionScreenPrompt</c>，<b>缺失会直接抛异常</b> ——
/// zhs / eng 的 cards.json 里都必须有这个键。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class LightEye : ModCardTemplate, IDepthCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;


    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DynamicVar("ExtraDrawMinDepth", 29),
        new CardsVar("Draw", 3),
        new DynamicVar("ExtraDraw", 2m)
    ];

    public LightEye() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        int draw = DynamicVars["Draw"].IntValue;

        if (DepthCmd.IsDepthGreaterThan(Owner, 29))
        {
            draw += DynamicVars["ExtraDraw"].IntValue;
        }

        // 先抽牌：这样刚抽到的牌也能被选中（PhotonCut / Glimmer 的顺序）。
        await CardPileCmd.Draw(choiceContext, draw, Owner);

        var picked = (await CardSelectCmd.FromHand(
            prefs: new CardSelectorPrefs(SelectionScreenPrompt, 1),
            context: choiceContext,
            player: Owner,
            filter: null,
            source: this)).FirstOrDefault();

        // 手牌为空 / 战斗结束中 / 自动打出的场合都可能拿到空集合。
        if (picked == null)
        {
            return;
        }

        // ⚠️ 必须是 SetUntilPlayed —— "0 费**直到打出为止**"，失效条件**只有 WhenPlayed**。
        //    另外两个看起来像、其实都会错：
        //    - SetToFreeThisTurn()：失效含 EndOfTurn。可是牌被放回抽牌堆顶之后，
        //      当回合已经不可能再打出它了；回合结束时 0 费就没了，下回合抽到是原价
        //      → 这张牌的效果等于从来没存在过。
        //    - SetToFreeThisCombat()：失效是 EndOfCombat。0 费会赖到整场战斗结束，
        //      之后每次抽到它都是 0 费 → 太强。
        //      用户 2026-10-04 纠正："理论上是第一次打出才是零费，后面变成原来费用才对"。
        //    SetUntilPlayed 正是这个语义（原版 Eidolon 就是"弃牌堆所有牌 0 费直到打出"）。
        picked.EnergyCost.SetUntilPlayed(0);

        await CardPileCmd.Add(picked, PileType.Draw, CardPilePosition.Top);
    }

    protected override void OnUpgrade()
    {
        // 升级：抽 3 → 4（设计稿："抽4"）。层数 >29 的 +2 不变。
        DynamicVars["Draw"].UpgradeValueBy(1m);
    }
}
