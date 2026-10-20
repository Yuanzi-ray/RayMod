using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

using RayMod.Beasts;

namespace RayMod.Cards;

/// <summary>
/// 普通卡「背扇」：获得 8 点格挡；将一张「沙地兽」添加到<b>抽牌堆</b>；层数 +3。<b>消耗</b>。
/// 升级后层数 +5。
///
/// <para>注意它加的是<b>抽牌堆</b>而不是手牌 —— 和「饲养」正好互补：
/// 饲养是立刻能用但占手牌位，背扇是延后到手（位置随机）。</para>
///
/// <para><b>★ 2026-10-17 修玩家反馈（Wave）两条</b></para>
/// <list type="number">
/// <item><b>「生成的衍生牌看不懂」</b> ⇒ 补 <see cref="AdditionalHoverTips"/>，悬停本卡就能看到
/// 「沙地兽」那张衍生牌的完整说明。</item>
/// <item><b>「洗入抽牌堆时没有牌进入牌堆的特效」</b> ⇒ 见 <see cref="OnPlay"/> 里
/// <c>CardCmd.PreviewCardPileAdd</c> 的注释。<b>这是原版的标准写法，我之前漏了。</b></item>
/// </list>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class BackFan : ModCardTemplate, ISandBeastCard, IDepthCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Common;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    public override bool GainsBlock => true;

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new BlockVar(8m, ValueProp.Move),
        new CardsVar("Depth", 3)
    ];

    /// <summary>
    /// ★ 2026-10-17（玩家反馈）：本卡会往抽牌堆塞一张「沙地兽」，
    /// 但卡面文字里只有一个名字，玩家看不到它到底做什么 ⇒ 挂一条指向衍生牌本身的悬停提示。
    ///
    /// <para>走 <c>ModCardTemplate.AdditionalHoverTips</c>（基类的 <c>ExtraHoverTips</c> 是 sealed）。
    /// 传 <c>false</c> 而不是 <c>IsUpgraded</c>：本卡生成的沙地兽<b>永远是未升级版</b>。</para>
    /// </summary>
    protected override IEnumerable<IHoverTip> AdditionalHoverTips =>
    [
        HoverTipFactory.FromCard<RayMod.Cards.SandBeast>(upgrade: false)
    ];

    /// <summary>消耗词条（设计稿明确写了）。</summary>
    public override IEnumerable<CardKeyword> CanonicalKeywords =>
    [
        CardKeyword.Exhaust
    ];

    public BackFan() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await CreatureCmd.GainBlock(Owner.Creature, DynamicVars.Block, cardPlay);

        // 造的是「沙地兽」卡（RayMod.Cards.SandBeast），不是召唤物。
        var beastCard = CombatState.CreateCard<RayMod.Cards.SandBeast>(Owner);

        // ★★ 2026-10-17：**必须用 CardCmd.PreviewCardPileAdd 套住**，否则牌悄悄沉进抽牌堆、玩家毫无感觉
        //     （这就是玩家报的"洗入抽牌堆时没有牌进入牌堆的特效"）。
        //
        //     这是**原版的标准写法**，别自己造特效。反编译里所有"生成牌进抽牌堆"的原版卡都这么写：
        //       Metamorphosis.cs:31   CardCmd.PreviewCardPileAdd(await CardPileCmd.AddGeneratedCardToCombat(item, PileType.Draw, Owner, CardPilePosition.Random));
        //       Reave.cs:42           CardCmd.PreviewCardPileAdd(await CardPileCmd.AddGeneratedCardsToCombat(enumerable, PileType.Draw, Owner, CardPilePosition.Random));
        //       Dirge.cs:47 / CaptureSpirit.cs:35 / GraveWarden.cs:33   同上
        //
        //     PreviewCardPileAdd(CardPileAddResult, float time = 1.2f, CardPreviewStyle = HorizontalLayout)
        //     是**同步 void**（不要 await），内部：TestMode 关、战斗未结束、且是本地玩家的牌才展示。
        //
        //     ⚠️ 血泪记录：我先试过手写 NCardFlyShuffleVfx.Create(Play→Draw, …) 来补特效，
        //        结果**抛 NullReferenceException** —— PileTypeExtensions.GetTargetPosition(null) 对
        //        PileType.Play 分支会解引用 node.Size，而 Create 传的就是 null。
        //        更糟的是被我的 try/catch 吞了，实机表现为"什么也没发生"，只能靠 godot.log 才看出来。
        CardCmd.PreviewCardPileAdd(
            await CardPileCmd.AddGeneratedCardToCombat(
                beastCard, PileType.Draw, Owner, CardPilePosition.Random));

        await DepthCmd.AddDepth(choiceContext, Owner, DynamicVars["Depth"].IntValue, cardSource: this);
    }

    protected override void OnUpgrade()
    {
        // 升级：层数 +3 → +5（设计稿："升级后层数+5"）。
        DynamicVars["Depth"].UpgradeValueBy(2m);
    }
}
