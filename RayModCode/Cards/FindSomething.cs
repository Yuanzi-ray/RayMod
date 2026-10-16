using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 普通卡「有所发现」：抽 1 张牌，层数 +3，造成 5 点伤害。升级后抽牌数提升至 2。
///
/// <para><b>为什么第一张正式卡选它：</b></para>
/// <list type="bullet">
/// <item>它是<b>唯一一张</b>同时满足"只用层数系统、不碰弹药、不碰沙地兽、不碰提丰印记"
/// 的普通卡 —— 后面三个系统都还没实现，选别的卡会把没做完的依赖拖进来。</item>
/// <item>稀有度是 <c>Common</c>，正好补上 <c>CardFactory.CreateForReward</c> 需要的
/// 非基本牌：卡池里原本只有两张 <c>Basic</c>，奖励生成会抛
/// <c>InvalidOperationException</c>（详见交接文档三-B）。</item>
/// <item>层数 +3 让"积累层数"这条线在实战里更快推进，方便验证 20/30/50/65 各档。</item>
/// </list>
///
/// <para>参考原版同构实现：<c>src\Core\Models\Cards\PommelStrike.cs</c>
///（打击 + 抽牌，动态变量与本地化占位符用法完全一致）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class FindSomething : ModCardTemplate, IDepthCard
{
    /// <summary>基础耗能。</summary>
    private const int BaseEnergyCost = 1;

    /// <summary>卡牌类型。</summary>
    private const CardType CardKind = CardType.Attack;

    /// <summary>
    /// 稀有度必须是 <c>Common</c>。
    /// 这**不是**单纯照抄设计稿：<c>CardFactory.CreateForReward</c>
    /// 会把 <c>CardRarity.Basic</c> 全部滤掉（src\Core\Factories\CardFactory.cs:221），
    /// 卡池里一张非基本牌都没有时，战斗结束的选牌奖励会直接抛异常。
    /// </summary>
    private const CardRarity CardRarityValue = CardRarity.Common;

    /// <summary>目标类型（任意敌人）。</summary>
    private const TargetType CardTarget = TargetType.AnyEnemy;

    /// <summary>是否在卡牌图鉴中显示。</summary>
    private const bool ShowInCardLibrary = true;

    /// <summary>
    /// 占位卡图：复用打击的图。
    /// 等有正式美术时，把 FindSomething.png 放进
    /// <c>RayMod\images\cards\</c> 并在 Godot 里正常导入一次，这里不用改
    /// （<see cref="AssetProfile"/> 已按类名拼路径）。
    /// </summary>
    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    /// <summary>
    /// 动态变量：伤害与抽牌数。
    /// <c>DamageVar</c> 绑 <c>{Damage}</c>，<c>CardsVar</c> 绑 <c>{Cards}</c>
    /// （见 <c>CardsVar.defaultName</c>），两者都支持 <c>:diff()</c> 显示升级差值。
    /// </summary>
    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DamageVar(5m, ValueProp.Move),
        new CardsVar(1),
        // 层数增量走变量（原先是写在 OnPlay 里的字面 3 → 卡面只能写死"层数+3"，改不动）
        new DynamicVar("DepthGain", 3)
    ];

    public FindSomething() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        // 1) 先抽牌：设计稿的顺序是"抽1张牌，层数+3，造成5点伤害"。
        await CardPileCmd.Draw(choiceContext, DynamicVars.Cards.BaseValue, Owner);

        // 2) 再加层数。走 DepthCmd（唯一入口），不要在卡里直接碰 DepthPower：
        //    真实层数存在跑团级存档里，DepthCmd 会负责同步战斗内的显示与结算。
        await DepthCmd.AddDepth(choiceContext, Owner, DynamicVars["DepthGain"].IntValue, cardSource: this);

        // 3) 最后结算伤害。
        await DamageCmd.Attack(DynamicVars.Damage.BaseValue)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            // WithHitFx(string? vfx, string? sfx, string? tmpSfx) —— 与原版 PommelStrike 同参。
            .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
            .Execute(choiceContext);
    }

    protected override void OnUpgrade()
    {
        // 升级：抽牌数 1 → 2（伤害维持 5，按设计稿）。
        DynamicVars.Cards.UpgradeValueBy(1m);
    }
}
