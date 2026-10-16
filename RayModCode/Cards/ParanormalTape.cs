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
/// 稀有攻击「灵异录像带」：<b>在 50~100 之间取一个随机数变成你的当前层数</b>，
/// 然后<b>获得新层数 50% 的格挡</b>、<b>造成新层数 40% 的伤害</b>。
/// 升级后费用 2 → 1。
///
/// <para><b>用户 2026-10-04 拍板①</b>："50%格挡 / 40%伤害"是<b>新层数</b>的百分比 ——
/// 也就是先掷层数，再用那个新值算格挡与伤害（不是旧层数）。</para>
///
/// <para><b>它是一张赌牌</b>：掷到 50 就是 25 格挡 + 20 伤害（2 费偏亏），
/// 掷到 100 就是 50 格挡 + 40 伤害（2 费超模）。但它同时会把层数推到 50~100 ——
/// 也就是<b>必然踩进</b>"51 层起不能召唤沙地兽 + 每回合 -5HP"，
/// 掷到 101 以上的死亡倒计时则不会碰到（上限正好 100）。
/// 这是设计意图：用一层楼的高度换一波攻防。</para>
///
/// <para><b>⚠️ 卡面刻意不写数字（不写 <c>{CalculatedDamage}</c>）</b>：
/// 伤害/格挡的基数来自"打完才知道的随机层数"，写任何实算值都必然误导
/// （写旧层数的 40% = 玩家以为只有 8 点，实际是 30 点）。
/// 所以文案直接写清百分比，让玩家自己按"新层数是 50~100"心算区间 ——
/// 这是<b>有意偏离"动态数值用三元组"规范</b>的一处，理由就是不实算的随机基数。
/// 掷出的层数本身会立刻显示在层数角标上。</para>
///
/// <para>随机流用 <c>RunState.Rng.Niche</c>：原版注释里它正是"不参与其他系统交互的
/// 一次性随机"，用别的流（CombatTargets / CombatCardGeneration）会污染随机序列。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class ParanormalTape : ModCardTemplate, IDepthCard
{
    private const int BaseEnergyCost = 2;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.AnyEnemy;
    private const bool ShowInCardLibrary = true;

    // 掷层数的上下界与格挡/伤害百分比全部走动态变量（原先写死成 const →
    // 卡面只能写死"50到100""50%""40%"，网页工具改不动、文案却能随便改）。
    // ⚠️ 百分比变量存的是**卡面上那个整数**（50 / 40），代码里 /100m 换算成比例 ——
    //    绝不允许出现"卡面 50、变量 0.5"这种不一致。

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    /// <summary>加格挡的牌必须写这个（否则「全力以赴」那类格挡修正会静默失效）。</summary>
    public override bool GainsBlock => true;

    /// <summary>
    /// 掷层数区间与格挡/伤害百分比。
    ///
    /// <para>⚠️ 仍然<b>不</b>写 <c>{CalculatedDamage}</c>（理由见类注释）：伤害/格挡的基数来自
    /// "打完才知道的随机层数"，写任何实算值都必然误导（写旧层数的 40% = 玩家以为只有 8 点，实际是 30 点）。
    /// 这四个变量是<b>输入</b>（区间与比例），<b>不是</b>那个随机结果。</para>
    /// </summary>
    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DynamicVar("MinRollDepth", 50),
        new DynamicVar("MaxRollDepth", 100),
        new DynamicVar("BlockPercent", 50),
        new DynamicVar("DamagePercent", 40)
    ];

    // 2026-10-10 用户要求去掉「消耗」→ 本卡不再声明 CardKeyword.Exhaust。
    // 原先此处有一个只为 Exhaust 而存在的 CanonicalKeywords override，现已整个删除：
    // CardModel.CanonicalKeywords 是 virtual 且默认返回 Array.Empty<CardKeyword>()，
    // 删掉 override 即回落到「无词条」，比留一个返回空集合的空 override 更干净。

    public ParanormalTape() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        // 1) 掷一个新层数并写进去（"设为"语义走 SetDepth）。区间走变量。
        int minRoll = DynamicVars["MinRollDepth"].IntValue;
        int maxRoll = DynamicVars["MaxRollDepth"].IntValue;

        int rolled = Owner.RunState.Rng.Niche.NextInt(minRoll, maxRoll + 1);

        await DepthCmd.SetDepth(choiceContext, Owner, rolled, cardSource: this);

        // 2) 按**新层数**结算格挡与伤害（用户拍板①）。向下取整：
        //    奇数层时 50%/40% 会带小数，取整避免出现 37.5 这种卡面没法显示的值。
        //    百分比变量存整数（50 / 40），这里 /100m 换算成比例。
        int block = (int)Math.Floor(rolled * DynamicVars["BlockPercent"].IntValue / 100m);
        int damage = (int)Math.Floor(rolled * DynamicVars["DamagePercent"].IntValue / 100m);

        await CreatureCmd.GainBlock(Owner.Creature, block, ValueProp.Move, cardPlay);

        await DamageCmd.Attack((decimal)damage)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
            .Execute(choiceContext);
    }

    protected override void OnUpgrade()
    {
        // 升级：费用 2 → 1（设计稿："费用减1"）。
        EnergyCost.UpgradeBy(-1);
    }
}
