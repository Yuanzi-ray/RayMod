using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Ammo;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见技能「储备」：获得 8 点格挡；获得相当于<b>当前层数一半</b>的弹药，
/// <b>被弹药上限夹掉的部分转化为层数</b>。<b>消耗</b>，升级后去掉消耗。
///
/// <para>"溢出转层数"靠 <see cref="AmmoCmd.AddAmmo"/> 的<b>返回值</b>实现：
/// 它返回的是实际增加量，所以 <c>溢出 = 请求量 − 实际增加量</c>。
/// 用户拍板：层数一半<b>向下取整</b>（整数除法天然如此）。</para>
///
/// <para>这条设计让"层数换弹药"在弹药已满时不会白给 —— 弹药顶到上限时，
/// 牌会变成"8 格挡 + 一大堆层数"，而层数上涨又意味着走向更深的减益档位，
/// 是这张牌真正的代价。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Reserve : ModCardTemplate, IAmmoCard, IDepthCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    /// <summary>层数折算成弹药的分母（"层数一半"）。</summary>
    private const int DepthPerAmmo = 2;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    public override bool GainsBlock => true;

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new BlockVar(8m, ValueProp.Move)
    ];

    /// <summary>消耗词条（设计稿明确写了；升级后去掉）。</summary>
    public override IEnumerable<CardKeyword> CanonicalKeywords =>
    [
        CardKeyword.Exhaust
    ];

    public Reserve() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await CreatureCmd.GainBlock(Owner.Creature, DynamicVars.Block, cardPlay);

        int request = DepthCmd.GetDepth(Owner) / DepthPerAmmo;

        if (request <= 0)
        {
            return;
        }

        int gained = AmmoCmd.AddAmmo(Owner, request);
        int overflow = request - gained;

        if (overflow > 0)
        {
            await DepthCmd.AddDepth(choiceContext, Owner, overflow, cardSource: this);
        }
    }

    protected override void OnUpgrade()
    {
        // 升级：去掉"消耗"（设计稿："去消耗"）。数值不动。
        RemoveKeyword(CardKeyword.Exhaust);
    }
}
