using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Characters;
using RayMod.Depth;
using RayMod.Powers;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见技能「探照灯」：获得 4 点格挡；若<b>层数大于 19</b>，打完这张牌后
/// <b>下个回合不受层数负面效果影响</b>。升级后门槛 19 → 9。
///
/// <para>豁免的范围是<b>五条全免</b>（用户 2026-10-03 拍板 Q8）：10 层的沙地兽寿命、
/// 20 层的无兽掉血、30 层的少召唤、51 层的掉血 + 禁止召唤、101 层的连续死亡。
/// 完整清单在 <see cref="DepthPenalty"/> —— 以后新增层数负面效果时<b>必须同时在那里落判定</b>。</para>
///
/// <para>门槛用 <c>DynamicVar</c>，升级后卡面数字自动变成 9，不需要写散文化的升级说明。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Searchlight : ModCardTemplate, IDepthCard
{
    private const int BaseEnergyCost = 0;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    public override bool GainsBlock => true;

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new BlockVar(4m, ValueProp.Move),
        new DynamicVar("DepthThreshold", 19m)
    ];

    public Searchlight() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await CreatureCmd.GainBlock(Owner.Creature, DynamicVars.Block, cardPlay);

        if (!DepthCmd.IsDepthGreaterThan(Owner, DynamicVars["DepthThreshold"].IntValue))
        {
            return;
        }

        // Amount = 1 = 受保护的回合数；它会在打出的这一回合结束时"武装"，
        // 于是整个下一回合的层数负面效果全部豁免（见 SearchlightPower）。
        await PowerCmd.Apply<SearchlightPower>(
            choiceContext, Owner.Creature, 1m, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：门槛 19 → 9（设计稿："（>9）"）。
        DynamicVars["DepthThreshold"].UpgradeValueBy(-10m);
    }
}
