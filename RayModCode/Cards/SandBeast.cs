using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Beasts;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// <b>衍生牌</b>「沙地兽」：0 费，召唤一只沙地兽，抽一张牌，<b>消耗</b>。升级后召唤两只。
///
/// <para><b>2026-10-10 用户要求：「沙地兽」只作为衍生牌 —— 不进初始卡组、不进卡牌奖励。</b>
/// 本次已<b>删掉</b> <c>[RegisterCharacterStarterCard(typeof(RayModCharacter), 1)]</c>
/// （原第 28 行，初始牌组 13 → 12 张）。</para>
///
/// <para><b>「不进卡牌奖励」已满足，未改稀有度</b>（2026-10-10 查证，见下
/// <see cref="CardRarityValue"/> 的说明）：原本的 <c>CardRarity.Basic</c> 就已经把这张牌
/// 挡在战斗奖励之外，所以 <c>[RegisterCard]</c> / <c>CardRarity</c> /
/// <c>ShowInCardLibrary</c> 三处<b>一个都没动</b>。
/// 它的获取途径只有 6 处效果生成（<c>Raise</c> / <c>BackFan</c> / <c>WhySoMany</c> /
/// <c>FeranmutsPower</c> / <c>NurturePower</c> / <c>SandBeastLairPower</c>）
/// 与「装弹」的召唤（<c>LoadGun.OnPlay</c>，2026-10-10 新增）。</para>
///
/// <para>设计稿的类名就是 <c>SAND_BEAST</c>（→ ID <c>RAY_MOD_CARD_SAND_BEAST</c>），
/// 所以这个卡类叫 <see cref="SandBeast"/>。</para>
///
/// <para><b>⚠️ 注意重名</b>：召唤物本身也叫 <c>SandBeast</c>，在
/// <see cref="RayMod.Beasts"/> 命名空间下（<c>RayMod.Beasts.SandBeast</c>）。
/// 两者是<b>不同的类</b>：本类是"打出后召唤"的卡，那个是场上站着的那只。
/// 本类不直接引用召唤物类型（召唤统一走 <see cref="BeastCmd"/>），所以不会歧义。</para>
///
/// <para>这张牌是整套"沙地兽"体系的主要入口：它是 0 费、自带抽牌、
/// 打完就消耗，所以打一张 = 挂一层印记 + 补一张手牌。注意<b>层数没有任何关系</b>，
/// 召唤本身只受沙地兽上限和层数减益（10/30/50 层）影响。</para>
///
/// <para><b>⚠️ <c>[RegisterCard]</c> 这一行不能删</b>（2026-10-10 查证）：它只负责把卡放进
/// <see cref="RayModCardPool"/>，<b>不是</b>"会进卡牌奖励"的原因（奖励由稀有度闸门决定，
/// 战斗奖励只 roll Common/Uncommon/Rare，见 CardRarityOdds.cs + CardRarityExtensions.cs）。
/// 但删掉它会让这张卡<b>不属于任何卡池</b> —— <c>CardModel.Pool</c> 找不到池子时直接抛
/// <c>InvalidProgramException("Card ... is not in any card pool!")</c>
/// （<c>Core\Models\CardModel.cs:298-317</c>），而卡图/能量图标/卡框（<c>VisualCardPool</c>）
/// 与「动物图册」<c>AnimalGuidePower</c> 的候选池都走 <c>Pool</c>/<c>AllCards</c>，
/// 6 处 <c>CreateCard&lt;SandBeast&gt;</c> 一渲染就会炸。
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class SandBeast : ModCardTemplate, ISandBeastCard
{
    /// <summary>0 费（设计稿）。</summary>
    private const int BaseEnergyCost = 0;

    private const CardType CardKind = CardType.Skill;

    /// <summary>
    /// <c>Basic</c>：它是"初始卡牌"那一组里的，不进战斗奖励池。
    ///
    /// <para><b>2026-10-10 查证（用户要求"不进入卡牌奖励"）：</b>这一条<b>已经满足，
    /// 无需改动</b> —— 稀有度保持 <c>Basic</c> 不变。依据：</para>
    /// <list type="bullet">
    /// <item>战斗奖励走 <c>RewardsSet.cs:230</c> →
    /// <c>CardCreationOptions.ForRoom</c>（<c>CardCreationOptions.cs:99-106</c>，Monster/Elite/Boss/Shop
    /// 各自赔率）→ <c>CardFactory.CreateForReward</c>（<c>CardFactory.cs:214-242</c>）。</item>
    /// <item>其中 <c>CardRarityOdds.RollWithoutChangingFutureOdds</c> / <c>RollWithBaseOdds</c>
    /// <b>只会返回 Rare/Uncommon/Common</b>（<c>CardRarityOdds.cs:96-134</c>）；
    /// 再经 <c>GetNextAllowedRarity</c> 向上走，而
    /// <c>CardRarityExtensions.GetNextHighestRarityWithWrapping</c> 的环是
    /// <c>Basic→Common→Uncommon→Rare→Common</c>（<c>CardRarityExtensions.cs:19-22</c>）——
    /// <b>永远回不到 Basic</b>。</item>
    /// <item>Uniform 赔率分支还额外显式排掉 Basic 与 Ancient（<c>CardFactory.cs:221-223</c>）。</item>
    /// <item>商店 <c>CreateForMerchant</c> 也显式滤 Basic（<c>CardFactory.cs:47, 72</c>）；
    /// 战斗内随机生成 <c>FilterForCombat</c> 同样排除 Basic/Ancient/Event
    /// （<c>CardFactory.cs:161</c>）。</item>
    /// </list>
    /// <para><b>⚠️ <c>[RegisterCard]</c> 那一行也不能删</b>：它只把卡放进
    /// <see cref="RayModCardPool"/>，与奖励无关；但删掉它这张卡就<b>不属于任何卡池</b>，
    /// <c>CardModel.Pool</c> 会直接抛 <c>InvalidProgramException("Card ... is not in any card pool!")</c>
    /// （<c>Core\Models\CardModel.cs:298-317</c>），卡图/能量图标/卡框
    /// （<c>VisualCardPool</c>）与「动物图册」<c>AnimalGuidePower.cs:106</c> 的候选池都会炸。</para>
    /// </summary>
    private const CardRarity CardRarityValue = CardRarity.Basic;

    /// <summary>召唤自己，不需要选目标。</summary>
    private const TargetType CardTarget = TargetType.Self;

    /// <summary>
    /// <b>保持为 true</b>（2026-10-10 未改动）：用户只说"不进卡牌奖励"，没说"图鉴看不见"。
    /// <c>ShouldShowInCardLibrary</c> 的唯一消费点是卡牌图鉴
    /// （<c>NCardLibraryGrid.cs:157</c>，"Set via constructor parameter to block a card from
    /// appearing in the Card Library screen"，<c>CardModel.cs:826-829</c>），与奖励生成无关；
    /// 原版衍生牌（<c>Shiv</c> 等）也照样显示在图鉴里。
    /// </summary>
    private const bool ShowInCardLibrary = true;

    /// <summary>占位卡图（等美术时换成 SandBeast.png，路径已按类名拼好）。</summary>
    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    /// <summary>召唤数量（升级后 1 → 2）与<b>抽牌数</b>。</summary>
    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CardsVar("Summon", 1),
        // 抽牌数走变量（原先写在 OnPlay 里是字面 1 → 卡面只能写死"抽1张牌"，改不动）。
        new CardsVar("Draw", 1)
    ];

    /// <summary>消耗词条（设计稿明确写了）。</summary>
    public override IEnumerable<CardKeyword> CanonicalKeywords =>
    [
        CardKeyword.Exhaust
    ];

    public SandBeast() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // 1) 召唤。走 BeastCmd（唯一入口）：它负责层数减益、上限、满上限随机替换、挂印记、广播召唤事件。
        //    ⚠️ 层数 >50 时这里一只都不会召唤，但抽牌照常 —— 用户 2026-10-03 确认过这个口径。
        await BeastCmd.Summon(choiceContext, Owner, DynamicVars["Summon"].IntValue, cardSource: this);

        // 2) 抽牌（设计稿："召唤一只沙地兽，抽一张牌"）。张数走变量，卡面才能用 {Draw}。
        await CardPileCmd.Draw(choiceContext, DynamicVars["Draw"].BaseValue, Owner);
    }

    protected override void OnUpgrade()
    {
        // 升级：召唤 1 只 → 2 只（设计稿："召唤两只沙地兽"）。
        DynamicVars["Summon"].UpgradeValueBy(1m);
    }
}
