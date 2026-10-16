using MegaCrit.Sts2.Core.Animation;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using RayMod.Ammo;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Art;

/// <summary>
/// <b>"现在正在打哪张牌"</b> + <b>技能/能力牌的动画触发</b> —— 动画分派的两块胶水。
///
/// <para><b>为什么要记"当前卡"</b>：游戏只点 6 个触发器（<c>Idle/Attack/Cast/PowerUp/Hit/Dead</c>），
/// 光看触发器分不出"这张技能牌是装弹还是别的"，而 <see cref="SpineAnimMap"/> 的分支条件需要这个信息。
/// 记录点选在遗物的 <c>BeforeCardPlayed</c>（遗物确实是战斗内钩子监听者：
/// <c>CombatState.IterateHookListeners</c> 会把玩家遗物加进去，<c>CombatState.cs:429-436</c>；
/// 而 <c>Hook.BeforeCardPlayed</c> 正是走这条路，<c>Hook.cs:263-270</c>），
/// 出牌结束再清掉。</para>
///
/// <para><b>为什么要在这里补动画</b>：原版每张技能/能力牌都<b>自己</b>调一句
/// <c>await CreatureCmd.TriggerAnim(creature, "Cast"/"PowerUp", 延迟)</c>
/// （110 个原版卡文件里到处都是），而本工程的 92 张卡<b>一句都没写</b> ——
/// 所以之前只有攻击牌会动（攻击动画是 <c>AttackCommand</c> 自己触发的，
/// <c>AttackCommand.cs:230-231</c> 把 <c>_attackerAnimName</c> 设成 <c>"Attack"</c>），
/// 技能/能力牌不仅<b>不动</b>，连<b>施法音效都没有</b>（<c>CreatureCmd.cs:977-988</c> 会按触发器放
/// <c>CastSfx</c>/<c>PowerUpSfx</c>）。与其去改 92 个文件，不如在"每张牌都要过"的这个钩子上统一补。</para>
/// </summary>
public static class SpineAnimDispatch
{
    private static readonly Logger Log = new("RayMod.Art", LogType.Generic);

    private static bool _loggedFailure;

    /// <summary>正在结算的那张牌（没有就是 null）。动画分支条件靠它分派。</summary>
    public static CardModel? CurrentCard { get; private set; }

    /// <summary>当前这张牌算不算"弹药牌"（装填动作的判据）。</summary>
    public static bool IsAmmoCard => CurrentCard is IAmmoCard;

    /// <summary>
    /// 当前卡的 <b>C# 类名</b>（判据口径与 <c>CardFieldVfx.Profiles</c>、
    /// <c>SpineAnimMap.ExplicitSkillChains</c> 一致："哪张牌播哪个动作"只认类名）。
    /// </summary>
    public static string? CurrentCardName => CurrentCard?.GetType().Name;

    /// <summary>
    /// 出牌开始：记下当前卡，并给技能/能力牌补上施法动画。
    /// 攻击牌不在这里触发 —— 它的 <c>Attack</c> 由 <c>AttackCommand</c> 自己发。
    /// </summary>
    public static async Task OnCardPlayed(CardPlay cardPlay)
    {
        var card = cardPlay?.Card;

        NoteCardPlayed(card);

        if (card == null)
        {
            return;
        }

        string? trigger = card.Type switch
        {
            CardType.Skill => CreatureAnimator.castTrigger,
            CardType.Power => CreatureAnimator.powerUpTrigger,
            _ => null,
        };

        if (trigger == null)
        {
            return;
        }

        try
        {
            var owner = card.Owner;

            if (owner == null)
            {
                return;
            }

            float delay = trigger == CreatureAnimator.powerUpTrigger
                ? owner.Character.PowerUpAnimDelay
                : owner.Character.CastAnimDelay;

            // 这一步同时把施法音效放出来（CreatureCmd 内部按触发器选 Sfx）。
            await CreatureCmd.TriggerAnim(owner.Creature, trigger, delay);
        }
        catch (Exception e)
        {
            // ⚠️ 这个钩子被 await 在出牌流程中间（坑 42 的同类风险）：
            // 动画失败最多是"这张牌没动作"，绝不能把出牌卡死。
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                Log.Error($"[动画分派] 触发施法动画失败（只记这一次）：{e}");
            }
        }
    }

    /// <summary>出牌结束：清掉"当前卡"。</summary>
    public static void OnCardPlayFinished(CardPlay? cardPlay)
    {
        var card = cardPlay?.Card;

        if (ReferenceEquals(CurrentCard, card))
        {
            CurrentCard = null;
        }
    }

    /// <summary>记下当前卡（<paramref name="card"/> 为 null 时清空）。</summary>
    public static void NoteCardPlayed(CardModel? card) => CurrentCard = card;

    /// <summary>
    /// 变体挑选：<b>稳定哈希</b>而不是随机数。
    ///
    /// <para>原因：<c>AnimState.CallTrigger</c>（<c>AnimState.cs:69-83</c>）会<b>逐条</b>问分支条件，
    /// 每条条件里各掷一次随机数会互相打架（可能全不中、也可能重复中）。
    /// 哈希只依赖"触发器名 + 当前牌的 Id"，所以一次判定内结果恒定，
    /// 且同一种牌永远播同一条链、不同牌之间自然散开。</para>
    /// </summary>
    public static int StableIndex(string trigger, int count)
    {
        if (count <= 1)
        {
            return 0;
        }

        string key = trigger + "|" + (CurrentCard?.Id.Entry ?? "-");

        // FNV-1a 32 位：.NET 的 string.GetHashCode 每进程加盐，跨启动会变，这里不能用。
        uint hash = 2166136261u;

        foreach (char c in key)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return (int)(hash % (uint)count);
    }
}
