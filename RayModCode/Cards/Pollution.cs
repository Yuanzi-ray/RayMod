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
/// 罕见技能「污染」：<b>0 费</b>，获得 10 点格挡，并把<b>抽牌堆里随机一张牌变成「污染」</b>。
/// 升级后格挡 10 → 12。
///
/// <para>用户 2026-10-02 定义。这张牌<b>自我复制</b>：每次打出都会往抽牌堆里多种一张自己
/// （<c>Transform</c> 是"替换"不是"添加"，所以牌组总数不变，不会无限膨胀）。</para>
///
/// <para><b>两处刻意的取舍（都不在卡面上写，但会影响手感）：</b></para>
/// <list type="number">
/// <item><b>候选包括已经是「污染」的牌</b>（用户 2026-10-03 明确："可以随机到污染啊"）。
/// 所以随到「污染」是一次合法结果 —— 它会"重种"一张，牌面数值不变。</item>
/// <item>替换牌的升级状态<b>跟着"打出的这张「污染」"走</b>（用户 2026-10-03 拍板：
/// "取决于打出的那张污染是否升级。如果没有，那么变的也没有，反之亦然"）——
/// 打未升级的 → 种出来的也是未升级；打升级的 → 种出来的也是升级版。
/// 这正是原版 <c>Begone</c> / <c>Charge</c> 的写法（判的是<b>施放者自己</b>是否升级，
/// 而不是被变换的那张牌）。</item>
/// <item>⚠️ 由"可以随机到「污染」"+"上一条"推出的一个边缘后果：牌组里同时有<b>已升级</b>和
/// <b>未升级</b>两张「污染」时，打未升级的那张、又正好随机到已升级的那张，
/// 会把它的升级洗掉。这是规则的直接推论、不是 bug；要堵它就得改成"按被替换者保留升级"。</item>
/// </list>
///
/// <para><b>实现要点</b>：随机流用 <c>RunState.Rng.CombatCardSelection</c>（"随机选牌"的语义流，
/// 别用 <c>CombatTargets</c>）。牌堆读写走 <c>PileType.Draw.GetPile(Owner).Cards</c>
/// —— ⚠️ 这个扩展方法<b>出战斗会抛异常</b>，所以只在 <c>OnPlay</c> 里用。</para>
///
/// <para>替换牌的构造走 <c>CombatState.CreateCard&lt;T&gt;</c>（本工程已验证过它能造 mod 卡牌：
/// 「沙地兽巢穴」「为什么这么多！」同款），而不是 <c>CardCmd.TransformTo&lt;T&gt;</c> 内部那条
/// <c>CardScope.CreateCard</c> 路径。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Pollution : ModCardTemplate
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
        new BlockVar(10m, ValueProp.Move)
    ];

    public Pollution() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await CreatureCmd.GainBlock(Owner.Creature, DynamicVars.Block, cardPlay);

        var combatState = CombatState;

        if (combatState == null)
        {
            return;
        }

        // 候选 = 抽牌堆里所有**可变换**的牌（包括已经是「污染」的 —— 用户 2026-10-03 明确）。
        // 唯一的过滤是 IsTransformable：永恒牌不可变换，对它调 Transform 会抛异常
        //（原版 Begone 没防这个，这里补上）。
        var candidates = PileType.Draw.GetPile(Owner).Cards
            .Where(card => card.IsTransformable)
            .ToList();

        if (candidates.Count == 0)
        {
            // 抽牌堆空 / 全是不可变换的牌 —— 不是错误，只是这次没有可污染的牌。
            return;
        }

        var victim = Owner.RunState.Rng.CombatCardSelection.NextItem(candidates);

        if (victim == null)
        {
            return;
        }

        var replacement = combatState.CreateCard<Pollution>(Owner);

        // 替换牌的升级状态跟着"打出的这张"走（用户 2026-10-03 拍板）：
        //   打未升级的 -> 种出来也是未升级；打升级的 -> 种出来也是升级版。
        // 照抄原版 Begone / Charge —— 它们判的也是**施放者自己**是否升级，
        // 而不是被变换的那张牌（Transform 本身不保留升级状态，所以必须手动补）。
        if (IsUpgraded)
        {
            // CardCmd.Upgrade 是同步 void，别 await。
            CardCmd.Upgrade(replacement);
        }

        await CardCmd.Transform(victim, replacement);
    }

    protected override void OnUpgrade()
    {
        // 升级：格挡 10 → 12（用户 2026-10-02 定义）。
        DynamicVars.Block.UpgradeValueBy(2m);
    }
}
