using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

// ⚠️ 必须起别名：卡牌「沙地兽」和召唤物「沙地兽」同名（RayMod.Cards.SandBeast /
//    RayMod.Beasts.SandBeast）。本文件要造的是**卡牌**那一张。
using SandBeastCard = RayMod.Cards.SandBeast;

using RayMod.Beasts;

namespace RayMod.Cards;

/// <summary>
/// 罕见技能「为什么这么多！」：选择 2 张手牌，将其<b>变化</b>为「沙地兽」。
/// 升级后变化出来的是<b>升级版</b>沙地兽。
///
/// <para>照抄原版 <c>Begone</c>（选 1 张手牌变化）：<c>CombatState.CreateCard&lt;T&gt;</c> 造出
/// 替换牌 →（升级时）<c>CardCmd.Upgrade</c> → <c>CardCmd.Transform(原牌, 替换牌)</c>。</para>
///
/// <para>⚠️ 三个坑：</para>
/// <list type="number">
/// <item><b>变换不保留升级状态</b> —— 替换牌是新克隆，得自己调 <c>Upgrade</c>。</item>
/// <item>选中但 <c>Pile == null</c> 的牌不能变换（原版会抛
/// <c>InvalidOperationException</c>），所以动手前逐个再验一次。</item>
/// <item>用 filter 排除 <c>IsTransformable == false</c> 的牌（原版 <c>Begone</c> 没排除，
/// 遇到永恒牌会抛异常；这里加上更安全）。</item>
/// </list>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class WhySoMany : ModCardTemplate, ISandBeastCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CardsVar("Count", 2)
    ];

    public WhySoMany() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        var combatState = CombatState;

        if (combatState == null)
        {
            return;
        }

        // 用原版自带的"变化"选牌提示词，省一个本地化键（Begone / Charge 同款）。
        var selected = await CardSelectCmd.FromHand(
            prefs: new CardSelectorPrefs(
                CardSelectorPrefs.TransformSelectionPrompt, DynamicVars["Count"].IntValue),
            context: choiceContext,
            player: Owner,
            filter: (CardModel card) => card.IsTransformable,
            source: this);

        // 先快照再动手：变换会把原牌从状态里摘掉，边遍历边改不安全。
        foreach (var card in selected.ToList())
        {
            if (card.Pile == null)
            {
                continue;
            }

            var beast = combatState.CreateCard<SandBeastCard>(Owner);

            if (IsUpgraded)
            {
                // CardCmd.Upgrade 是同步 void，别 await。
                CardCmd.Upgrade(beast);
            }

            await CardCmd.Transform(card, beast);
        }
    }

    protected override void OnUpgrade()
    {
        // 升级：变化出来的是**升级版**沙地兽（设计稿）。张数不变。
    }
}
