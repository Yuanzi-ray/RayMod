using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Ammo;
using RayMod.Beasts;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见攻击「狙击」：<b>消耗所有弹药</b>，每消耗 1 层就对<b>随机敌人</b>造成 5 点伤害；
/// 若命中的敌人带有沙地兽印记，该次伤害 ×1.5（卡面写作"提高 50%" —— 见下）。
/// 升级后基础伤害 6。
///
/// <para>用户 2026-10-03 拍板：这个 1.5 倍是<b>叠乘</b>（不是替换印记的 +10%/层）。
/// 所以对 3 层印记的敌人是 <c>5 × 1.5 × 1.3</c> —— 我们只负责把 1.5 乘进
/// <c>Attack</c> 的入参，印记那一层由 <see cref="SandBeastMarkPower"/> 照常在引擎里乘。
/// 这也意味着<b>不需要</b>给印记 Power 加任何"本次静音"的旁路。</para>
///
/// <para>⚠️ <b>卡面口径</b>（2026-10-10 字面数字提升）：这个 1.5 倍走变量
/// <c>MarkedBonusPercent</c>，但卡面写的是"该次伤害提高 {MarkedBonusPercent}%"而不是
/// "变为 1.5 倍" —— <b>因为卡面渲染不了小数</b>（<c>:diff()</c> 与默认渲染都会把值强转 int，
/// 1.5 会显示成 1）。变量存卡面上那个整数（50），×1.5 的换算在 <see cref="OnPlay"/> 里做，
/// 这样"卡面显示什么、变量就存什么"才成立。</para>
///
/// <para>⚠️ 每段的伤害可能不同（取决于那一段随机到谁），所以<b>不能</b>用
/// <c>TargetingRandomOpponents</c> 一把交给引擎 —— 必须自己逐段抽目标、逐段算伤害。
/// 随机流用原版标准的 <c>RunState.Rng.CombatTargets</c>。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Snipe : ModCardTemplate, ISandBeastCard, IAmmoCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.RandomEnemy;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DamageVar(5m, ValueProp.Move),
        // ★ 命中带印记敌人时的额外伤害百分比（叠乘在印记本身的加成之上）。
        //   卡面「该次伤害提高{MarkedBonusPercent:diff()}%」显示的就是这个 50。
        //   ⚠️ 变量存的是**卡面上那个数**（50），代码里 /100m 换回 ×1.5
        //      —— 绝不允许出现"卡面 50、变量 0.5"这种不一致（同 ParanormalTape 的百分比口径）。
        //   ⚠️ 为什么不能写成 1.5m 的倍率变量再让卡面显示"1.5倍"：卡面渲染不了小数 ——
        //      {X:diff()} 走 HighlightDifferencesFormatter，它把值强转 int（1.5 → "1"）；
        //      不带格式化器时 DynamicVar.ToString() 也走 IntValue，同样丢小数。
        //      （本工程已有同类记录：ParanormalTape.cs 注释"避免出现 37.5 这种卡面没法显示的值"。）
        new DynamicVar("MarkedBonusPercent", 50)
    ];

    public Snipe() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // 先消耗：返回值 = 实际消耗量，也就是这一枪有几段。
        int shots = AmmoCmd.ConsumeAllAmmo(Owner);

        if (shots <= 0)
        {
            return;
        }

        for (int i = 0; i < shots; i++)
        {
            // 每段重新取一次敌人列表：上一段可能已经把某只打死了
            // （HittableEnemies 每次访问都是新列表，不会用旧快照打到尸体）。
            var enemies = CombatState?.HittableEnemies;

            if (enemies == null || enemies.Count == 0)
            {
                break;
            }

            var target = Owner.RunState.Rng.CombatTargets.NextItem(enemies);

            if (target == null)
            {
                break;
            }

            decimal damage = DynamicVars.Damage.BaseValue;

            if (target.HasPower<SandBeastMarkPower>())
            {
                // 叠乘（不是替换印记的 +10%/层）：1 + 50/100 = ×1.5。
                // 变量存卡面那个整数 50，这里换算成倍率 —— 单位换算只在代码里做。
                damage *= 1m + DynamicVars["MarkedBonusPercent"].IntValue / 100m;
            }

            await DamageCmd.Attack(damage)
                .FromCard(this, cardPlay)
                .Targeting(target)
                // 2026-10-20（第二轮）：命中特效<b>整段不设</b> —— 不是忘了写。
                // ① 原来借 vfx_attack_slash ⇒ 一把枪打出刀光；
                // ② 中间换过 vfx_starry_impact，录屏逐帧看下来<b>它的紫白爆闪会把我自己画的
                //    弹道/枪口火/命中环完全盖掉</b>（见 Art\CardFieldVfx.Snipe.cs 的文件注释）。
                // `WithHitFx(string? vfx = null, ...)` 的参全是可空的，
                // 不调用它就等于 HitVfx/HitSfx 保持 null ⇒ 原版不再额外播任何命中特效，
                // 这一段演出完全由 RayMod 接管。
                // ⚠️ 只改表现，伤害/目标/顺序一个字没动。
                .Execute(choiceContext);
        }
    }

    protected override void OnUpgrade()
    {
        // 升级：基础伤害 5 → 6（设计稿）。
        DynamicVars.Damage.UpgradeValueBy(1m);
    }
}
