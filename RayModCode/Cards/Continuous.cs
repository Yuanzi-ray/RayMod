using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 稀有攻击「源源不断」：<b>层数大于 60 时才能打出</b>，造成相当于
/// <b>你遗物数量 × 7</b> 的伤害。升级后倍数 7 → 9。
///
/// <para>门槛走 <see cref="DepthGatedCard"/>（层数门槛的唯一通道，
/// 这样「探井人」的豁免对它自动生效）。</para>
///
/// <para>伤害用三元组显示实算值（<c>基准 0 + 7 × 遗物数</c>）——
/// 卡面数字会随你捡到的遗物实时上涨。遗物越多这张牌越厚，
/// 与本角色"层数越高越强、遗物也从层数来（>60 层战斗结束 50% 给遗物）"形成一条闭环。</para>
///
/// <para><b>★ 2026-10-09 新增（用户指定）：若斩杀敌人，本场战斗结束时获得一个遗物。</b>
/// 这里只置"待发放"标志（<c>DepthState.PendingKillRelicReward</c>），由
/// <c>MineRelic.TryModifyRewards</c> 在<b>战斗奖励界面</b>里补一个遗物奖励项 ——
/// 与「&gt;60 层遗物档」走同一条已验证的通路，理由见 <see cref="GrantKillRelic"/>。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Continuous : DepthGatedCard, IDepthCard
{
    private static readonly Logger Log = new("RayMod.Cards", LogType.Generic);

    private const int BaseEnergyCost = 2;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.AnyEnemy;
    private const bool ShowInCardLibrary = true;

    /// <summary>
    /// 可打出的层数门槛（严格大于 50）。★ <b>逻辑读这个常量</b>，卡面用同名 DynamicVar 显示。
    /// ⚠️ 原注释写"大于 60"，但常量一直是 50（卡面也已改成 50）—— 注释滞后，2026-10-07 修正。
    /// </summary>
    private const int RequiredDepth = 50;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    /// <summary>
    /// ★ 2026-10-07：<c>{RequiredDepth}</c> 喂卡面（原本"大于50"写死在文案里）。
    ///
    /// <para>⚠️ <b>逻辑仍读常量，不读 <c>DynamicVars</c></b>：基类
    /// <c>DepthGatedCard.IsPlayable =&gt; DepthGate.CanPlay(Owner, DepthConditionMet)</c> 会在
    /// <b>卡池构建阶段</b>求值，那时 <c>DynamicVars</c> 可能尚未填充 —— 读它会抛异常，
    /// 导致<b>整个卡牌池加载失败、卡牌全都不显示</b>（2026-10-07 实测踩过）。</para>
    /// </summary>
    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CalculationBaseVar(0m),
        new ExtraDamageVar(7m),
        new DynamicVar("RequiredDepth", RequiredDepth),
        new CalculatedDamageVar(ValueProp.Move)
            .WithMultiplier((card, _) => card.Owner?.Relics.Count ?? 0)
    ];

    public Continuous() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    /// <summary>层数大于 50 才满足条件（探井人的豁免由基类统一处理）。</summary>
    protected override bool DepthConditionMet => DepthCmd.IsDepthGreaterThan(Owner, RequiredDepth);

    protected override async Task OnPlayGated(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        var target = cardPlay.Target;

        // ★ 2026-10-09：「若斩杀敌人，本场战斗结束获得一个遗物」。
        //    判据 = 出牌前目标活着、这张牌的伤害结算完之后它死了。
        //    （单目标攻击，两次判定之间没有别人的动作，所以"死了"就是这张牌打死的。）
        bool wasAlive = target.IsAlive;

        await DamageCmd.Attack(DynamicVars.CalculatedDamage)
            .FromCard(this, cardPlay)
            .Targeting(target)
            .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
            .Execute(choiceContext);

        if (!wasAlive || target.IsAlive)
        {
            return;
        }

        // ★★ 2026-10-16 修的真 bug（玩家实机反馈）：**必须判"死的是不是敌人"**。
        //    卡面写的是「若斩杀**敌人**」，而原来只判"目标死了" ⇒ 打死**爪牙/宠物**
        //    （沙地兽这类 `IsPet` 单位、被召唤出来的小弟）也会白给一个遗物。
        //    判据用 `Creature.IsEnemy`（= `Side == CombatSide.Enemy`，反编译确认）。
        if (!target.IsEnemy)
        {
            Log.Info($"[源源不断] {target.Name} 不是敌人（IsEnemy=false）⇒ 不给遗物");
            return;
        }

        GrantKillRelic(target);
    }

    /// <summary>
    /// 记下"<b>本场战斗结束要给一个遗物</b>"。
    ///
    /// <para><b>为什么只置标志、不在这里直接发</b>：沿用「层数 &gt;60 战斗结束给遗物」那套已验证的做法
    /// （<c>DepthPower.AfterCombatEnd</c> 掷骰 → <c>DepthState.PendingRelicReward</c> 传递 →
    /// <c>MineRelic.TryModifyRewards</c> 在<b>战斗奖励界面</b>里加一个遗物奖励项）。
    /// 2026-10-04 用户实测过"直接 <c>RelicCmd.Obtain</c> 塞进遗物栏"的版本：只闪一下动画、
    /// 没有任何"我拿到了什么"的确认，玩家的第一反应是"遗物是坏的"。走奖励界面 = 可见、可跳过、有名字。</para>
    ///
    /// <para>标志放在 <c>DepthState</c>：它本来就是"<b>每场战斗一次</b>"的状态容器，
    /// <c>ResetForCombat</c> 会在下场战斗开始时清掉；本卡自己也读层数门槛，语义同源。</para>
    /// </summary>
    private void GrantKillRelic(Creature target)
    {
        try
        {
            var player = Owner;

            if (player == null)
            {
                Log.Warn("[源源不断] 斩杀：解析不出 Player，本次不给遗物");
                return;
            }

            var state = DepthRunData.Store.Get(player);
            state.PendingKillRelicReward = true;

            Log.Info($"[源源不断] 斩杀 {target.Name} → 本场战斗结束给一个遗物" +
                     "（已排队，会出现在战斗奖励界面里）");
        }
        catch (Exception e)
        {
            // 记账失败绝不能把出牌打断（同"音效/美术出问题不影响结算"的思路）。
            Log.Warn($"[源源不断] 斩杀记账失败：{e.GetType().Name}: {e.Message}");
        }
    }

    protected override void OnUpgrade()
    {
        // 升级：倍数 7 → 9（设计稿："造成的伤害提升至9倍"）。
        DynamicVars.ExtraDamage.UpgradeValueBy(2m);
    }
}
