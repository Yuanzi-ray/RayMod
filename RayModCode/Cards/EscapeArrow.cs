using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Ammo;
using RayMod.Characters;
using RayMod.Combat;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;
using LogType = MegaCrit.Sts2.Core.Logging.LogType;

namespace RayMod.Cards;

/// <summary>
/// 稀有攻击「脱身矢」：造成<b>上一张牌伤害的 4 倍</b>的伤害，[blue]弹药[/blue]+2。升级后弹药 +3。
///
/// <para><b>用户 2026-10-04 拍板</b>：技能算 <b>0</b>、多段算<b>总和</b>、
/// 并且"上一张"是<b>本场战斗内最后打出的那张牌，跨回合保留</b> ——
/// 所以可以留着这张牌，等一个大伤害回合之后再打。</para>
///
/// <para><b>实现走原版战斗史，不自己记账本</b>：<c>DamageReceivedEntry</c> 带 <c>CardSource</c>，
/// 按"上一张牌的 start/finish 下标窗口"求和即可 —— 细节与三个口径如何自动成立见
/// <see cref="CardDamageHistory"/>。</para>
///
/// <para>卡面用三元组显示<b>实算伤害</b>（<c>基准 0 + 4 × 上一张牌伤害</c>），
/// 所以手牌里这张牌的数字会随你打出的牌实时变化。乘数 lambda 必须是静态写法（坑 27）。</para>
///
/// <para><b>判据日志（2026-10-11 补）</b>：这条牌以前<b>一条日志都没有</b>，出问题只能读代码。
/// 现在卡面预览与出牌结算都会打一行 <c>[脱身矢] 卡面/结算：…</c>，两条日志里的
/// 「上一张牌伤害」必须相等 —— 那就是"显示值 = 实际值"的判据。
/// 去重键是"战斗 + 相位 + 窗口"，所以同一窗口状态只会打一行（历史每入账一条都会重问一次，
/// 不去重会刷屏）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class EscapeArrow : ModCardTemplate, IAmmoCard
{
    private const int BaseEnergyCost = 3;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.AnyEnemy;
    private const bool ShowInCardLibrary = true;

    /// <summary>卡面文案里的 {ExtraDamage:diff()} 与结算用的倍数（同一个数，别再写死第二份）。</summary>
    private const decimal ExtraDamageMultiplier = 4m;

    private static readonly Logger Log = new("RayMod.Cards", LogType.Generic);

    /// <summary>判据日志去重键：只在"窗口真的变了"时打一行。</summary>
    private static (int Combat, string Phase, decimal Damage, int PreviousStart, int PreviousFinish)? _lastLogged;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CalculationBaseVar(0m),
        new ExtraDamageVar(ExtraDamageMultiplier),
        new CalculatedDamageVar(ValueProp.Move)
            .WithMultiplier((card, _) => QueryPreviousDamage(card)),
        new DynamicVar("AmmoGain", 2m)
    ];

    public EscapeArrow() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        await DamageCmd.Attack(DynamicVars.CalculatedDamage)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_attack_slash", null, "heavy_attack.mp3")
            .Execute(choiceContext);

        AmmoCmd.AddAmmo(Owner, DynamicVars["AmmoGain"].IntValue);
    }

    protected override void OnUpgrade()
    {
        // 升级：弹药 +2 → +3（设计稿："弹药+3"）。伤害倍数不变。
        AddKeyword(CardKeyword.Retain);
    }

    /// <summary>
    /// 卡面预览与出牌结算共用的乘数（= 上一张牌伤害）。
    /// 注意这必须能被静态 lambda 调用（<c>WithMultiplier</c> 会拒绝捕获式 lambda，坑 27）。
    /// </summary>
    private static decimal QueryPreviousDamage(CardModel card)
    {
        int previous = CardDamageHistory.LastCardDamage(card.Owner);
        LogQuery(card, previous);

        return previous;
    }

    /// <summary>
    /// 判据日志：<c>[脱身矢] 卡面/结算：上一张牌伤害=6 → 本牌=24（previousStart=… 条目数=…）</c>。
    /// 相位由"问这个值的是不是正在结算的那张牌"判定 —— 卡面预览时问的是手牌里的实例，
    /// 结算时问的是正在打出的同一个实例。
    /// </summary>
    private static void LogQuery(CardModel card, int previous)
    {
        CardDamageHistory.Window window = CardDamageHistory.LastWindow;
        string phase = window.IsResolvingCard(card) ? "结算" : "卡面";

        (int, string, decimal, int, int) key =
        (
            window.Combat?.Value ?? -1,
            phase,
            previous,
            window.PreviousStart,
            window.PreviousFinish
        );

        if (_lastLogged == key)
        {
            return;
        }

        _lastLogged = key;

        Log.Info(
            $"[脱身矢] {phase}：上一张牌伤害={previous} → 本牌={ExtraDamageMultiplier * previous}（{window.Describe()}）");
    }
}
