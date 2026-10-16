using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MinionLib.Commands;
using MinionLib.Minion;
using RayMod.Depth;
using RayMod.Powers;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Beasts;

/// <summary>
/// 沙地兽的对外 API。所有卡牌都通过这里召唤/查询，不要直接碰
/// <see cref="SandBeast"/> 或 <see cref="BeastState"/>。
///
/// <para><b>规则（用户 2026-10-03 逐条拍板）：</b></para>
/// <list type="number">
/// <item>上限初始 <b>2</b>、硬顶 <b>6</b>；<b>战斗结束重置</b>。</item>
/// <item>召唤数量先过层数：<b>&gt;50 层一只都不召唤</b>；<b>30 层起少召唤一只</b>（阈值型，持续生效）。</item>
/// <item>没满上限 → 直接入场；<b>已满 → 随机替换一只</b>，新兽<b>继承</b>被替换者的印记。</item>
/// <item>每只兽入场即<b>随机标记一个敌人</b>（+1 层印记，可叠加）。</item>
/// <item>上限被调低导致数量超出 → <b>随机移除</b>多出来的，印记一并消失。</item>
/// </list>
///
/// <para><b>召唤事件</b>复用已有的 <see cref="DepthEvents.RaiseMinionSummoned"/>
/// （沙地兽之怒 / 祝福 / 哺育 都要监听它），不另建总线。</para>
/// </summary>
public static class BeastCmd
{
    private static readonly Logger Log = BeastRunData.Log;

    // ===================== 读 =====================

    /// <summary>场上活着的沙地兽（顺序 = 宠物列表顺序）。</summary>
    public static IReadOnlyList<Creature> GetBeasts(Player? player)
    {
        var pets = player?.PlayerCombatState?.Pets;

        if (pets == null)
        {
            return Array.Empty<Creature>();
        }

        var result = new List<Creature>();

        foreach (var pet in pets)
        {
            if (pet.IsAlive && pet.Monster is SandBeast)
            {
                result.Add(pet);
            }
        }

        return result;
    }

    /// <summary>
    /// 当前沙地兽数量。这也是 <c>DepthPower.MinionCount()</c>（20 层起"无沙地兽每回合 -6HP"）
    /// 用的计数 —— 用户 2026-10-03 确认"沙地兽真的出现在场上"，所以这里数的是真实宠物。
    /// </summary>
    public static int GetCount(Player? player) => GetBeasts(player).Count;

    /// <summary>本场战斗的沙地兽上限。</summary>
    public static int GetCap(Player? player) => TryGetState(player)?.Cap ?? DepthConstants.MinionCapInitial;

    /// <summary>本回合还能吃到印记增伤的牌数。</summary>
    public static int GetQuotaRemaining(Player? player) => TryGetState(player)?.QuotaRemaining ?? 0;

    /// <summary>
    /// 取玩家的沙地兽状态；战斗外（主菜单预览卡牌）拿不到存档，返回 null 而不是抛异常。
    ///
    /// <para><b>★ 2026-10-11：这里是唯一的读取入口，也是"本回合记账"的滚账点。</b>
    /// 拿到 state 之后先 <see cref="RollTurnOver"/> —— 于是所有读取方
    /// （「沙地兽的拯救」的额外打出次数 / 卡面 <c>{ExtraPlays}</c> / 手牌发光 / 印记增伤配额）
    /// 拿到的都一定是<b>当前回合号</b>的值，不再依赖"回合开始钩子什么时候跑"。
    /// 详见 <see cref="BeastState.CountedTurn"/>。</para>
    /// </summary>
    public static BeastState? TryGetState(Player? player)
    {
        if (player == null)
        {
            return null;
        }

        try
        {
            var state = BeastRunData.Store.Get(player);

            RollTurnOver(player, state, "读取");

            return state;
        }
        catch (Exception e)
        {
            Log.Warn($"[沙地兽] 取状态失败: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// ★ 2026-10-11：把"本回合"记账滚到玩家当前的回合号；<b>真的换回合了就打一条判据日志</b>。
    ///
    /// <para>判据日志形如：<c>[沙地兽] 复位：回合号 8（上次 7，触发=回合开始钩子）→ …</c>
    /// —— 下次实机看日志就能一眼确认"复位发生在哪个回合号、是谁触发的"。
    /// 触发名只有两种：<c>回合开始钩子</c>（<c>SandBeastQuotaPower.AfterPlayerTurnStart</c>）
    /// 与 <c>读取</c>（任一次 <c>TryGetState</c>，例如卡面/打牌/发光）。</para>
    ///
    /// <para>战斗外（<c>PlayerCombatState == null</c>，例如主菜单的卡牌预览）直接跳过：
    /// 那时读到的字段本来就没有"本回合"可言。</para>
    /// </summary>
    internal static void RollTurnOver(Player player, BeastState state, string trigger)
    {
        int turn = player.PlayerCombatState?.TurnNumber ?? BeastState.TurnUnknown;

        if (turn == BeastState.TurnUnknown)
        {
            return;
        }

        int previous = state.CountedTurn;

        if (!state.RollTurn(turn))
        {
            return;
        }

        Log.Info($"[沙地兽] 复位：回合号 {turn}" +
                 $"（上次 {(previous == BeastState.TurnUnknown ? "未知" : previous.ToString())}，触发={trigger}）" +
                 "→ 本回合召唤/被拦/配额/替换 已清零");

        // 角标显示的是"本回合还剩几张牌吃印记增伤"，滚账后顺手同步（Power 不在位时空操作）。
        SyncQuotaPower(player);
    }

    /// <summary>
    /// ★ 2026-10-11：回合开始钩子专用入口 —— 由 <c>SandBeastQuotaPower.AfterPlayerTurnStart</c> 调。
    ///
    /// <para>它<b>不是</b>正确性的必要条件（正确性在 <see cref="RollTurnOver"/> 里，读写都滚账），
    /// 存在的意义有两个：① 让判据日志标明"是回合开始钩子触发的"；
    /// ② 让手牌角标在回合一开始就刷新（不依赖玩家先读一次）。</para>
    /// </summary>
    internal static void RollTurnForTurnStart(Player player)
    {
        try
        {
            var state = BeastRunData.Store.Get(player);

            RollTurnOver(player, state, "回合开始钩子");
        }
        catch (Exception e)
        {
            // 回合开始钩子抛异常会把整个游戏卡死（坑 42），这里必须吞掉。
            Log.Warn($"[沙地兽] 回合开始滚账失败（本回合跳过）: {e.GetType().Name}: {e.Message}");
        }
    }

    // ===================== 战斗内同步 =====================

    /// <summary>
    /// 确保玩家身上挂着 <see cref="SandBeastQuotaPower"/>，并借"Power 不在位"判定
    /// "本场战斗第一次"来重置全部沙地兽状态。
    ///
    /// <para>与 <c>DepthCmd.EnsurePower</c> / <c>AmmoCmd.EnsurePower</c> 完全同构，
    /// 由初始遗物「下矿」在回合开始时调用。
    /// <b>⚠️ 顺序要求：它必须排在 <c>DepthCmd.AddDepth</c> 之前</b>
    /// （归零发生在加层数之后会把第一回合的 +3 吃掉，见 MineRelic 的注释）。</para>
    /// </summary>
    public static async Task EnsurePower(
        PlayerChoiceContext choiceContext, Player player, CardModel? cardSource = null)
    {
        var power = player.Creature.GetPower<SandBeastQuotaPower>();

        if (power == null)
        {
            // Power 不在位 = 本场战斗还没初始化过（原版战斗结束会清空所有 Power）。
            BeastRunData.Store.Get(player).ResetForCombat();

            power = await PowerCmd.Apply<SandBeastQuotaPower>(
                choiceContext, player.Creature, 1m, applier: null, cardSource: cardSource);
        }

        if (power != null)
        {
            power.SyncFromState();
        }
    }

    // ===================== 召唤 =====================

    /// <summary>
    /// 召唤 <paramref name="count"/> 只沙地兽（卡牌的"召唤 N 只"都走这里）。
    /// 返回<b>实际</b>召唤出来的只数（被层数减益或上限拦掉时会少于请求值）。
    /// </summary>
    public static async Task<int> Summon(
        PlayerChoiceContext choiceContext, Player player, int count, CardModel? cardSource = null)
    {
        if (count <= 0)
        {
            return 0;
        }

        var state = TryGetState(player);

        if (state == null || player.Creature.CombatState == null)
        {
            return 0;
        }

        // ★★★ 2026-10-13：**让"能召唤"和"有配额能力"绑在一起**（用户指出的真场景）。
        //
        //   背景：`SandBeastQuotaPower` 原先**只由莱伊的两个遗物**挂
        //   （「下矿」`MineRelic.cs` / 「伙伴」`BeastCompanion.cs`），
        //   所以"非莱伊玩家召唤沙地兽"会掉进一个**半成品状态**：
        //     · 有上限（`BeastState.Cap` 默认 2）· 能召唤 · 能挂印记（`ApplyMark` 只认 applier）
        //     · **但没有配额角标** · **吃不到自己召的兽的印记增伤**
        //       （`SandBeastMarkPower` 要求"打伤害的人身上有 quota 能力"）
        //     · **`Cap` 也不会每场复位**（`ResetForCombat` 只在 `EnsurePower` 里）
        //
        //   ⚠️ **这不是理论情况，原版真的能走到**：遗物「棱彩宝石」（`PrismaticGem.cs:46`）
        //      把卡池换成 `player.UnlockState.CharacterCardPools ∪ 原池`，而
        //      `UnlockState.CharacterCardPools => Characters.Select(c => c.CardPool)`
        //      = **所有角色（含 mod 角色）** ⇒ 非莱伊玩家**能抽到莱伊的牌**（含「沙地兽」）。
        //      （对照：「色彩哲学家」事件**不行** —— 它的池是硬编码的 5 个原版池，
        //        `ColorfulPhilosophers.cs:18-24`。）
        //
        //   放在这里而不是"每个召唤入口各挂一次"，是因为召唤入口有 8 个以上
        //   （「沙地兽」「装弹」「跳舞的月光」「广域警觉」「去咧嘴谷」「哺育」「饲料」「蓄势」…），
        //   而 `Summon` 是**唯一**的汇聚点 —— 判据只有一处（铁律 50）。
        //   对莱伊是 **no-op**：能力本来就在，`EnsurePower` 只多做一次 `SyncFromState`。
        await EnsurePower(choiceContext, player, cardSource);

        try
        {
            if (state.Cap <= 0)
            {
                state.BlockedSummonsThisTurn += count;
                Log.Info($"[沙地兽] 上限为 0，不召唤（本回合已被拦 {state.BlockedSummonsThisTurn} 只）");
                return 0;
            }

            int depth = DepthCmd.GetDepth(player);

            // 「探照灯」：本回合豁免层数负面效果（用户拍板 Q8 = 五条全免）——
            // 其中包括"30 层起少召唤一只"和"51 层起不能再召唤"这两条。
            bool penaltySuppressed = DepthPenalty.IsSuppressed(player);

            // 大于 50 层：一只都不召唤。⚠️ 只拦"召唤"这件事，
            // 卡牌的其他部分（打伤害、给格挡）照常生效 —— 用户 2026-10-03 确认。
            if (!penaltySuppressed && depth > DepthConstants.NoSummonDepth)
            {
                state.BlockedSummonsThisTurn += count;
                Log.Info($"[沙地兽] depth={depth} > {DepthConstants.NoSummonDepth}，不能召唤" +
                         $"（本回合已被拦 {state.BlockedSummonsThisTurn} 只）");
                return 0;
            }

            // 30 层起：少召唤一只（阈值型，"持续生效"，同 20 层起 -6HP 的口径）。
            if (!penaltySuppressed && depth >= DepthConstants.MinionSummonPenaltyMinDepth)
            {
                int requested = count;

                count = Math.Max(0, count - DepthConstants.MinionSummonPenaltyAtDepth30);

                // 诊断：被这条减益吃掉的只数也记一笔（单只召唤牌会被整只吃掉 → 0）。
                state.BlockedSummonsThisTurn += requested - count;

                if (count == 0)
                {
                    Log.Info($"[沙地兽] depth={depth} >= {DepthConstants.MinionSummonPenaltyMinDepth}，" +
                             $"少召唤 {DepthConstants.MinionSummonPenaltyAtDepth30} 只后为 0，不召唤" +
                             $"（本回合已被拦 {state.BlockedSummonsThisTurn} 只）");
                    return 0;
                }
            }

            int summoned = 0;

            for (int i = 0; i < count; i++)
            {
                if (GetCount(player) >= state.Cap)
                {
                    await ReplaceOne(choiceContext, player, state, cardSource);
                }
                else
                {
                    await AddOne(choiceContext, player, inherited: null, cardSource);
                }

                summoned++;
            }

            state.SummonedThisCombat += summoned;
            state.SummonedThisTurn += summoned;

            // 音效：放下沙地兽（对应游戏里的 ON_UNIT_BORN.token_10034_ray_sndbst）。
            // ★ 2026-10-15：带 player（门控会确认"本地玩家 + 他玩的是莱伊"才响，§S47）。
            Audio.SfxPlayer.Play(Audio.Sfx.BeastSet, player);

            SyncQuotaPower(player);

            // 广播给"每召唤 N 只就触发"的卡（沙地兽之怒 / 祝福 / 哺育 / 之盾 ...）。
            // ⚠️ cardSource 一起带上：「哺育」要排除"打出「沙地兽」卡触发的召唤"（用户 2026-10-04 拍板）。
            DepthEvents.RaiseMinionSummoned(player, summoned, cardSource);

            Log.Info($"[沙地兽] 召唤 {summoned} 只（depth={depth}，回合号={state.CountedTurn}）：" +
                     $"场上 {GetCount(player)}/{state.Cap}，本场累计 {state.SummonedThisCombat}，" +
                     $"本回合 {state.SummonedThisTurn}（本回合已被层数拦掉 {state.BlockedSummonsThisTurn} 只）");

            return summoned;
        }
        catch (Exception e)
        {
            Log.Error($"[沙地兽] Summon({count}) 失败: {e}");
            return 0;
        }
    }

    /// <summary>
    /// 让一只沙地兽退场（移除印记 → 杀掉）。
    /// 用在：满上限替换、10 层起的寿命到期、上限被调低。
    /// </summary>
    public static async Task Retire(PlayerChoiceContext choiceContext, Creature beast)
    {
        // 音效：收回沙地兽（对应 ray_sndbst_finish_2）。满上限替换时也会响 ——
        // 一次替换本来就是"收一只 + 放一只"，两声正好对上的。
        // ★ 2026-10-15：owner 取这只沙地兽的归属（`beast.PetOwner`），门控见 SfxPlayer.Play 重载（§S47）。
        Audio.SfxPlayer.Play(Audio.Sfx.BeastUnset, beast.PetOwner);

        if (beast.Monster is SandBeast model)
        {
            await RemoveMarkOf(choiceContext, model);
        }

        // force: true —— 沙地兽不是"被打死"的，血量只是个占位。
        // 之后 MinionLib 的 MinionKillPatch 会负责把它从战斗里清掉
        // （原版只清理敌人侧的尸体，宠物侧是 MinionLib 补的）。
        await CreatureCmd.Kill(beast, force: true);

        // ★★ 2026-10-09：**节点得我们自己收**（用户报的"大量替换时有残影"）。
        //    反编译实证：`CreatureCmd.Kill` 里是
        //    `StartDeathAnim(shouldRemoveFromCombat && creature.IsMonster)`，而
        //    `AnimDie(shouldRemove)` **只有 true 才播淡出、才收节点**；同一方法里
        //    `NCombatRoom.RemoveCreatureNode` **也只对 IsMonster 调**。
        //    沙地兽是 Pet（`IsMonster == false`）→ 两条路都不走；MinionLib 的
        //    `MinionKillPatch` 更是**只从战斗状态里摘掉、完全不碰节点**。
        //    → 节点带着形象永远留在场上，一次换 N 只就是 N 个残影。
        BeastArt.RemoveArt(beast);
    }

    /// <summary>
    /// 摘掉一只沙地兽持有的<b>全部</b>印记（每个敌人 -1 层，到 0 层时
    /// <c>PowerCmd.ModifyAmount</c> 会自动移除 Power）。
    ///
    /// <para>幂等：先把持有列表整体取走（<see cref="SandBeast.TakeMarkedEnemies"/>），
    /// 所以重复调用第二次就是空操作。这也是"满上限替换"能工作的关键 ——
    /// 替换时旧兽先把手里的印记<b>交给</b>新兽（层数原地不动），
    /// 于是这里再被调用时已经没东西可摘。</para>
    /// </summary>
    public static async Task RemoveMarkOf(PlayerChoiceContext choiceContext, SandBeast model)
    {
        var enemies = model.TakeMarkedEnemies();

        foreach (var enemy in enemies)
        {
            var mark = enemy.GetPower<SandBeastMarkPower>();

            if (mark == null)
            {
                // 目标已经死了（Power 随生物消失），什么都不用做。
                continue;
            }

            await PowerCmd.ModifyAmount(choiceContext, mark, -1m, applier: null, cardSource: null);

            Log.Info($"[沙地兽] 退场摘掉印记（{enemy.Name} 剩 {enemy.GetPowerAmount<SandBeastMarkPower>()} 层）");
        }
    }

    // ===================== 印记转移 =====================

    /// <summary>
    /// 把所有沙地兽印记<b>集中到一个敌人</b>身上（「沙地兽之心」）。
    ///
    /// <para>语义（用户 2026-10-03 拍板）：转移的是<b>层数总和</b>；原敌人清零；
    /// 多只兽标同一敌人时那些层一起转。返回实际集中的层数（0 = 场上本来就没有印记）。</para>
    ///
    /// <para><b>两件事必须一起做</b>，只做一半就会留下永远清不掉的孤儿印记：</para>
    /// <list type="number">
    /// <item><b>记账搬家</b>：每只兽手里"记着哪个敌人"的条目改成 <paramref name="target"/>
    /// （<b>条目数一个不变</b>），将来兽退场时才能摘对对象。</item>
    /// <item><b>层数搬家</b>：把所有敌人身上的 <see cref="SandBeastMarkPower"/> 摘下并合计，
    /// 再一次性挂到 <paramref name="target"/> 上。⚠️ <paramref name="target"/> 自己原有的层
    /// <b>也走同一条路</b>（先全摘、再挂 total），否则它自己的层会被算两遍。</item>
    /// </list>
    /// </summary>
    /// <returns>实际集中到目标身上的层数。</returns>
    public static async Task<int> TransferAllMarks(
        PlayerChoiceContext choiceContext, Player player, Creature target, CardModel? cardSource = null)
    {
        var enemies = player.Creature.CombatState?.HittableEnemies;

        if (enemies == null || enemies.Count == 0)
        {
            return 0;
        }

        try
        {
            // 1) 记账先搬家。必须排在"摘 Power"之前 —— 摘完之后就没法区分
            //    "这个敌人本来就死了"和"刚被我们摘空的"了（DropDeadMarks 查的是 IsAlive）。
            int ownedLayers = 0;

            foreach (var beast in GetBeasts(player))
            {
                if (beast.Monster is not SandBeast model)
                {
                    continue;
                }

                model.DropDeadMarks();
                ownedLayers += model.MarkedEnemies.Count;
                model.RetargetMarks(target);
            }

            // 2) 层数搬家：全部摘下 + 合计。
            int total = 0;

            foreach (var enemy in enemies)
            {
                if (enemy.GetPower<SandBeastMarkPower>() == null)
                {
                    continue;
                }

                int amount = enemy.GetPowerAmount<SandBeastMarkPower>();

                if (amount <= 0)
                {
                    continue;
                }

                total += amount;

                var mark = enemy.GetPower<SandBeastMarkPower>();

                if (mark != null)
                {
                    await PowerCmd.ModifyAmount(
                        choiceContext, mark, -amount, applier: null, cardSource: null);
                }
            }

            if (total > 0)
            {
                await PowerCmd.Apply<SandBeastMarkPower>(
                    choiceContext, target, total, applier: player.Creature, cardSource: cardSource);
            }

            // 3) 这两个数应该永远相等（"敌人身上的层数"= "各只兽记的条目数"）。
            //    不等说明别处把记账写坏了 —— 留日志好排查，但效果照常按敌人的层数结算。
            if (ownedLayers != total)
            {
                Log.Warn($"[沙地兽] 印记集中：记账 {ownedLayers} 层 ≠ 敌人身上 {total} 层" +
                         "（记账可能已写坏，本次仍按敌人的层数结算）");
            }

            Log.Info($"[沙地兽] 印记集中：{total} 层 -> {target.Name}");

            return total;
        }
        catch (Exception e)
        {
            Log.Error($"[沙地兽] TransferAllMarks 失败: {e}");
            return 0;
        }
    }

    // ===================== 上限 =====================

    /// <summary>
    /// 上限 +/-delta（本场战斗内有效，被夹在 [0, <see cref="DepthConstants.MinionCapMax"/>]）。
    ///
    /// <para>上限被<b>调低</b>时，多出来的沙地兽会被随机移除、印记一并消失
    /// （用户 2026-10-03 确认，这是「沙尘暴」上限 -1 的代价）。</para>
    /// </summary>
    public static async Task ModifyCap(
        PlayerChoiceContext choiceContext, Player player, int delta)
    {
        var state = TryGetState(player);

        if (state == null)
        {
            return;
        }

        try
        {
            int oldCap = state.Cap;
            state.Cap = Math.Clamp(state.Cap + delta, 0, DepthConstants.MinionCapMax);

            if (state.Cap == oldCap)
            {
                return;
            }

            Log.Info($"[沙地兽] 上限 {oldCap} -> {state.Cap}（delta={delta}）");

            while (GetCount(player) > state.Cap)
            {
                var beasts = GetBeasts(player);

                if (beasts.Count == 0)
                {
                    break;
                }

                var victim = player.RunState.Rng.CombatTargets.NextItem(beasts);

                if (victim == null)
                {
                    break;
                }

                await Retire(choiceContext, victim);
            }

            SyncQuotaPower(player);
        }
        catch (Exception e)
        {
            Log.Error($"[沙地兽] ModifyCap({delta}) 失败: {e}");
        }
    }

    // ===================== 内部 =====================

    /// <summary>
    /// 召唤一只沙地兽，并把印记挂上。返回<b>这次新挂的敌人</b>（没有可选敌人时返回 null）。
    ///
    /// <para><b>两件事，顺序不能反</b>（用户 2026-10-03 明确："召唤沙地兽的时候会附着层数"）：</para>
    /// <list type="number">
    /// <item><b>接管</b> <paramref name="inherited"/> —— 满上限替换时，被替换者持有的印记
    /// 原封不动转交给新兽（<b>层数不变</b>，只是换了持有者，将来由新兽负责摘）。</item>
    /// <item><b>自己再挂一层</b> —— 基础规则"被召唤后立即随机标记一个敌人"。
    /// 所以一次替换的净效果是<b>印记总层数 +1</b>，这正是设计要的。</item>
    /// </list>
    /// </summary>
    /// <param name="inherited">要接管的印记（满上限替换时传被替换者交出的那些）；null = 没有可继承的。</param>
    private static async Task<Creature?> AddOne(
        PlayerChoiceContext choiceContext, Player player, List<Creature>? inherited, CardModel? cardSource)
    {
        var beast = await MinionCmd.AddMinion<SandBeast>(
            choiceContext, player,
            new MinionSummonOptions(Position: MinionPosition.Front, Source: cardSource));

        if (beast?.Monster is not SandBeast model)
        {
            Log.Warn("[沙地兽] AddMinion 没有返回沙地兽模型，跳过标记");
            return null;
        }

        // 帧动画美术（把兜底占位图换成桌面那张 GIF 拆出来的 30 帧）。失败只是没美术，不影响玩法。
        BeastArt.ApplyAnimatedArt(beast);

        // 1) 接管旧兽交出来的印记（层数原地不动）。
        if (inherited != null)
        {
            foreach (var enemy in inherited)
            {
                if (enemy.IsAlive)
                {
                    model.AddMarkedEnemy(enemy);
                }
            }
        }

        // 2) 基础规则：新召唤的沙地兽立即挂印记（「萌宠博主」会换成"所有敌人各一层"）。
        return await MarkOnSummon(choiceContext, player, model, cardSource);
    }

    /// <summary>
    /// 新兽入场时的"挂印记"这一步（从 <see cref="AddOne"/> 拆出来，2026-10-04）。
    ///
    /// <para><b>两种形态</b>：</para>
    /// <list type="bullet">
    /// <item><b>基础规则</b>（没有「萌宠博主」）：随机标记<b>一个</b>敌人，+1 层。</item>
    /// <item><b>「萌宠博主」在场</b>：<b>替换</b>掉随机单标记 —— 所有敌人<b>各挂一层</b>，
    /// 然后由那张能力牌对所有有印记的敌人各造成一次伤害。
    /// （用户 2026-10-04 拍板："替换原来的随机单标记，改为所有敌人各挂一层"。）</item>
    /// </list>
    ///
    /// <para>⚠️ 记账与层数必须<b>严格 1:1</b>：每挂一层就 <c>AddMarkedEnemy</c> 一次
    /// （<see cref="SandBeast.AddMarkedEnemy"/> 刻意不去重，见那边的注释）。
    /// 漏一次就会留下退场时摘不掉的孤儿印记；多一次则会把别人的层数摘掉。</para>
    /// </summary>
    /// <returns>这次新挂的第一个敌人（没有可挂的敌人时返回 null）。</returns>
    private static async Task<Creature?> MarkOnSummon(
        PlayerChoiceContext choiceContext, Player player, SandBeast model, CardModel? cardSource)
    {
        var enemies = player.Creature.CombatState?.HittableEnemies;

        if (enemies == null || enemies.Count == 0)
        {
            // 场上已经没敌人了（最后一只刚被打死、战斗即将结束）。
            // 允许存在"没有印记"的沙地兽，不报错 —— 这与"目标死了不重新标记"是同一条规则。
            Log.Info("[沙地兽] 场上没有可选敌人，这只暂时没有印记");
            return null;
        }

        // 「萌宠博主」：随机单标记 → 所有敌人各一层。
        var blogger = player.Creature.GetPower<PetBloggerPower>();

        if (blogger != null)
        {
            // 快照一份：挂印记的过程里敌人列表可能会变（伤害打死敌人）。
            var all = enemies.ToList();

            foreach (var enemy in all)
            {
                model.AddMarkedEnemy(enemy);

                await PowerCmd.Apply<SandBeastMarkPower>(
                    choiceContext, enemy, 1m, applier: player.Creature, cardSource: cardSource);
            }

            Log.Info($"[沙地兽] 萌宠博主：所有敌人各挂一层（{all.Count} 个）" +
                     $"；这只兽共持有 {model.MarkedEnemies.Count} 层");

            // 附带伤害：对所有**有印记**的敌人各结算一次（刚挂完，所以就是全部敌人）。
            await blogger.OnBeastSummoned(choiceContext);

            return all[0];
        }

        var target = PickRandomEnemy(player);

        if (target == null)
        {
            Log.Info("[沙地兽] 场上没有可选敌人，这只暂时没有印记");
            return null;
        }

        model.AddMarkedEnemy(target);

        await PowerCmd.Apply<SandBeastMarkPower>(
            choiceContext, target, 1m, applier: player.Creature, cardSource: cardSource);

        Log.Info($"[沙地兽] 新挂一层 -> {target.Name}（该敌人现在 {target.GetPowerAmount<SandBeastMarkPower>()} 层）" +
                 $"；这只兽共持有 {model.MarkedEnemies.Count} 层");

        return target;
    }

    /// <summary>
    /// 满上限时：随机挑一只替换掉。
    ///
    /// <para><b>净效果 = 印记总层数 +1</b>（用户 2026-10-03 拍的 B 方案）：
    /// 旧兽把持有的层<b>原样交给</b>新兽（层数不动、只换持有者），
    /// 新兽再按基础规则随机挂自己的一层。</para>
    /// </summary>
    private static async Task ReplaceOne(
        PlayerChoiceContext choiceContext, Player player, BeastState state, CardModel? cardSource)
    {
        var beasts = GetBeasts(player);
        var victim = beasts.Count > 0 ? player.RunState.Rng.CombatTargets.NextItem(beasts) : null;

        if (victim == null)
        {
            // 理论上到不了这里（调用方已经确认满了），纯防御。
            await AddOne(choiceContext, player, inherited: null, cardSource);
            return;
        }

        var victimModel = victim.Monster as SandBeast;

        // 关键：先把旧兽持有的印记整层"取走"（层数原地一动不动）。
        // 取走之后 Retire → RemoveMarkOf 就摘不到任何东西了 —— 这就是"接管"的实现方式。
        var inherited = victimModel?.TakeMarkedEnemies() ?? new List<Creature>();

        Log.Info($"[沙地兽] 替换前：被替换的兽持有 {inherited.Count} 层（{DescribeTargets(inherited)}）");

        await Retire(choiceContext, victim);
        var newTarget = await AddOne(choiceContext, player, inherited, cardSource);

        state.ReplacedThisTurn = true;

        Log.Info($"[沙地兽] 替换后：接管 {inherited.Count} 层 + 新挂 1 层到 {newTarget?.Name ?? "（无）"}" +
                 $"，该敌人印记 {newTarget?.GetPowerAmount<SandBeastMarkPower>() ?? 0} 层，" +
                 $"场上 {GetCount(player)} 只");
    }

    /// <summary>把一组敌人拼成日志文本（含是否已死）。</summary>
    private static string DescribeTargets(IEnumerable<Creature> enemies)
    {
        var parts = new List<string>();

        foreach (var enemy in enemies)
        {
            parts.Add($"{enemy.Name}{(enemy.IsAlive ? "" : "(已死)")}");
        }

        return parts.Count == 0 ? "无" : string.Join("、", parts);
    }

    /// <summary>随机挑一个可命中的敌人（用原版标准的 CombatTargets 随机流）。</summary>
    private static Creature? PickRandomEnemy(Player player)
    {
        var enemies = player.Creature.CombatState?.HittableEnemies;

        if (enemies == null || enemies.Count == 0)
        {
            return null;
        }

        return player.RunState.Rng.CombatTargets.NextItem(enemies);
    }

    private static void SyncQuotaPower(Player player)
    {
        var power = player.Creature.GetPower<SandBeastQuotaPower>();
        power?.SyncFromState();
    }
}
