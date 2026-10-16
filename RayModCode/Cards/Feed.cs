using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Beasts;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见技能「饲料」：召唤 3 只沙地兽；<b>若本回合发生过"满上限替换"，再召唤 1 只</b>。
/// 升级后"再召唤"数量 1 → 2。
///
/// <para>计数读 <c>BeastState.ReplacedThisTurn</c>（该字段的注释里就写着这张牌），
/// 它由 <c>BeastCmd.Summon</c> 在发生替换时置位。</para>
///
/// <para>⚠️ <b>读取时机是"打出前"</b>：先读标志、再召唤。若放到召唤之后读，
/// 这张牌自己召唤的 3 只（在满上限时必然触发替换）会把标志置上，
/// 等于"满上限时无条件多给 1 只"，那就不是"存在替换才给"了。
/// 若设计意图其实是后者，把这次读取挪到两次 Summon 之间即可。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Feed : ModCardTemplate, ISandBeastCard
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
        new CardsVar("Count", 3),
        new CardsVar("Extra", 1)
    ];

    public Feed() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        bool replaced = BeastCmd.TryGetState(Owner)?.ReplacedThisTurn ?? false;

        await BeastCmd.Summon(choiceContext, Owner, DynamicVars["Count"].IntValue, this);

        if (replaced)
        {
            await BeastCmd.Summon(choiceContext, Owner, DynamicVars["Extra"].IntValue, this);
        }
    }

    protected override void OnUpgrade()
    {
        // 升级：再召唤 1 只 → 2 只（设计稿："再召唤数量提升至2"）。
        DynamicVars["Extra"].UpgradeValueBy(1m);
    }
}
