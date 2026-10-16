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
/// 稀有技能「跳舞的月光」：召唤<b>等同于本场战斗你召唤过沙地兽数量</b>的沙地兽。<b>消耗</b>。
/// 升级后去掉消耗。
///
/// <para>计数读 <c>BeastState.SummonedThisCombat</c>（含已被替换/退场的），
/// 与「沙地兽狂潮」「沙地兽之盾」同一个来源。</para>
///
/// <para>⚠️ 在<b>召唤之前</b>读取计数：<see cref="BeastCmd.Summon"/> 会在内部累加这个值，
/// 读晚了就会把自己也算进去（自我放大）。</para>
///
/// <para>实战形态：前期打几张召唤牌 → 这张牌一口气把场面铺满（受沙地兽上限与
/// "30 层起少召唤一只 / 51 层起完全不能召唤"两条层数减益约束，见 <see cref="BeastCmd.Summon"/>）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class DancingMoonlight : ModCardTemplate, ISandBeastCard
{
    private const int BaseEnergyCost = 2;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    /// <summary>
    /// ★ 2026-10-07（用户要求）：卡面显示**实得的召唤只数**。
    ///
    /// <para>乘数 lambda 只读 <see cref="BeastState.SummonedThisCombat"/>，不碰 <c>DynamicVars</c>
    /// —— 避免"初始化期读 DynamicVars 导致卡牌全不显示"的坑。取值与 <see cref="OnPlay"/> 一致。</para>
    /// </summary>
    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CalculationBaseVar(0m),
        new CalculationExtraVar(1m),
        new CalculatedBlockVar(ValueProp.Move)
            .WithMultiplier((card, _) => BeastCmd.TryGetState(card.Owner)?.SummonedThisCombat ?? 0)
    ];

    /// <summary>消耗词条（设计稿明确写了；升级后去掉）。</summary>
    public override IEnumerable<CardKeyword> CanonicalKeywords =>
    [
        CardKeyword.Exhaust
    ];

    public DancingMoonlight() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        int summoned = BeastCmd.TryGetState(Owner)?.SummonedThisCombat ?? 0;

        if (summoned <= 0)
        {
            return;
        }

        await BeastCmd.Summon(choiceContext, Owner, summoned, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：去掉"消耗"（设计稿："去除消耗词条"）。
        RemoveKeyword(CardKeyword.Exhaust);
    }
}
