using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 普通卡「枪托打击」：造成 22 点伤害。<b>每场战斗只能使用两次</b>。升级后 28 点伤害。
///
/// <para>「每场战斗只能用 N 次」的机制照抄原版 <c>UpMySleeve</c>
///（<c>src\Core\Models\Cards\UpMySleeve.cs</c>）：卡实例上挂一个私有计数器，
/// 用 <see cref="IsPlayable"/> 拦住超出次数的打出。</para>
///
/// <para><b>⚠️ 比原版多了一步</b>：原版那张牌一个战斗里只用几次就进弃牌堆、
/// 不重置也没人发现；但这张牌若计数器不重置，打两次之后<b>整局都用不了</b>。
/// 所以这里额外覆写 <see cref="BeforeCombatStart"/> 把计数清零
///（原版 <c>UpMySleeve</c> 没写这步，别照抄漏了）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class ButtStrike : ModCardTemplate
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Common;
    private const TargetType CardTarget = TargetType.AnyEnemy;
    private const bool ShowInCardLibrary = true;

    /// <summary>每场战斗允许打出的次数（设计稿）。</summary>
    private const int MaxPlaysPerCombat = 2;

    /// <summary>本场战斗已打出的次数（卡实例级字段）。</summary>
    private int _timesPlayedThisCombat;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DamageVar(22m, ValueProp.Move),
        new CardsVar("MaxPlays", MaxPlaysPerCombat)
    ];

    /// <summary>用满次数后这张牌就不可打出（原版 <c>IsPlayable</c> 钩子）。</summary>
    protected override bool IsPlayable => _timesPlayedThisCombat < MaxPlaysPerCombat;

    public ButtStrike() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    /// <summary>每场战斗开始清零，否则次数会跨战斗累积。</summary>
    public override Task BeforeCombatStart()
    {
        _timesPlayedThisCombat = 0;
        return Task.CompletedTask;
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        await DamageCmd.Attack(DynamicVars.Damage.BaseValue)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_attack_blunt", null, "heavy_blunt_attack.mp3")
            .Execute(choiceContext);

        _timesPlayedThisCombat++;
    }

    protected override void OnUpgrade()
    {
        // 升级：22 → 28（次数维持 2 次）。
        DynamicVars.Damage.UpgradeValueBy(6m);
    }
}
