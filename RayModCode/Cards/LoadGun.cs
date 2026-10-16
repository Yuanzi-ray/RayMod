using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Ammo;
using RayMod.Beasts;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 初始卡「装弹」：获得 3 点格挡；弹药 +2，层数 +2。升级后：格挡 4，弹药 +2，层数 +3。
///
/// <para>设计稿里的 5 张初始卡之一。它同时喂两个资源系统，
/// 是最能体现"莱伊靠层数与弹药双线运转"的牌。</para>
///
/// <para><b>2026-10-04 平衡调整：新增 3 点格挡（升级后 4）</b>（用户拍板，针对"开局太弱"）。
/// 它 1 费只加资源、完全不防守，在开局那几回合等于白让一次出牌机会 ——
/// 补上格挡后它能干"边铺资源边站住"的活。数值刻意压得比同费防御牌低
/// （「防御」5、「背扇」8），因为它已经额外给了弹药和层数。</para>
///
/// <para><b>2026-10-10 用户要求：新增"召唤 1 只沙地兽"</b>（第 4 步，见 <c>OnPlay</c>）。
/// 配合"沙地兽从初始牌组里删掉"一起看：开局第一回合抽到这张牌就能召唤一只沙地兽，
/// 沙地兽的入口从"初始牌组带一张召唤卡"变成"由装弹直接召唤"。
/// ⚠️ 是<b>真召唤</b>（<c>BeastCmd.Summon</c>，场上多一只），<b>不是</b>生成一张
/// 「沙地兽」卡到手里 —— 两者不是一回事。</para>
///
/// <para><b>⚠️ 连带改动：本卡必须加 <see cref="ISandBeastCard"/></b>（2026-10-10）：
/// 本工程口径是"卡面文案里提到「沙地兽」的牌就实现它"（见 <c>Beasts\ISandBeastCard.cs</c>），
/// 由 <c>_tools\check_sandbeast_cards.ps1</c> 强制"文案 ↔ 接口"一致（坑 29）。
/// 加了召唤那句之后文案里就有「沙地兽」了，不加接口校验器会报
/// <c>[文案有、代码没实现接口] RAY_MOD_CARD_LOAD_GUN</c> 并 exit 1；
/// 同时「动物图册」<c>AnimalGuidePower</c> 的候选池也会漏掉这张牌。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
[RegisterCharacterStarterCard(typeof(RayModCharacter), 1)]
public sealed class LoadGun : ModCardTemplate, IAmmoCard, IDepthCard, ISandBeastCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Skill;

    /// <summary>
    /// <c>Basic</c>：它是<b>初始卡组</b>的一员，不进战斗奖励池
    /// （原版 <c>CardFactory.CreateForReward</c> 会滤掉 Basic 与 Ancient，见 CardFactory.cs:223）。
    ///
    /// <para>2026-10-03 用户要求从 <c>Common</c> 改成 <c>Basic</c>。</para>
    ///
    /// <para>⚠️ <b>副作用（改别的卡的稀有度前先看这条）</b>：改完之后卡池里剩下
    /// <b>4 张</b>非 Basic 牌（有所发现 / 潜行 / 快速装填 / 深入），
    /// 正好卡在<b>战斗奖励能生成的最低线</b>上（3 个奖励位 + 1 张容错的由来见交接文档坑 5：
    /// 黑名单在循环里增长，非 Basic 集合为空就会抛
    /// <c>InvalidOperationException</c>）。<b>再少一张，战斗结束的选牌奖励就会崩</b> ——
    /// 到时候要么把某张卡改回 Common，要么再加一张非 Basic 的卡。</para>
    /// </summary>
    private const CardRarity CardRarityValue = CardRarity.Basic;

    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    /// <summary>这张牌会给格挡（原版靠它决定格挡相关动画/悬停，13 张 BlockVar 牌都这么写）。</summary>
    public override bool GainsBlock => true;

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new BlockVar(3m, ValueProp.Move),
        new CardsVar("Ammo", 2),
        new CardsVar("Depth", 2)
    ];

    public LoadGun() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // 先给格挡：和「背扇」等牌的写法一致（CreatureCmd.GainBlock + DynamicVars.Block）。
        await CreatureCmd.GainBlock(Owner.Creature, DynamicVars.Block, cardPlay);

        AmmoCmd.AddAmmo(Owner, DynamicVars["Ammo"].IntValue);
        await DepthCmd.AddDepth(choiceContext, Owner, DynamicVars["Depth"].IntValue, cardSource: this);

        // 4) 2026-10-10 用户要求：召唤 1 只沙地兽。
        //    ⚠️ 「召唤」的唯一入口是 BeastCmd.Summon —— 照抄「沙地兽」这张牌自己的写法
        //    （SandBeast.cs:70）。它内部负责层数减益（≥30 层少召唤一只）、上限（Cap）、
        //    满上限随机替换、挂印记、广播召唤事件，所以这里<b>不需要</b>自己判任何东西。
        //    ⚠️ 不要写成 CreateCard<SandBeast> + AddGeneratedCardToCombat(..., PileType.Hand, ...)：
        //    那是"把召唤卡塞进手里让你自己打"，不是召唤。
        //    ⚠️ 数量用字面量 1：本卡升级<b>不</b>增强召唤（见 OnUpgrade），不需要 DynamicVar；
        //    「沙地兽」加 CardsVar("Summon") 是因为它要升级 1 → 2。
        //    ⚠️ 层数 >50 时它一只都不会召唤，但格挡/弹药/层数照常生效 —— 与
        //    SandBeast.cs:69 注释里用户 2026-10-03 确认过的口径一致。
        await BeastCmd.Summon(choiceContext, Owner, 1, cardSource: this);
    }

    protected override void OnUpgrade()
    {
        // 升级：层数 +2 → +3（弹药维持 +2，见设计稿），另加格挡 3 → 4（2026-10-04）。
        // ⚠️ 2026-10-10：新增的"召唤 1 只沙地兽"**不参与升级**（用户没说，按默认不增强）。
        DynamicVars["Depth"].UpgradeValueBy(1m);
        DynamicVars.Block.UpgradeValueBy(1m);
    }
}
