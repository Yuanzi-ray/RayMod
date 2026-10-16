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
/// 罕见技能「黑暗之下」：让目标敌人<b>失去 5 点力量</b>；层数大于 19 时，<b>额外</b>再失去 3 点。
/// 升级后额外值 3 → 4。
///
/// <para>"失去力量"就是给 <c>StrengthPower</c> 传<b>负数</b>（原版 <c>Malaise.cs:40</c>、
/// <c>SharedFate.cs:39</c> 都是这个写法），不需要自定义 Power。</para>
///
/// <para>⚠️ <c>PowerCmd.Apply</c> 可能返回 <c>null</c>（战斗结束 / 被 Artifact 之类挡下 /
/// 结算后为 0），所以**不要**假设它一定生效，后续逻辑别依赖返回值。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Darkness : ModCardTemplate, IDepthCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.AnyEnemy;
    private const bool ShowInCardLibrary = true;


    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DynamicVar("DepthThreshold", 19),
        // ⚠️ 别用 PowerVar<StrengthPower>：它的名字取的是 typeof(T).Name = "StrengthPower"，
        //    而文案里的占位符是 {StrengthLoss} —— 名字对不上时**整条描述都会原样显示**（占位符不替换）。
        //    原版降力量类卡（DarkShackles / PiercingWail / SharedFate）一律用这种
        //    "朴素 DynamicVar + 自定义名" 的写法。
        new DynamicVar("StrengthLoss", 5m),
        new DynamicVar("ExtraStrengthLoss", 3m)
    ];

    public Darkness() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        decimal strengthLoss = DynamicVars["StrengthLoss"].BaseValue;

        if (DepthCmd.IsDepthGreaterThan(Owner, 19))
        {
            strengthLoss += DynamicVars["ExtraStrengthLoss"].IntValue;
        }

        // ⚠️ 传**正数**：DarknessPower 继承 TemporaryStrengthPower，负号由它的 IsPositive=false 决定。
        //    直接给 StrengthPower 传负数会变成"永久降力量"，那是错的（用户 2026-10-03 纠正：
        //    这张牌应该是**本回合**失去力量，回合结束自动还回去）。
        await PowerCmd.Apply<DarknessPower>(
            choiceContext, cardPlay.Target, strengthLoss, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：额外失去 3 → 4（设计稿："改为额外失去4点力量"）。
        DynamicVars["ExtraStrengthLoss"].UpgradeValueBy(1m);
    }
}
