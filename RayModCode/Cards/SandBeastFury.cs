using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Characters;
using RayMod.Powers;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

using RayMod.Beasts;

namespace RayMod.Cards;

/// <summary>
/// 罕见技能「沙地兽之怒」：<b>本回合每召唤一只沙地兽，获得 1 点力量</b>（回合结束还回去）。
/// 升级后添加<b>保留</b>。
///
/// <para>用户 2026-10-03 拍板：<b>只算打出这张牌之后</b>的召唤，打出之前的召唤不算 ——
/// 所以打出瞬间一点力量都不给。这条口径直接决定了 Power 的实现方式，
/// 见 <see cref="SandBeastFuryPower"/> 的注释（不能用原版 <c>TemporaryStrengthPower</c> 基类）。</para>
///
/// <para>配合「饲料」「去咧嘴谷」「沙地兽」这类一次召唤多只的牌，可以当回合叠出一大截力量，
/// 然后用「伏击」这种多段攻击兑现。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class SandBeastFury : ModCardTemplate, ISandBeastCard
{
    private const int BaseEnergyCost = 0;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CardsVar("Strength", 1)
    ];

    public SandBeastFury() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // Amount 只是"让 Power 存在 + 角标基数"，真正的力量在 Power 内部按召唤数累加。
        await PowerCmd.Apply<SandBeastFuryPower>(
            choiceContext, Owner.Creature, DynamicVars["Strength"].BaseValue, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：添加"保留"（设计稿："加保留"）。
        AddKeyword(CardKeyword.Retain);
    }
}
