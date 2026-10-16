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
/// 罕见能力「沙地兽的祝福」：<b>每召唤 4 只沙地兽，获得 1 点能量</b>。升级后添加<b>固有</b>。
///
/// <para>计数从<b>打出这张牌那一刻</b>开始（用户 2026-10-03 拍板 Q10），
/// 否则一打出就会按"本场已经召唤过多少只"倒推、白送能量。逻辑在
/// <see cref="SandBeastBlessPower"/>。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class SandBeastBless : ModCardTemplate, ISandBeastCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Power;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new EnergyVar(1),
        // 卡面「每召唤4只」的那个 4：**生效真值**在共享文件里 ——
        // Powers\SandBeastBlessPower.cs 的 `public const int SummonsPerTrigger = 4;`。
        // 卡文件没地方把这个值传过去：卡里传给 Power 的 Amount 是"每次给多少能量"，
        // 没有第二通道传"每几只触发一次"。
        // 这里按本地化门禁要求（占位符名字必须能在本卡自己的 .cs 里找到同名符号）
        // 声明一份同名字面量；⚠️ 与 Power 里那个 const 必须同步改。
        new DynamicVar("SummonsPerTrigger", 4)
    ];

    public SandBeastBless() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<SandBeastBlessPower>(
            choiceContext, Owner.Creature, DynamicVars.Energy.BaseValue, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：添加"固有"（设计稿："加固有"）。
        AddKeyword(CardKeyword.Innate);
    }
}
