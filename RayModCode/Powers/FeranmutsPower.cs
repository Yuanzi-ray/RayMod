using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using RayMod.Beasts;
using STS2RitsuLib.Interop.AutoRegistration;

// ⚠️ 必须起别名：卡牌「沙地兽」和召唤物「沙地兽」同名（RayMod.Cards.SandBeast /
//    RayMod.Beasts.SandBeast）。本文件要造的是**卡牌**那一张。
using SandBeastCard = RayMod.Cards.SandBeast;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 稀有能力牌「巨兽」的载体：<b>每回合多抽 1 张牌</b>、<b>每回合沙地兽上限 +1</b>，
/// 并且<b>每回合开始时选择 2 张手牌变成「沙地兽」</b>。
///
/// <para><b>用户 2026-10-04 拍板："每回合都变"</b>（不是打出时选一次）——
/// 所以选牌界面每个回合开始都会弹一次。</para>
///
/// <para><b>用户 2026-10-04 更正：「上限 +1」是每回合都加，不是打出时加一次</b>。
/// 设计稿原文那三条本来就在同一组里（"回合开始时多抽1张牌，沙地兽上限+1，选择2张手牌变为沙地兽"），
/// 之前只做成了"打出时 +1"，所以从第二回合起就不再涨了。现在每回合开始再 +1，
/// <c>Amount</c> = <b>已累计的上限加成</b>（角标跟着涨，和实际值始终一致）。</para>
///
/// <para><b>为什么挂在 <c>AfterPlayerTurnStart</c> 而不是 <c>BeforeHandDraw</c></b>：
/// 后者在抽牌<b>之前</b>触发（<c>CombatManager.cs:654</c> vs <c>:677</c>），
/// 那时手牌通常是空的（除非有保留牌），<b>根本挑不出 2 张来变</b>。
/// 抽牌已经发生在 <c>AfterPlayerTurnStart</c> 之前，所以这时手牌是完整的。</para>
///
/// <para>选牌提示词用原版共享的 <c>CardSelectorPrefs.TransformSelectionPrompt</c>
/// （"选择要变化的牌"）—— Power 不是 CardModel，用不了 <c>SelectionScreenPrompt</c>，
/// 借原版已有的键就省一个本地化键。</para>
///
/// <para>⚠️ 选中的牌用 <c>Pile == null</c> 兜一层：原版 <c>CardCmd.Transform</c> 对
/// 没有牌堆归属的牌会<b>抛异常</b>。</para>
///
/// <para>⚠️ 上限会被夹在 <c>DepthConstants.MinionCapMax</c>（= 6，硬顶）——
/// 涨到 6 之后这一条就白给了（<c>BeastCmd.ModifyCap</c> 内部夹的）。</para>
/// </summary>
[RegisterPower]
public sealed class FeranmutsPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(FeranmutsPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    private static readonly Logger Log = new("RayMod.Powers", LogType.Generic);

    /// <summary>
    /// 每回合额外抽几张（升级加的是"保留"，不是这个数）。
    /// ★ 默认值只是兜底；实际值在能力被施加时从打出它的那张卡的
    /// <c>DynamicVars["ExtraDraw"]</c> 读进来（卡面数字改了就真的生效）。
    /// </summary>
    private int _extraDraw = 1;

    /// <summary>
    /// 每回合要变几张手牌。
    /// ★ 默认值只是兜底；实际值从卡的 <c>DynamicVars["Transform"]</c> 读进来。
    /// </summary>
    private int _transform = 2;

    /// <summary>每回合沙地兽上限 +几。</summary>
    private const int CapPerTurn = 1;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示<b>已累计</b>的沙地兽上限加成。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    /// <summary>从打出它的卡上取"多抽几张 / 变几张"，取不到就沿用默认值。</summary>
    public override Task AfterApplied(Creature? applier, CardModel? cardSource)
    {
        if (cardSource is { } src)
        {
            if (src.DynamicVars.ContainsKey("ExtraDraw"))
            {
                _extraDraw = src.DynamicVars["ExtraDraw"].IntValue;
            }

            if (src.DynamicVars.ContainsKey("Transform"))
            {
                _transform = src.DynamicVars["Transform"].IntValue;
            }
        }

        // ★★★ 2026-10-13：「巨兽」的**常驻巨眼**（用户："巨兽开出来之后眼睛就常驻了啊，
        //    但是你的鼠标过去会打散"）。挂上之后整场战斗都在，能力掉了由它自己自查收场 ——
        //    和「子弹模式」的弹幕同一套路（见 Art\CardFieldVfx.Feranmuts.cs）。
        RayMod.Art.CardFieldVfx.AttachGiantEye(base.Owner?.Player ?? base.Owner?.PetOwner);

        return Task.CompletedTask;
    }

    /// <summary>
    /// ★★ <b>出牌 → 巨眼炸散</b>（用户 2026-10-13："出牌后所有粒子都散开，然后再回来"）。
    ///
    /// <para>挂在 <c>BeforeCardPlayed</c>：这是"这次出牌"最早的钩子（早于扣费/OnPlay），
    /// 而且会为**任意一张牌**触发 ⇒ "打任何牌都会把眼睛震散"。
    /// 判据只看"是不是自己打的"（多人模式下别人出牌不该震我的眼睛）。</para>
    ///
    /// <para>⚠️ 自动打出（<c>IsAutoPlay</c>）也算"出牌" —— 那是玩家打出的连锁，一样该震。</para>
    /// </summary>
    public override Task BeforeCardPlayed(CardPlay cardPlay)
    {
        if (cardPlay.Player == (base.Owner?.Player ?? base.Owner?.PetOwner))
        {
            // ★ 2026-10-13 联机：把 owner 一起传进去 —— 这道"是不是自己打的"只管得住
            //   本 Power 自己那一份；联机时**队友的**巨兽 Power 也会在**我的客户端**上跑一遍
            //   这个钩子，而 `_giantEye` 是静态单例 ⇒ 不传 owner 的话队友出牌会震我的眼睛。
            RayMod.Art.CardFieldVfx.BurstGiantEye(cardPlay.Player);
        }

        return Task.CompletedTask;
    }

    /// <summary>多抽 1 张（原版"每回合多抽"的写法）。</summary>
    public override decimal ModifyHandDraw(Player player, decimal count)
    {
        if (player != (base.Owner?.Player ?? base.Owner?.PetOwner))
        {
            return count;
        }

        return count + _extraDraw;
    }

    /// <summary>每回合开始（抽牌之后）：沙地兽上限 +1，然后选 2 张手牌变化为「沙地兽」。</summary>
    public override async Task AfterPlayerTurnStart(PlayerChoiceContext choiceContext, Player player)
    {
        if (player != (base.Owner?.Player ?? base.Owner?.PetOwner))
        {
            return;
        }

        // ⚠️⚠️ 整个效果包在 try 里 —— **保命用**。这个钩子在 CombatManager.StartTurn 中间被 await，
        // 抛异常会让 StartTurn 永远完不成 → **整个游戏卡死**（2026-10-04 实测踩过）。
        try
        {
            var combatState = base.CombatState;

            if (combatState == null)
            {
                return;
            }

            // 1) 沙地兽上限 +1（用户 2026-10-04 更正：每回合都加）。
            //    顺序放在选牌**之前**：选牌要弹界面、可能被玩家拖着，上限这条不该被界面卡住。
            await BeastCmd.ModifyCap(choiceContext, player, CapPerTurn);

            // 角标 = 已累计的上限加成（ModifyCap 是"夹在 0..6 之内"的，
            // 但这里记的是"加过多少"，所以到硬顶后角标仍会继续涨 —— 那是玩家的实际投入）。
            await PowerCmd.ModifyAmount(
                choiceContext, this, CapPerTurn, applier: null, cardSource: null);

            // 2) 选 2 张手牌变成「沙地兽」。
            var selected = await CardSelectCmd.FromHand(
                prefs: new CardSelectorPrefs(CardSelectorPrefs.TransformSelectionPrompt, _transform),
                context: choiceContext,
                player: player,
                filter: (CardModel card) => card.IsTransformable,
                source: this);

            foreach (var card in selected.ToList())
            {
                // 变化要求原牌还在某个牌堆里（Pile == null 会抛异常）。
                if (card.Pile == null)
                {
                    continue;
                }

                // ⚠️ 必须用 **CombatState** 造替换牌，不能用 RunState：
                //    进战斗牌堆的卡必须先归属到 CombatState，否则出牌堆时会抛
                //    "XXX must be added to a CombatState before adding it to this pile"
                //    （这正是 2026-10-04 那次卡死的原因）。
                var replacement = combatState.CreateCard<SandBeastCard>(player);

                await CardCmd.Transform(card, replacement);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[巨兽] 每回合开始时处理失败（本回合跳过，不影响回合继续）: {e}");
        }
    }
}
