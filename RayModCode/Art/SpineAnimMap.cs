using MegaCrit.Sts2.Core.Animation;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using RayMod.Ammo;

namespace RayMod.Art;

/// <summary>一段动画（名字 + 是否循环）。名字必须存在于 <see cref="SpineAnims"/> 清单里。</summary>
/// <param name="Name">骨架里的动画名。</param>
/// <param name="Loop">是否循环。<b>排队播放的中间段必须写 false</b>（见 <see cref="SpineAnimMap"/> 的说明）。</param>
public readonly record struct AnimSegment(string Name, bool Loop = false);

/// <summary>一条"动作链"：按顺序排队播放的若干段，播完自动回待机（<see cref="Terminal"/> 为 true 时例外）。</summary>
public sealed class AnimChain
{
    public IReadOnlyList<AnimSegment> Segments { get; }

    /// <summary>
    /// 播完<b>不回待机</b> —— 只有死亡要这样（原版与 RitsuLib 的 <c>Standard</c> 都是这个口径：
    /// 终止状态不设 <c>NextState</c>，否则尸体会自己站起来）。
    /// </summary>
    public bool Terminal { get; }

    public AnimChain(params AnimSegment[] segments) : this(terminal: false, segments)
    {
    }

    public AnimChain(bool terminal, params AnimSegment[] segments)
    {
        Segments = segments;
        Terminal = terminal;
    }
}

/// <summary>
/// <b>动画映射表与分派规则</b> —— 本项目所有"骨架动画叫什么"的知识都集中在这一个文件里。
///
/// <para><b>为什么要有这一层</b>（用户 2026-10-04 提的"映射太死板"）：原来
/// <c>RayModCharacter.GenerateAnimator</c> 把 <c>"Attack_Begin"</c> / <c>"Skill_1_Begin"</c> 这些字符串
/// 直接写死在代码里，于是① 想换一个动画要改代码；② 骨架里明明有 <c>Skill_1/2/3</c> 三套技能动作、
/// <c>Reload_*</c> 装填动作、<c>Attack_Down_*</c> 卧姿动作，全都用不上；
/// ③ 商店那条"原版点名要 <c>relaxed_loop</c>"的路根本接不上（骨架里没有这个名字）。</para>
///
/// <para><b>三条规则</b>：</para>
/// <list type="number">
/// <item><b>触发器 → 类别</b>：游戏只会点 6 个触发器（<c>CreatureAnimator</c> 的
/// <c>Idle/Attack/Cast/PowerUp/Hit/Dead</c> 常量），分派靠"当前正在打的那张牌"细化
/// （<see cref="SpineAnimDispatch.CurrentCard"/>）—— 例如同样是 <c>Cast</c>，
/// 弹药牌走 <c>Reload_*</c>、其余走 <c>Skill_*</c>。</item>
/// <item><b>类别 → 变体</b>：一个类别可以配多条链，按"牌名稳定哈希"挑一条
/// （不用随机数：<c>AnimState.CallTrigger</c> 会逐条问条件，每条各掷一次随机数会互相打架）。
/// 同一种牌每次都播同一条链，不同牌之间自然散开。</item>
/// <item><b>条件分支</b>：<c>CreatureAnimator.AddAnyState(trigger, state, condition)</c> 原生支持条件
/// （<c>AnimState.cs:69-83</c>），取<b>第一个</b>条件通过的；所以最后一条永远不带条件 = 兜底。</item>
/// </list>
///
/// <para>⚠️ <b>中间段一律 <c>Loop = false</c></b>：链是靠 <c>AnimState.NextState</c> 交给 Spine
/// <b>排队</b>播放的（<c>CreatureAnimator.AddNextState</c> → <c>animationState.AddAnimation</c>），
/// 而**排队的循环动画永远不会播完**，后面的 <c>_End</c> 和回待机就都轮不上。</para>
/// </summary>
public static class SpineAnimMap
{
    // ===================== 开关（想换观感改这里） =====================

    /// <summary>
    /// 要不要用 <c>_Down_</c>（卧姿）系列当第二变体。<b>默认关</b>：那是"被打倒在地"的姿势，
    /// 随机拿来打普通攻击/装填会很怪。打开后 <c>Attack_Down_*</c> 会变成攻击的第二个变体。
    /// </summary>
    public const bool UseDownChains = false;

    /// <summary>
    /// 受击动画：骨架里<b>没有</b>专门的 hurt（原版五个角色各有一套自己命名的），
    /// 也没有 <c>OnAttack</c> 这种名字（那是误读 —— 权威清单见 <see cref="SpineAnims"/>）。
    /// 默认留空 = 受击时保持待机；想试备用候选可以填 <see cref="SpineAnims.Default"/>。
    /// </summary>
    public const string? HitAnimation = null;

    // ===================== 显式映射表（"哪张牌播哪个动作"的唯一出处） =====================

    /// <summary>
    /// ★★ <b>指定卡 → 指定动作链</b>（键 = C# 类名，与 <c>CardFieldVfx.Profiles</c> 同一套口径）。
    ///
    /// <para>用户 2026-10-12 定的对应关系：<b>1技能「脱身矢」→ <c>Skill_1_*</c> ·
    /// 2技能「广域警觉」→ <c>Skill_2_*</c> · 3技能「得见光芒」→ <c>Skill_3_*</c></b>。
    /// 想改"哪张牌播哪个动作"，**只改这张表**（别的文件一个字都不用动）。</para>
    ///
    /// <para>⚠️ 为什么必须显式指定：这三张牌 <see cref="CardType"/> 都是 <c>Attack</c>
    /// （`EscapeArrow.cs:42` / `WideareaVigilance.cs:31` / `SeeTheLight.cs:37`），
    /// 所以游戏只会点 <c>attackTrigger</c>，而按类型分派永远给它们 <c>Attack_*</c>。</para>
    /// </summary>
    private static readonly (string CardName, AnimChain Chain)[] ExplicitSkillChains =
    [
        ("EscapeArrow", SkillChain(1)),
        ("WideareaVigilance", SkillChain(2)),
        ("SeeTheLight", SkillChain(3)),
    ];

    /// <summary>
    /// 技能动作链（<c>Skill_N_Begin → Loop → End</c>，播完自动回待机）。
    /// <para>⚠️ 中间段一律 <c>Loop = false</c>：链是靠 <c>AnimState.NextState</c> 交给 Spine
    /// <b>排队</b>播的，而**排队的循环动画永远播不完**，后面的 <c>_End</c> 与回待机就都轮不上（见类注释）。</para>
    /// </summary>
    public static AnimChain SkillChain(int n) => n switch
    {
        1 => new AnimChain(new AnimSegment(SpineAnims.Skill1Begin), new AnimSegment(SpineAnims.Skill1Loop),
            new AnimSegment(SpineAnims.Skill1End)),
        2 => new AnimChain(new AnimSegment(SpineAnims.Skill2Begin), new AnimSegment(SpineAnims.Skill2Loop),
            new AnimSegment(SpineAnims.Skill2End)),
        _ => new AnimChain(new AnimSegment(SpineAnims.Skill3Begin), new AnimSegment(SpineAnims.Skill3Loop),
            new AnimSegment(SpineAnims.Skill3End)),
    };

    // ===================== 类别 =====================

    public enum AnimCategory
    {
        Idle,
        Attack,
        Cast,
        PowerUp,
        Reload,
        Dead,
    }

    /// <summary>
    /// <b>按卡牌类型分派</b>的唯一入口。
    ///
    /// <para>⚠️ 弹药<b>攻击</b>牌刻意<b>不算</b> <see cref="AnimCategory.Reload"/>：
    /// 它本来就要打人，播装填动作会很怪（而且它的攻击动画是 <c>AttackCommand</c> 自己触发的）。
    /// 走装填的是弹药<b>技能/能力</b>（装弹、快速装填、精制子弹、填装形态…）。</para>
    /// </summary>
    public static AnimCategory CategoryFor(CardModel? card)
    {
        if (card == null)
        {
            return AnimCategory.Idle;
        }

        if (card is IAmmoCard && card.Type != CardType.Attack)
        {
            return AnimCategory.Reload;
        }

        return card.Type switch
        {
            CardType.Attack => AnimCategory.Attack,
            CardType.Power => AnimCategory.PowerUp,
            _ => AnimCategory.Cast,
        };
    }

    // ===================== 各触发器的分支计划 =====================

    /// <summary>
    /// 某个触发器这次该走的候选分支（<b>按顺序</b>，条件为 null = 兜底）。
    ///
    /// <para>同一个触发器里，条件都只依赖 <see cref="SpineAnimDispatch.CurrentCard"/> 与触发器名，
    /// 所以一次判定内结果稳定（<c>CallTrigger</c> 会逐条问过去）。</para>
    /// </summary>
    public static List<(Func<bool>? Condition, AnimChain Chain)> BranchesFor(string trigger)
    {
        var branches = new List<(Func<bool>?, AnimChain)>();

        // ★★ 显式映射优先（2026-10-12 用户要求："打出技能相关牌应该把模型动作映射上"）：
        //    「脱身矢 / 广域警觉 / 得见光芒」= 她的 1/2/3 技能，但这三张**卡类型都是 Attack**
        //    ⇒ 不显式指定的话，游戏只会点 attackTrigger，永远播通用的 Attack_Begin/Loop/End，
        //      骨架里那三套 Skill_1/2/3 一次都用不上。
        //    这三条放在**最前面**：`AnimState.CallTrigger` 取第一个条件通过的，所以它们盖过下面的哈希变体。
        //    三个触发器都挂（attack 是当前实际触发的那个；cast/powerUp 是"以后卡类型改了也不漏"）。
        if (trigger is CreatureAnimator.attackTrigger
                     or CreatureAnimator.castTrigger
                     or CreatureAnimator.powerUpTrigger)
        {
            foreach ((string cardName, AnimChain chain) in ExplicitSkillChains)
            {
                branches.Add((() => SpineAnimDispatch.CurrentCardName == cardName, chain));
            }
        }

        switch (trigger)
        {
            case CreatureAnimator.attackTrigger:
                AddVariants(branches, trigger, AttackChains());
                break;

            case CreatureAnimator.castTrigger:
                // 弹药牌（技能/能力）优先走装填，其余按变体散开。
                branches.Add((() => SpineAnimDispatch.IsAmmoCard, ReloadChain()));
                AddVariants(branches, trigger, CastChains());
                break;

            case CreatureAnimator.powerUpTrigger:
                branches.Add((() => SpineAnimDispatch.IsAmmoCard, ReloadChain()));
                AddVariants(branches, trigger, PowerUpChains());
                break;

            case CreatureAnimator.hitTrigger:
                if (HitAnimation is { Length: > 0 } hit)
                {
                    branches.Add((null, new AnimChain(new AnimSegment(hit))));
                }

                break;

            case CreatureAnimator.deathTrigger:
                // Terminal：死亡播完不回待机（否则尸体会自己站起来）。
                branches.Add((null, new AnimChain(terminal: true, new AnimSegment(SpineAnims.Die))));
                break;
        }

        return branches;
    }

    /// <summary>把一组变体加成"挑一个 + 兜底"的分支。</summary>
    private static void AddVariants(
        List<(Func<bool>?, AnimChain)> branches, string trigger, IReadOnlyList<AnimChain> chains)
    {
        if (chains.Count == 0)
        {
            return;
        }

        for (int i = 0; i < chains.Count; i++)
        {
            int index = i;
            int count = chains.Count;

            // 最后一条不带条件 = 兜底，保证这个触发器一定有事发生。
            Func<bool>? condition = i == chains.Count - 1
                ? null
                : () => SpineAnimDispatch.StableIndex(trigger, count) == index;

            branches.Add((condition, chains[i]));
        }
    }

    // ===================== 各类别的动画链 =====================

    /// <summary>待机（循环，链到此为止 —— 不要给它挂 NextState）。</summary>
    public static AnimState CreateIdleState() => new(SpineAnims.Idle, isLooping: true);

    public static IReadOnlyList<AnimChain> AttackChains()
    {
        var list = new List<AnimChain>
        {
            new(new AnimSegment(SpineAnims.AttackBegin), new AnimSegment(SpineAnims.AttackLoop),
                new AnimSegment(SpineAnims.AttackEnd)),
        };

        if (UseDownChains)
        {
            list.Add(new AnimChain(
                new AnimSegment(SpineAnims.AttackDownBegin), new AnimSegment(SpineAnims.AttackDownLoop),
                new AnimSegment(SpineAnims.AttackDownEnd)));
        }

        return list;
    }

    /// <summary>技能三类：骨架里真有 <c>Skill_1/2/3</c> 三套，这是变体最多的一类。</summary>
    public static IReadOnlyList<AnimChain> CastChains() =>
    [
        new AnimChain(new AnimSegment(SpineAnims.Skill1Begin), new AnimSegment(SpineAnims.Skill1Loop),
            new AnimSegment(SpineAnims.Skill1End)),
        new AnimChain(new AnimSegment(SpineAnims.Skill2Begin), new AnimSegment(SpineAnims.Skill2Loop),
            new AnimSegment(SpineAnims.Skill2End)),
        new AnimChain(new AnimSegment(SpineAnims.Skill3Begin), new AnimSegment(SpineAnims.Skill3Loop),
            new AnimSegment(SpineAnims.Skill3End)),
    ];

    /// <summary>
    /// 能力牌：用技能三套里偏"摆架势"的那两套（原版能力牌走 <c>PowerUp</c>，
    /// 我们骨架里没有专门的强化动作）。
    /// </summary>
    public static IReadOnlyList<AnimChain> PowerUpChains() =>
    [
        new AnimChain(new AnimSegment(SpineAnims.Skill3Begin), new AnimSegment(SpineAnims.Skill3Loop),
            new AnimSegment(SpineAnims.Skill3End)),
        new AnimChain(new AnimSegment(SpineAnims.Skill2Begin), new AnimSegment(SpineAnims.Skill2Loop),
            new AnimSegment(SpineAnims.Skill2End)),
    ];

    /// <summary>弹药牌：装填动作（骨架里站着装填是 <c>Reload_*</c>）。</summary>
    public static AnimChain ReloadChain()
    {
        var segments = new List<AnimSegment>
        {
            new(SpineAnims.ReloadBegin), new(SpineAnims.ReloadLoop), new(SpineAnims.ReloadEnd),
        };

        return new AnimChain([.. segments]);
    }

    // ===================== 骨架动画 → AnimState 树 =====================

    /// <summary>
    /// 把一条链变成 <c>AnimState</c> 串（<c>段1 → 段2 → … → 待机</c>），返回链头。
    /// </summary>
    public static AnimState BuildChainHead(AnimChain chain, AnimState idleState)
    {
        AnimState? head = null;
        AnimState? previous = null;

        foreach (var segment in chain.Segments)
        {
            var state = new AnimState(segment.Name, segment.Loop);

            if (head == null)
            {
                head = state;
            }
            else
            {
                previous!.NextState = state;
            }

            previous = state;
        }

        if (head == null)
        {
            return idleState;
        }

        // 播完自动回待机（原版同款写法：CharacterModel.GenerateAnimator 里 xxx.NextState = idle）。
        // ⚠️ 终止链（死亡）不接 NextState。
        if (!chain.Terminal)
        {
            previous!.NextState = idleState;
        }

        return head;
    }

    // ===================== 商店 / 休息处 =====================

    /// <summary>
    /// 原版点名要的"放松待机"名（<c>CharacterModel.relaxedAnim = "relaxed_loop"</c>，
    /// <c>NMerchantCharacter.cs:14</c> 直接播它）。我们骨架里没有，所以映射到 <see cref="SpineAnims.Idle"/>。
    /// </summary>
    public const string RelaxedTarget = SpineAnims.Idle;
}
