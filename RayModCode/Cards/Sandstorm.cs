using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Beasts;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见技能「沙尘暴」：获得 <b>15</b> 点格挡，<b>沙地兽上限 -1</b>；<b>只有上限确实被削减时</b>才获得 2 点能量。升级后格挡 <b>18</b>。
///
/// <para>降上限走 <c>BeastCmd.ModifyCap(负数)</c> —— 用户规则 #7 已经实现：
/// <b>降上限导致场上数量超出时，会随机移除多出来的沙地兽，它们的印记一并消失</b>（<c>BeastCmd.cs:277</c> 附近）。</para>
///
/// <para>上限被夹在 [0, 6]，所以最多降到 0（此时 <c>BeastCmd.Summon</c> 会一只都召不出来）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Sandstorm : ModCardTemplate, ISandBeastCard
{
    private static readonly Logger Log = new("RayMod.Cards", LogType.Generic);

    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    public override bool GainsBlock => true;

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        // ★ 2026-10-17 平衡：20 → 15（升级仍 +3 ⇒ 18）
        new BlockVar(15m, ValueProp.Move),
        new EnergyVar(2),
        // 上限削减量走变量（原先是写在 OnPlay 里的字面 -1 → 卡面只能写死"上限-1"，改不动）。
        // 存**正数** 1：卡面显示的"1"就是这个值，减号留在文案里（见作业说明 §4.3）。
        new DynamicVar("CapLoss", 1)
    ];

    public Sandstorm() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await CreatureCmd.GainBlock(Owner.Creature, DynamicVars.Block, cardPlay);

        // ★ 2026-10-17 平衡（用户口径："无沙地兽上限时，不回复费用"）：
        //   先记削前上限 → 削减 → 再读一次；只有**确实削下去了**才回费。
        //   上限已经是 0 时 ModifyCap 削不动，此时不回费。
        //   ⚠️ 判据用"前后各读一次并比较"，不去抄 ModifyCap 内部的夹取逻辑
        //      —— 同一件事的判据只能有一处（铁律 50）。
        int capBefore = BeastCmd.GetCap(Owner);

        await BeastCmd.ModifyCap(choiceContext, Owner, -DynamicVars["CapLoss"].IntValue);

        int capAfter = BeastCmd.GetCap(Owner);

        if (capAfter < capBefore)
        {
            await PlayerCmd.GainEnergy(DynamicVars.Energy.IntValue, Owner);
            return;
        }

        Log.Info($"[沙尘暴] 沙地兽上限已是 {capBefore}，削减无效 ⇒ 不回复 {DynamicVars.Energy.IntValue} 点能量");
    }

    protected override void OnUpgrade()
    {
        // 升级：格挡 15 → 18（用户口径："升级后防18"）。上限与能量不变。
        DynamicVars.Block.UpgradeValueBy(3m);
    }
}
