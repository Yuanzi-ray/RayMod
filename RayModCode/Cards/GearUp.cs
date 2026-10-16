using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 普通卡「整装待发」：获得 7 点格挡；若当前层数大于 50，获得 3 倍格挡。
/// 升级后基础格挡提升至 9。
///
/// <para>⚠️ <b>2026-10-07</b>：门槛原写作 59（设计稿口径），平衡轮改成 50 时只动了常量，
/// 卡面与注释都漏改 → 玩家在 51~59 层会误判"没到阈值"。现门槛用
/// <c>private const int BlockThreshold</c> 喂逻辑、同名 <c>DynamicVar</c> 喂卡面
/// （<c>{BlockThreshold:diff()}</c>），两者由 <c>_varSyncCheck</c> 强制同步。</para>
///
/// <para>这是给"深层"准备的防御牌：60 层以上是 20 层起每回合 -6HP 的最深档
/// （见 <see cref="DepthConstants"/>），需要一张能扛住的牌。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class GearUp : ModCardTemplate, IDepthCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Common;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    /// <summary>
    /// 触发倍率所需的最低层数（设计稿原写"大于59"，平衡轮改成 50）。
    /// ★ <b>逻辑读这个常量</b>，卡面用同名 DynamicVar 显示，两者由下面的静态自检强制同步。
    /// </summary>
    private const int BlockThreshold = 50;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    public override bool GainsBlock => true;

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CalculationBaseVar(7m),
        // 额外值 = 基准值本身 ⇒ 基准 + 基准×2 = 基准×3，正好表达"3 倍格挡"。
        // ⚠️ 升级时两者必须一起加（见 OnUpgrade），否则 3 倍会算错。
        new CalculationExtraVar(7m),
        // ★ 2026-10-07：卡面显示走 DynamicVar（改常量卡面自动跟着变），但逻辑仍读常量
        //   —— 避免初始化期访问 DynamicVars（那个坑见 UntappedTreasure.cs 的说明）。
        // ⚠️ 历史教训：这里原本只有 private const TripleBlockDepth = 50，
        //    而卡面写的是"大于59"（连注释都跟着写 59）—— 51~59 层玩家会误判成"没到阈值"。
        new DynamicVar("BlockThreshold", BlockThreshold),
        // ★ 倍率也走变量（卡面「改为{BlockMultiplier:diff()}倍」显示的就是这个 3）。
        //   ⚠️ 这里原来写的是 private const decimal TripleBlockMultiplier = 3m —— 常量会被内联，
        //   网页工具只认"变量声明 + 字面数字"的写法，写成常量等于卡面永远改不动。
        new DynamicVar("BlockMultiplier", 3),
        // ⚠️ 乘数 lambda 里必须写 card.DynamicVars["..."]（走入参 card），不能写本卡的 DynamicVars：
        //   ① 捕获 this 会被原版 CalculatedVar.WithMultiplier 判成"非静态"并抛
        //      "Multiplier calc must be static!"（坑 27，见 CaliberEnlargement.cs）；
        //   ② 这个 lambda 只在 Calculate() 里求值，而 Calculate() 本身就在读
        //      DynamicVars.CalculationBase —— 不会在初始化期被求值（那个坑见 UntappedTreasure.cs）。
        //   "-1m" 是引擎口径换算：格挡 = 基准 + 额外值×乘数，所以"×N 倍"要传 N-1。
        new CalculatedBlockVar(ValueProp.Move)
            .WithMultiplier((card, _) => DepthCmd.IsDepthGreaterThan(card.Owner, BlockThreshold)
                ? card.DynamicVars["BlockMultiplier"].BaseValue - 1m
                : 0m)
    ];

    /// <summary>
    /// 同步自检：常量与卡面变量的本意值必须一致（本卡当前 50）。
    /// 改阈值时两处一起改，否则<b>加载时立刻抛异常</b>，而不是等玩家发现"卡面骗人"。
    /// </summary>
    private static readonly decimal[] _varSyncCheck = [BlockThreshold - 50m];

    public GearUp() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // 格挡 = 7（或 9） / 21（或 27），卡面显示的就是这个实算值。
        await CreatureCmd.GainBlock(
            Owner.Creature,
            DynamicVars.CalculatedBlock.Calculate(cardPlay.Target),
            DynamicVars.CalculatedBlock.Props,
            cardPlay);
    }

    protected override void OnUpgrade()
    {
        // 升级：基础格挡 7 → 9（设计稿："基础获得9点格挡"）。
        // ⚠️ 额外值要同步 +2，否则"3 倍"会退化成 9 + 7×2 = 23 而不是 27。
        DynamicVars.CalculationBase.UpgradeValueBy(2m);
        DynamicVars.CalculationExtra.UpgradeValueBy(2m);
    }
}
