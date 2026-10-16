using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using RayMod.Beasts;
using STS2RitsuLib.Interop.AutoRegistration;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 稀有能力牌「动物图册」的载体：<b>回合开始时把 Amount 张"和沙地兽有关的牌"加入手牌</b>。
///
/// <para>钩子用 <c>BeforeHandDraw</c>（照抄原版 <c>SentryModePower</c> 与
/// <see cref="SandBeastLairPower"/>）：它在抽牌<b>之前</b>把牌塞进手里，
/// 所以玩家第一眼看到手牌时那张牌就在了。</para>
///
/// <para><b>候选池怎么来</b>：从角色卡池 <c>Character.CardPool.AllCards</c> 里筛
/// 实现了 <see cref="ISandBeastCard"/> 的牌 —— <b>用类型判据，不查文案、不查变量名</b>
/// （坑 29 的教训）。随机流用 <c>RunState.Rng.CombatCardGeneration</c>
/// （"生成卡牌"的语义流，和"随机选牌/随机目标"那几条分开，互不干扰）。</para>
///
/// <para>造卡走 <c>CombatState.CreateCard(canonicalCard, owner)</c> + <c>AddGeneratedCardToCombat</c>，
/// 与「沙地兽巢穴」同一套（已验证能造 mod 卡牌）。
/// ⚠️ <b>不要</b>用 <c>RunState.CreateCard</c> —— 那个造出来的卡没归属到战斗，
/// 进战斗牌堆时会抛 "must be added to a CombatState before adding it to this pile"，
/// 而在回合开始钩子里抛异常会**把整个游戏卡死**（2026-10-04 实测）。</para>
/// </summary>
[RegisterPower]
public sealed class AnimalGuidePower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(AnimalGuidePower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    private static readonly Logger Log = new("RayMod.Powers", LogType.Generic);

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示每回合给几张。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    public override async Task BeforeHandDraw(
        Player player, PlayerChoiceContext choiceContext, ICombatState combatState)
    {
        if (player != (base.Owner?.Player ?? base.Owner?.PetOwner))
        {
            return;
        }

        // ⚠️⚠️ 整个效果包在 try 里。**这是保命的**：这个钩子在 CombatManager.StartTurn 的
        // 流程中间被 await，一旦抛异常，StartTurn 永远完不成 →
        // **整个游戏卡死**（2026-10-04 实测踩过，用户截图就是卡在出牌界面）。
        // 效果失败最多是"这回合少给一张牌"，绝不能把游戏挂掉。
        try
        {
            int count = (int)base.Amount;

            for (int i = 0; i < count; i++)
            {
                var canonical = PickRandomSandBeastCard(player);

                if (canonical == null)
                {
                    return;
                }

                // ⚠️ 必须用 **CombatState** 造卡，不能用 RunState：
                //    进战斗牌堆的卡必须先归属到 CombatState，否则
                //    AddGeneratedCardToCombat 会抛
                //    "XXX must be added to a CombatState before adding it to this pile"
                //    （这正是 2026-10-04 那次卡死的原因）。
                var card = combatState.CreateCard(canonical, player);

                await CardPileCmd.AddGeneratedCardToCombat(card, PileType.Hand, player);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[动物图册] 加牌失败（本回合跳过，不影响回合继续）: {e}");
        }
    }

    /// <summary>从角色卡池里随机挑一张"和沙地兽有关的牌"（没有就返回 null）。</summary>
    private static CardModel? PickRandomSandBeastCard(Player player)
    {
        var pool = player.Character?.CardPool;

        if (pool == null)
        {
            return null;
        }

        var candidates = pool.AllCards.Where(card => card is ISandBeastCard).ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        return player.RunState.Rng.CombatCardGeneration.NextItem(candidates);
    }
}
