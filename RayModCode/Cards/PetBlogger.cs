using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Beasts;
using RayMod.Characters;
using RayMod.Powers;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 稀有能力「萌宠博主」：<b>召唤沙地兽时，改为给所有敌人各附加一层印记，
/// 并对所有有印记的敌人造成 4 点伤害</b>。升级后添加<b>固有</b>。
///
/// <para><b>用户 2026-10-04 拍板⑦：这是"替换"不是"追加"</b> ——
/// 基础规则"沙地兽入场后随机标记一个敌人"被整条换成"所有敌人各挂一层"。
/// 打出这张牌之后，召唤沙地兽就变成一发全体点名。</para>
///
/// <para>因为沙地兽印记是"每层 +10% 受到攻击伤害"，群体上印记的收益是<b>乘性放大</b>的
/// （多个敌人都变脆，而不是只脆一个）；再叠上每只兽入场就来的全体 4 点伤害，
/// 「跳舞的月光」「饲料」这类一次召唤一堆的牌会瞬间变成一张爆发牌。</para>
///
/// <para>⚠️ 它有<b>两个</b>改动点，分别在两处实现（别只改一半）：
/// 挂印记在 <c>BeastCmd.MarkOnSummon</c>，附带伤害在 <see cref="PetBloggerPower.OnBeastSummoned"/>。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class PetBlogger : ModCardTemplate, ISandBeastCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Power;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    /// <summary>伤害数值（占位符 <c>{HitDamage}</c>）。</summary>
    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DynamicVar("HitDamage", 4m)
    ];

    public PetBlogger() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<PetBloggerPower>(
            choiceContext, Owner.Creature, DynamicVars["HitDamage"].BaseValue, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：添加"固有"（设计稿："添加固有词条"）。数值不变。
        AddKeyword(CardKeyword.Innate);
    }
}
