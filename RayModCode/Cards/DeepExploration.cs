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
/// 罕见攻击「深层探索」：造成 6 点伤害，<b>额外造成相当于当前层数的伤害</b>。
/// 升级后添加<b>保留</b>词条。
///
/// <para><b>卡面显示"实算伤害"的做法</b>（2026-10-03 用户提出"看不到实际能打多少"）：
/// 用原版的三元组，公式是 <c>CalculationBaseVar + ExtraDamageVar × 乘数</c>
/// （<c>CalculatedVar.Calculate</c>，<c>src\Core\Localization\DynamicVars\CalculatedVar.cs:71</c>）：</para>
/// <list type="bullet">
/// <item><c>CalculationBaseVar(6)</c> —— 基准 6 点</item>
/// <item><c>ExtraDamageVar(1)</c> —— 每 1 点层数加 1 点</item>
/// <item><c>CalculatedDamageVar</c> 的乘数 = <b>当前层数</b> ⇒ 总数 = 6 + 层数</item>
/// </list>
/// <para>所以卡面上的 <c>{CalculatedDamage}</c> 显示的就是<b>真正会打出的数字</b>；
/// 战斗外（图鉴预览）乘数取 0（<c>Calculate</c> 里判了 <c>CombatManager.Instance.IsInProgress</c>），
/// 显示基准值 6，这也正是原版「完美打击」「全身撞击」的行为。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class DeepExploration : ModCardTemplate, IDepthCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.AnyEnemy;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CalculationBaseVar(6m),
        new ExtraDamageVar(1m),
        new CalculatedDamageVar(ValueProp.Move)
            .WithMultiplier((card, _) => DepthCmd.GetDepth(card.Owner))
    ];

    public DeepExploration() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        // 走 CalculatedDamage 重载：伤害值由上面那套三元组实时算出，和卡面显示完全一致。
        await DamageCmd.Attack(DynamicVars.CalculatedDamage)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
            .Execute(choiceContext);
    }

    protected override void OnUpgrade()
    {
        // 升级：加「保留」词条（数值不变）。
        AddKeyword(CardKeyword.Retain);
    }
}
