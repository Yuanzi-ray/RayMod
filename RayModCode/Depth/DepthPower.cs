using System.Text;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名（RayMod 的 Entry.cs 同样处理）。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Depth;

/// <summary>
/// 层数（矿井层数）在<b>战斗内的视图与效果载体</b>。
///
/// <para><b>职责边界（重要，别搞混）：</b></para>
/// <list type="bullet">
/// <item>真实层数存在 <see cref="DepthState"/>（跑团级，跨战斗持久）。</item>
/// <item>这个 Power 只负责：显示层数、施加伤害加成、结算层级减益与战斗结束奖励。</item>
/// <item>改层数请一律走 <see cref="DepthCmd"/>，不要在卡牌里直接碰这个 Power。</item>
/// </list>
///
/// <para>之所以要这么拆：原版 <c>Player.AfterCombatEnd()</c> 会清空玩家所有 Power，
/// 层数不能寄存在这里，否则每场战斗都会归零，"累积层数"的设计就不成立。</para>
///
/// <para><b>另外两个坑：</b></para>
/// <list type="number">
/// <item><c>Amount</c> 固定为 1（保命）。原版 <c>ShouldRemoveDueToAmount()</c> 会把 Amount==0 的 Power 移除，
/// 所以不能拿 Amount 当层数；显示值由覆写 <see cref="DisplayAmount"/> 提供，
/// 描述里的 <c>{Amount}</c> 也要自己重绑（见 <see cref="Description"/>）。</item>
/// <item>全程走原版数据钩子，零 Harmony。伤害加成与 <see cref="StrengthPower"/> 同构。</item>
/// </list>
/// </summary>
[RegisterPower]
public sealed class DepthPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(DepthPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    internal sealed class Data
    {
        /// <summary>层数快照（真实值在 DepthState，这里只用于显示与结算）。</summary>
        public int Depth;

        /// <summary>20-29 档是否已经给过药水（脱离该档后重置）。</summary>
        public bool PotionTierGranted;

        /// <summary>连续超过 <see cref="DepthConstants.DeathDepth"/> 的回合数。</summary>
        public int DeathStreak;

        /// <summary>
        /// 本回合是否豁免层数负面效果（<b>回合开始时锁存</b>的快照，2026-10-04 加）。
        ///
        /// <para>⚠️ <b>为什么必须锁存、不能现场再查 <see cref="DepthPenalty.IsSuppressed"/></b>：
        /// 本次平衡把"无沙地兽每回合 -6"从<b>回合开始</b>挪到了<b>回合结束</b>，而
        /// 「探照灯」（<c>SearchlightPower</c>）恰好<b>也在回合结束</b>消耗自己的保护回合
        /// （<c>Amount -= 1</c>，归零就 <c>PowerCmd.Remove</c>）。
        /// 两者是同一时机上的两个钩子，<b>执行顺序不受我们控制</b>：
        /// 若探照灯先跑完，它可能已经把自己摘掉，此刻现场再查 <c>IsSuppressed</c> 会读到 false
        /// —— 于是"受保护的回合"照样掉血，而且<b>静默不报错</b>（正是坑 29 那一类）。</para>
        ///
        /// <para>回合开始时探照灯的回合末逻辑一定还没跑，那一刻的值是唯一无歧义的，
        /// 所以在这里锁存一次、整回合都读它。</para>
        /// </summary>
        public bool SuppressedThisTurn;
    }

    private static readonly Logger Log = new("RayMod.Depth", LogType.Generic);

    /// <summary>
    /// 本 Power 是否<b>已经从跑团级存档同步过至少一次</b>（见
    /// <see cref="AfterPowerAmountChanged"/> 的说明 —— 它是"出生值不是外部改动"的判据）。
    /// </summary>
    private bool _syncedFromRunState;

    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    /// <summary>层数不会被扣成负数（下限在 DepthState 里夹到 0），所以不需要允许负值。</summary>
    public override bool AllowNegative => false;

    protected override object InitInternalData() => new Data();

    /// <summary>当前层数快照。</summary>
    public int Depth => GetInternalData<Data>().Depth;

    /// <summary>
    /// 显示值 = <see cref="Amount"/>（在 SyncFromRunState 里被同步成 max(层数, 1)）。
    ///
    /// 这里刻意<b>不</b>返回原始层数：那样层数为 0 时角标显示 0、悬停却显示 1，两者会打架。
    /// 保持和 Amount 一致，UI 上才是同一个数字。
    /// </summary>
    public override int DisplayAmount => Amount;

    /// <summary>
    /// <b>悬停描述：只列"当前生效"的增益/负面</b>（2026-10-07 用户要求）。
    ///
    /// <para>以前这里是整张阈值表（10/20/30/50/60/100 全列一遍），玩家得自己对照当前层数找哪条生效。
    /// 现在按 <see cref="Amount"/>（= 层数）挑出生效的那几条拼起来；中性区间给一句
    /// <c>line_none</c> 兜底，避免悬停是空的。</para>
    ///
    /// <para><b>⚠️⚠️ 为什么同时要去掉 <c>SmartDescriptionLocKey</c> 的覆写</b>：
    /// <c>PowerModel.HoverTips</c> 有两条分支（<c>PowerModel.cs:350-410</c>）——
    /// 若 <c>HasSmartDescription</c> 为真就走 <b>SmartDescription 分支</b>，那条路<b>只补内置变量</b>
    /// （Amount/OnPlayer/PlayerCount/OwnerName…），<b>不会</b>用我们注入的 <c>{Lines}</c>；
    /// 结果是 SmartFormat 报 <c>No source extension could handle the selector named "Lines"</c>，
    /// <b>整段描述渲染失败</b>（<c>AmmoPower</c> 的注释里记过同类坑）。
    /// 而 <c>AddDumbVariablesToDescription</c> 是 <b>private</b>（<c>PowerModel.cs:543</c>）没法覆写，
    /// 所以唯一干净的出口就是"让 HasSmartDescription 为假" —— 把本覆写删掉，
    /// 让 <c>SmartDescriptionLocKey</c> 回到默认的 <c>.smartDescription</c>（该键不存在）。</para>
    ///
    /// <para>普通分支会走 <c>Description</c> + <c>GetFormattedText()</c>，我们注入的变量这时才生效。</para>
    /// </summary>
    public override LocString Description
    {
        get
        {
            var loc = new LocString("powers", base.Id.Entry + ".dynamic");

            // 兜底：新键缺失（本地化没跟上）时退回老的整体描述，别让悬停变空。
            if (!loc.Exists())
            {
                var fallback = new LocString("powers", base.Id.Entry + ".description");

                if (fallback.Exists())
                {
                    DynamicVars.AddTo(fallback);
                    fallback.Add("Amount", Amount);
                }

                return fallback;
            }

            loc.Add("Amount", Amount);
            loc.Add("Lines", BuildActiveLines());

            return loc;
        }
    }

    /// <summary>
    /// 按当前层数拼出"当前生效"的那几行（分增益/减益两节）。
    ///
    /// <para>⚠️ 阈值一律取自 <see cref="DepthConstants"/>，别在这里写死数字 ——
    /// 那条"卡面写 59、代码是 50"的老 bug 就是这么来的。</para>
    /// </summary>
    private string BuildActiveLines()
    {
        int depth = (int)Amount;

        var buffs = new List<string>();
        var debuffs = new List<string>();

        // ── 增益 ──
        if (depth >= DepthConstants.AttackBonusMinDepth) buffs.Add(Line("line_attack"));
        if (depth >= DepthConstants.GoldTierMinDepth && depth <= DepthConstants.GoldTierMaxDepth) buffs.Add(Line("line_gold"));
        if (depth > DepthConstants.RelicChanceDepth) buffs.Add(Line("line_relic"));

        // ── 减益 ──
        if (depth >= DepthConstants.PotionTierMinDepth) debuffs.Add(Line("line_noBeast"));
        if (depth > DepthConstants.NoSummonDepth)
        {
            debuffs.Add(Line("line_extraDamage"));
            debuffs.Add(Line("line_noSummon"));
        }
        if (depth > DepthConstants.DeathDepth) debuffs.Add(Line("line_death"));

        if (buffs.Count == 0 && debuffs.Count == 0)
        {
            return Line("line_none");
        }

        var sb = new StringBuilder();

        // 分节标题也走本地化键；某节为空就整节不出现。
        if (buffs.Count > 0)
        {
            sb.Append(Line("sec_buff"));

            foreach (string line in buffs)
            {
                sb.Append('\n').Append(line);
            }
        }

        if (debuffs.Count > 0)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(Line("sec_debuff"));

            foreach (string line in debuffs)
            {
                sb.Append('\n').Append(line);
            }
        }

        return sb.ToString();
    }

    /// <summary>取一条本地化行（键不存在就返回空串，不让整段炸掉）。</summary>
    private string Line(string suffix) =>
        LocString.GetIfExists("powers", $"{base.Id.Entry}.{suffix}")?.GetFormattedText() ?? string.Empty;

    /// <summary>
    /// 从跑团级存档同步层数进来，并处理层级变化的一次性效果。
    /// 由 <see cref="DepthCmd"/> 调用，不要在别处直接改。
    /// </summary>
    internal void SyncFromRunState(int depth)
    {
        var data = GetInternalData<Data>();
        int oldDepth = data.Depth;
        data.Depth = depth;

        // ---- 临时诊断日志（验证完就删）----
        // 目的：确认跑团级层数是否真的每回合递增、以及是否落到了 Power 的 Amount 上。
        // 交接文档第四节的"层数增长已验证"与日志里恒定的 Amount:1 矛盾，
        // 这行日志是用来一次性裁决那件事的。
        Log.Info($"[DepthPower] SYNC old={oldDepth} new={depth} amountBefore={Amount}");

        // Amount 必须跟着层数走，理由有两条：
        //  1. 悬停提示里 PowerModel.HoverTips 会把描述中的 {Amount} 绑成 Amount，
        //     所以 Amount 就是"玩家会看到的数字"。
        //  2. 保命：层数为 0 时 Amount 会变成 0，而 ShouldRemoveDueToAmount() 判的是 Amount==0。
        //     不过那个判定只发生在 PowerCmd.ModifyAmount 里，直接调用 SetAmount 不会触发移除，
        //     所以层数归零时这里用 Math.Max(depth, 1) 兜一层，避免任何意外的移除路径。
        SetAmount(Math.Max(depth, 1), silent: true);

        // 从这里开始，"Amount 被改" 才可能是外部改动（见 AfterPowerAmountChanged）。
        _syncedFromRunState = true;

        if (oldDepth == depth)
        {
            return;
        }

        OnDepthTierChanged(data, oldDepth, depth);
        InvokeDisplayAmountChanged();
    }

    /// <summary>
    /// 把<b>外部</b>对 <c>Amount</c> 的改动<b>回写成真实层数</b>（2026-10-04 补）。
    ///
    /// <para><b>为什么需要它</b>：真实层数存在跑团级存档 <see cref="DepthState"/> 里，
    /// <c>Amount</c> 只是<b>显示镜像</b> —— 于是任何"直接改 Power 层数"的调试手段
    /// 都只改到显示。实测踩过：原版开发者控制台的
    /// <c>power &lt;id&gt; &lt;amount&gt; &lt;target-index&gt;</c>
    /// 对已存在的 Power 走 <c>PowerCmd.ModifyAmount</c>
    /// （<c>ApplyPowerConsoleCmd.cs:71</c>），所以角标能显示到几百，
    /// 而所有档位判定（金币 / 遗物 / 禁召唤 / 死亡）读的仍是真实层数。</para>
    ///
    /// <para>用户 2026-10-04 报的"大于 61 层的遗物从来没出现过"就是这么来的：
    /// 控制台把 Amount 加到几百，日志里真实层数却全程只有 3，遗物档一次都没进过。</para>
    ///
    /// <para><b>不会自我递归</b>：这条钩子只在 <c>PowerCmd.ModifyAmount</c> / <c>PowerCmd.Apply</c>
    /// 里被调用，而本 Power 的数值全程走 <see cref="SyncFromRunState"/> 里的 <c>SetAmount</c>
    /// （不会触发钩子）。再加一道"显示值 == max(真实层数, 1)"的确认，双保险。</para>
    ///
    /// <para><b>⚠️⚠️ 还必须先判 <see cref="_syncedFromRunState"/>（2026-10-04 用户实测「积累」时踩到）</b>：
    /// <c>PowerCmd.Apply</c> 施加<b>全新</b> Power 时会在 <c>ApplyInternal</c>（<c>PowerCmd.cs:139</c>）
    /// 之后<b>立刻</b>派发本钩子（<c>:160</c>），此时 <c>Amount</c> 是 <b>出生值</b>
    /// （<c>DepthCmd.EnsurePower</c> 传的 <c>1m</c>），而层数可能已经带着「积累」的跨战斗值
    /// （真实层数 = 63）。出生值 1 ≠ max(63, 1)，于是回写把<b>刚带回来的层数踩成 1</b> ——
    /// 日志顺序就是铁证：
    /// <c>[积累] 记下层数 63</c> → <c>「积累」带回层数：本场起始层数 = 63</c> →
    /// <c>[DepthPower] Amount 被外部改成 1（真实层数 63）→ 回写成真实层数</c>。</para>
    /// </summary>
    public override async Task AfterPowerAmountChanged(
        PlayerChoiceContext choiceContext, PowerModel power, decimal amount, Creature? applier, CardModel? cardSource)
    {
        if (power != this)
        {
            return;
        }

        if (!_syncedFromRunState)
        {
            // 刚 Apply 出来的出生值（1），不是外部改动 —— 回写会把「积累」带回来的层数踩掉。
            Log.Info($"[DepthPower] 出生值 {base.Amount}（还没同步过跑团存档）→ 跳过回写");

            return;
        }

        if ((base.Owner?.Player ?? base.Owner?.PetOwner) is not Player player)
        {
            return;
        }

        int shown = (int)base.Amount;
        int real = DepthCmd.GetDepth(player);

        // 显示值 == max(真实层数, 1)：这次改动就是我们自己的同步造成的，不用管。
        if (shown == Math.Max(real, 1))
        {
            return;
        }

        Log.Info($"[DepthPower] Amount 被外部改成 {shown}（真实层数 {real}）→ 回写成真实层数");

        await DepthCmd.SetDepth(choiceContext, player, shown);
    }

    // ===================== 层级一次性效果 =====================

    /// <summary>
    /// 层级变动时的一次性效果 + 诊断日志。
    ///
    /// <para>日志会打印"从哪档跨到哪档"，用来排查"高层丢失低层效果"这类问题 ——
    /// 档位是互斥的（进入新档不再享有旧档的<b>档位专属</b>效果），
    /// 但"10 层起攻击 +2"是<b>阈值型</b>效果，不会随档位变化消失（见 <see cref="ModifyDamageAdditive"/>）。</para>
    ///
    /// <para><b>⚠️ 2026-10-11："跨档"必须按区间扫描判，不能只看落点。</b>
    /// 这里的 <paramref name="oldDepth"/> 以前只进诊断日志，效果逻辑一律只看落点 ——
    /// 于是"一步跨过整档"的写入会丢掉该档的<b>进入型收益</b>。用户实测：
    /// 灵异录像带把层数直接设到 61+（0→73 这类），20-29 档那瓶药水从来没发过
    /// （穷举 0..49 × 50..100 = 2550 组合，命中 0 次）。同根因还波及
    /// 「这是对的！」（15~19 翻倍到 30~38，跳过 20-29）与「积累」跨战斗带层数（开局 0→63）。</para>
    ///
    /// <para><b>⚠️ 绝对不要</b>用"循环调 <c>AddDepth(+1)</c>"来补发：那会让「源矿」打 73 次、
    /// 「宛如沙石」给 73 点格挡，并破坏 <c>SourceOrePower.cs:19-20</c> 记录的已拍板口径
    /// <b>"一次 AddDepth 调用 = 一次事件"</b>（「塌方」正是靠它才不滚雪球）。
    /// 所以补发只在<b>本方法内部</b>按区间扫描做，写入路径一次都不多打。</para>
    /// </summary>
    private void OnDepthTierChanged(Data data, int oldDepth, int newDepth)
    {
        // 诊断：跨档时打一条，能直接看出"哪一档生效了 / 有没有被跳过"。
        Log.Info($"[DepthPower] 跨档 {oldDepth} -> {newDepth}：" +
                 $"攻击加成={(newDepth >= DepthConstants.AttackBonusMinDepth ? "有" : "无")}(>=10) " +
                 $"药水档={(newDepth >= DepthConstants.PotionTierMinDepth && newDepth < DepthConstants.GoldTierMinDepth ? "在" : "不在")}(20-29) " +
                 $"金币档={(newDepth >= DepthConstants.GoldTierMinDepth && newDepth <= DepthConstants.GoldTierMaxDepth ? "在" : "不在")}(30-60) " +
                 $"遗物档={(newDepth > DepthConstants.RelicChanceDepth ? "在" : "不在")}(>60) " +
                 $"禁召唤={(newDepth > DepthConstants.NoSummonDepth ? "是" : "否")}(>50)");

        // 20-29 档：第一次进入时给一瓶药水；脱离该档后允许下次再给。
        //
        // ★ 2026-10-11：判定从"落点在档内"改成"落点在档内 或 本次跨越了该档"。
        //   crossedBand 只认**升档**（newDepth > oldDepth），所以 35→25 这类下降不会误发；
        //   区间判据是"老层低于 20 门槛、新层达到 20 门槛"，天然覆盖 19→30 这种整档跳过。
        //   PotionTierGranted 去重 + 离档复位，保证**一次升档只发一瓶**。
        bool inBandNow = newDepth >= DepthConstants.PotionTierMinDepth
                         && newDepth < DepthConstants.GoldTierMinDepth;
        bool crossedBand = newDepth > oldDepth
                           && oldDepth < DepthConstants.PotionTierMinDepth
                           && newDepth >= DepthConstants.PotionTierMinDepth;

        if ((inBandNow || crossedBand) && !data.PotionTierGranted)
        {
            data.PotionTierGranted = true;

            // 判据日志：整档跳过时打一条，实机上一眼能定案"补发有没有发生"。
            if (!inBandNow && crossedBand)
            {
                Log.Info($"[DepthPower] 跳档补发：层数 {oldDepth} -> {newDepth} 跨过 20-29，补 1 瓶药水");
            }

            // ⚠️ 必须用 Owner.Player ?? Owner.PetOwner，不能只用 PetOwner。
            // PetOwner 只对"宠物"有值（PlayerCombatState.AddPet 时设置），
            // 玩家自己的 Creature 上是 null（那个是 Creature.Player）。
            // 早先只判 PetOwner，导致 20-29 档的药水从来没发出来过。
            if ((base.Owner?.Player ?? base.Owner?.PetOwner) is Player player)
            {
                // 处在同步的层数写入路径上，没法 await，交给原版的 RunSafely 派发。
                TaskHelper.RunSafely(GrantRandomPotion(player));
            }
            else
            {
                Log.Warn("[DepthPower] 进入 20-29 档但解析不出 Player，药水不会发放");
            }
        }
        else if (!inBandNow)
        {
            // 离开档位复位，语义不变（落在 20-29 内不清，保证档内不再重复发）。
            data.PotionTierGranted = false;
        }

        // 30-60 档（★ 2026-10-11）：记下"本场曾到过金币档"，供 AfterCombatEnd 结算。
        //
        // 判据与上面药水档同构，只是把结果写进跑团级 DepthState（AfterCombatEnd 那会儿
        // 读的是 state，不是这个 Data）。两种命中任一即置位：
        //  1. inGoldNow   —— 当前落点在 [30,60] 内；
        //  2. crossedGold —— 本次是升档，且跨过了 30 层门槛（old < 30 <= new）。
        //     只认升档，所以 35→25 不会误置位；0→73 / 12→55 / 19→30 / 12→24 全都能置位。
        bool inGoldNow = newDepth >= DepthConstants.GoldTierMinDepth
                         && newDepth <= DepthConstants.GoldTierMaxDepth;
        bool crossedGold = newDepth > oldDepth
                           && oldDepth < DepthConstants.GoldTierMinDepth
                           && newDepth >= DepthConstants.GoldTierMinDepth;

        if (inGoldNow || crossedGold)
        {
            try
            {
                var state = (base.Owner?.Player ?? base.Owner?.PetOwner) is Player goldPlayer
                    ? DepthRunData.Store.Get(goldPlayer)
                    : null;

                if (state != null && !state.GoldTierReachedThisCombat)
                {
                    state.GoldTierReachedThisCombat = true;
                    Log.Info($"[DepthPower] 金币档：本场曾到过（{oldDepth} -> {newDepth}，" +
                             $"跨过 {DepthConstants.GoldTierMinDepth}）→ 战斗结束时按回合数发金币");
                }
            }
            catch (Exception e)
            {
                // 记账失败最多是"本场少拿金币"，绝不能把层数写入流程搞崩。
                Log.Warn($"[DepthPower] 金币档跨档记账失败（本场按结束层数照旧判定）: {e}");
            }
        }
    }

    /// <summary>
    /// 20-29 档：随机获得一瓶药水。
    ///
    /// <para><b>这里踩了两个坑，都是"没照抄原版"造成的：</b></para>
    ///
    /// <para><b>坑 1</b>：别用 <c>PotionCmd.TryToProcure&lt;PotionModel&gt;(player)</c>。
    /// 那个泛型重载内部是 <c>ModelDb.Potion&lt;T&gt;()</c>，要的是<b>具体药水类型</b>
    /// （如 <c>BlockPotion</c>），传抽象的 <c>PotionModel</c> 会抛
    /// <c>KeyNotFoundException: 'POTION.POTION_MODEL' was not present in the dictionary</c>。</para>
    ///
    /// <para><b>坑 2</b>：<c>PotionFactory.CreateRandomPotionInCombat(...)</c> 返回的是
    /// <b>canonical（规范）实例</b>，而 <c>TryToProcure(PotionModel, ...)</c> 第一行就会
    /// <c>potion.AssertMutable()</c>，于是抛
    /// <c>CanonicalModelException: Canonical model of type ...PowderedDemise used in incorrect place</c>。
    /// <b>必须 .ToMutable()</b>。</para>
    ///
    /// <para>正确写法就是原版 <c>Alchemize.cs:24</c> 那一整行（原样照抄，别自己简化）：
    /// <code>
    /// await PotionCmd.TryToProcure(
    ///     PotionFactory.CreateRandomPotionInCombat(owner, owner.RunState.Rng.CombatPotionGeneration).ToMutable(),
    ///     owner);
    /// </code></para>
    ///
    /// <para>教训：<b>凡是"给玩家一个东西"，先找原版同构实现照抄</b> ——
    /// 这类 API 普遍要求 mutable 实例（遗物那边也是 <c>RelicCmd.Obtain(relic.ToMutable(), ...)</c>）。</para>
    /// </summary>
    private async Task GrantRandomPotion(Player player)
    {
        try
        {
            var potion = PotionFactory
                .CreateRandomPotionInCombat(player, player.RunState.Rng.CombatPotionGeneration)
                .ToMutable();

            var result = await PotionCmd.TryToProcure(potion, player);

            if (result.success)
            {
                Log.Info($"[DepthPower] 20-29 档：已发放药水 {potion.Id.Entry}");
                // 语音：「这是短时间离舰申请……」—— 进入 20-29 档拿到药水时响一次。
                // 挂在这里而不是 Hook.AfterPotionProcured：后者对**任何**药水都触发，
                // 而这条只说"这一档的奖励"。
                Audio.VoicePlayer.Play(Audio.VoiceLine.ShortLeave, Audio.VoiceCategory.System);
            }
            else
            {
                // 最常见的原因是药水槽满了（原版允许失败，不该当错误处理）。
                Log.Info($"[DepthPower] 20-29 档：药水发放失败，原因={result.failureReason}");
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[DepthPower] granting potion failed: {e}");
        }
    }

    // ===================== 伤害加成：10 层起 +2 =====================

    /// <summary>
    /// 和原版 <see cref="StrengthPower"/> 完全同构：只对"自己造成的、受能力加成的攻击伤害"生效。
    /// 因此"禁止明火"的自伤（Unpowered/Unblockable）不会吃到这个加成。
    /// </summary>
    public override decimal ModifyDamageAdditive(
        Creature? target, decimal amount, ValueProp props, Creature? dealer,
        CardModel? cardSource, CardPlay? cardPlay)
    {
        if (base.Owner != dealer)
        {
            return 0m;
        }

        if (!props.IsPoweredAttack())
        {
            return 0m;
        }

        return Depth >= DepthConstants.AttackBonusMinDepth ? DepthConstants.AttackBonusDamage : 0m;
    }

    // ===================== 回合时机 =====================

    /// <summary>
    /// 回合开始：<b>只负责"死亡计数"和锁定本回合的豁免快照</b>，不再结算任何掉血。
    ///
    /// <para>⚠️ 2026-10-04 平衡调整后，两条层数掉血（20 层无沙地兽 -6、51 层额外 -7）
    /// <b>全部搬到了 <see cref="AfterSideTurnEnd"/></b>（回合结束 + 可格挡）。
    /// 别在这里再加掉血逻辑 —— 那会绕过"回合结束"这条口径，
    /// 变成玩家本回合的格挡还没机会吸收就先掉血。</para>
    /// </summary>
    public override async Task AfterPlayerTurnStart(PlayerChoiceContext choiceContext, Player player)
    {
        // ★★ 2026-10-13 联机适配：**这个钩子会按"每一个玩家的回合"对场上每一个监听者各跑一遍**
        //    （`Hook.AfterPlayerTurnStart` 把同一个 `player` 实参喂给全部战斗内监听者）。
        //    所以必须自己判"这个回合是不是我的"，否则 2 人局里会同时坏三件事：
        //      ① `data.DeathStreak` 在**队友的回合**也 +1 ⇒ 死亡线（连续 3 回合 >100 层）
        //         按玩家人数倍速逼近；
        //      ② `data.SuppressedThisTurn`（「探照灯」豁免的锁存值）被**队友的回合**覆盖掉
        //         ⇒ 我本回合的豁免当场失效，回合结束就白挨那两笔层数掉血；
        //      ③ 最要命的：下面的 `CreatureCmd.Kill(player.Creature, force: true)` 用的是
        //         **钩子实参** ⇒ 会把**队友**直接杀死（force = 无视免死）。
        //    同型判据见 `AmmoPower.cs:214` / `AmmoLedgerPower.cs:114` / `FeranmutsPower.cs:142`。
        if ((base.Owner?.Player ?? base.Owner?.PetOwner) is not Player owner || owner != player)
        {
            return;
        }

        var data = GetInternalData<Data>();

        // 「探照灯」：本回合豁免全部层数负面效果（用户 2026-10-03 拍板 Q8 = 五条全免）。
        // 这里一刀切掉"死亡计数"；两条掉血在回合结束钩子里读下面的锁存值，
        // 沙地兽那两条在 SandBeast / BeastCmd 里各判一次。
        // ⚠️ 死亡计数也必须跳过 —— 如果只是不掉血却照样累加，保护期一结束就直接暴毙。
        //
        // ⚠️ 2026-10-04：判定结果**锁存**进 data.SuppressedThisTurn，供回合结束那两条用。
        //    必须在这里锁存、不能等到回合结束再查 —— 理由见 Data.SuppressedThisTurn 的注释。
        //    本方法后续的 early return 也一并改用锁存值，保证整回合只有一个判据。
        data.SuppressedThisTurn = DepthPenalty.IsSuppressed(player);

        if (data.SuppressedThisTurn)
        {
            Log.Info($"[DepthPower] 探照灯生效：本回合跳过全部层数负面效果" +
                     $"（depth={data.Depth}，死亡计数冻结在 {data.DeathStreak}）");
            return;
        }

        // 死亡计数：连续 N 回合超过 100 层直接死。
        if (data.Depth > DepthConstants.DeathDepth)
        {
            data.DeathStreak++;
        }
        else
        {
            data.DeathStreak = 0;
        }

        if (data.DeathStreak >= DepthConstants.DeathConsecutiveTurns)
        {
            Log.Info($"[DepthPower] depth stayed above {DepthConstants.DeathDepth} for " +
                     $"{data.DeathStreak} turns — killing the player.");
            await CreatureCmd.Kill(player.Creature, force: true);
        }

        // ⚠️ 这里**不再**调用任何掉血：两条层数掉血已全部移到 AfterSideTurnEnd。
        //    详见上面 <see cref="ApplyEndOfTurnDepthDamage"/> 的说明。
    }

    /// <summary>
    /// <b>回合结束</b>结算层数掉血 —— <b>现在两条都走这里</b>：
    /// <list type="bullet">
    /// <item>20 层起 + <b>没有沙地兽</b>：<see cref="DepthConstants.NoMinionSelfDamage"/>（6）</item>
    /// <item>51 层起：再 <see cref="DepthConstants.HighDepthSelfDamage"/>（7），<b>与上一条叠加</b>。
    /// 51 层必然也 ≥20 层，所以两条会同时命中 —— 无沙地兽时一回合共 <b>13</b> 点，
    /// 有沙地兽时只有后一条 7 点。</item>
    /// </list>
    ///
    /// <para><b>2026-10-04 平衡调整（用户拍板）。两条都做了同样两处改动：</b></para>
    /// <list type="number">
    /// <item><b>时机：回合开始 → 回合结束</b>。旧版在 <c>AfterPlayerTurnStart</c> 触发，
    /// 且（见下条）本来就是不可格挡的 —— 两件事叠加，这些伤害必然直接落到血量上，
    /// 格挡完全参与不了。挪到回合结束后，你本回合打出的格挡<b>一定还在场</b>
    /// （格挡要到下个回合开始才清，不会在回合结束时消失），于是能顶掉它。</item>
    /// <item><b>性质：不可格挡 → 可格挡</b>。损伤道具从 <c>DamageProps.nonCardHpLoss</c>
    /// （<c>Unblockable | Unpowered</c>）换成 <c>DamageProps.nonCardUnpowered</c>（<c>Unpowered</c>）——
    /// 原版对后者的定义就是"非卡牌/非怪物行动造成的、<b>但仍然应该可格挡</b>的伤害，
    /// 典型用途是荆棘这类伤害型 Power"。</item>
    /// </list>
    ///
    /// <para>⚠️ <b>两处必须一起改</b>：只挪时机、不改可格挡性的话，格挡照样吸收不了，
    /// 等于"晚几秒掉血"，强度几乎不变 —— 那就没达成"开局太弱需要加强"的调整目的。</para>
    ///
    /// <para>另外 51 层那条的<b>数值由 5 提到 7</b>：它是<b>无条件</b>生效的（不看有没有沙地兽），
    /// 可格挡化之后需要数值补偿，否则高层压力会掉得太厉害。叠加总和因此由 11 变 <b>13</b>。</para>
    ///
    /// <para><b>阈值语义不变</b>：20 层起<b>永久生效</b>，不是 20-29 专属
    /// （<see cref="DepthConstants.PotionTierMinDepth"/>），<b>也没有上界</b>。</para>
    ///
    /// <para>⚠️ <b>豁免判定读的是回合开始时锁存的快照</b>（<c>Data.SuppressedThisTurn</c>），
    /// <b>不能</b>在这里现场调 <see cref="DepthPenalty.IsSuppressed"/> ——
    /// 「探照灯」也在回合结束消耗自己，现场查会读到已被摘掉的状态，导致保护失效且静默无错。
    /// 详见 <c>Data.SuppressedThisTurn</c> 的注释。</para>
    /// </summary>
    public override async Task AfterSideTurnEnd(
        PlayerChoiceContext choiceContext, CombatSide side, IEnumerable<Creature> participants)
    {
        // 和 SandBeastFuryPower / SearchlightPower 同构：本钩子对"双方"的回合结束各广播一次，
        // 先认出"这次结束的是不是我自己这边"。
        if (base.Owner is not { } owner || !participants.Contains(owner))
        {
            return;
        }

        // ⚠️ 同 AmmoPower.SyncFromData 那条坑：玩家 Creature 上 PetOwner 恒为 null，
        //    必须写成 Owner.Player ?? Owner.PetOwner。
        if ((owner.Player ?? owner.PetOwner) is not Player player)
        {
            return;
        }

        var data = GetInternalData<Data>();

        // 探照灯：本回合豁免（用回合开始锁存的值，见方法注释的警告）。
        if (data.SuppressedThisTurn)
        {
            Log.Info($"[DepthPower] 探照灯生效：跳过回合结束的层数掉血（depth={data.Depth}）");
            return;
        }

        await ApplyEndOfTurnDepthDamage(choiceContext, player, data.Depth);
    }

    /// <summary>
    /// 回合结束的两条层数掉血。两条<b>都可格挡</b>，都只由 <see cref="AfterSideTurnEnd"/> 调用。
    ///
    /// <para>⚠️ <b>不要在 <c>AfterPlayerTurnStart</c> 里再调用本方法</b> —— 那会把两条又拉回
    /// "回合开始 + 不可格挡"，等于把本次平衡改回去。</para>
    ///
    /// <para>两条用<b>各自独立的 <c>CreatureCmd.Damage</c> 调用</b>（没有合并成一次 13 点）：
    /// 保留各自的日志和"单次伤害次数"语义，结构与改造前一致，方便对着日志逐条核对。</para>
    ///
    /// <para>choiceContext 一路透传（而不是传 null），因为自伤也可能触发需要玩家选择的钩子。</para>
    /// </summary>
    private async Task ApplyEndOfTurnDepthDamage(PlayerChoiceContext choiceContext, Player player, int depth)
    {
        // 20 层起 + 无沙地兽：-6（可格挡）。注意**没有上界**（不是 20-29 专属）。
        if (depth >= DepthConstants.PotionTierMinDepth && MinionCount(player) == 0)
        {
            await CreatureCmd.Damage(choiceContext, player.Creature,
                DepthConstants.NoMinionSelfDamage, DamageProps.nonCardUnpowered, null, null, null);

            Log.Info($"[DepthPower] depth={depth} 无沙地兽 -> 回合结束 -{DepthConstants.NoMinionSelfDamage} HP（可格挡）");
        }

        // 51 层起：额外 -7（可格挡），与上一条叠加。
        if (depth > DepthConstants.NoSummonDepth)
        {
            await CreatureCmd.Damage(choiceContext, player.Creature,
                DepthConstants.HighDepthSelfDamage, DamageProps.nonCardUnpowered, null, null, null);

            Log.Info($"[DepthPower] depth={depth} >{DepthConstants.NoSummonDepth} -> 回合结束额外 -{DepthConstants.HighDepthSelfDamage} HP（可格挡）");
        }
    }

    // ===================== 战斗结算 =====================

    public override async Task BeforeCombatStart()
    {
        var data = GetInternalData<Data>();
        data.DeathStreak = 0;
        data.PotionTierGranted = false;

        // ★ 2026-10-11：本场"曾到过金币档"的记账也要复位（主复位点在 DepthState.ResetForCombat，
        // 这里是与 PotionTierGranted 同源的兜底 —— 保证第 2 场及以后的战斗不会残留上一场的记账）。
        try
        {
            if ((base.Owner?.Player ?? base.Owner?.PetOwner) is Player player)
            {
                DepthRunData.Store.Get(player).GoldTierReachedThisCombat = false;
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[DepthPower] 复位金币档记账失败（本场可能少拿金币）: {e}");
        }

        // 注意：层数归零不在这里做。
        // 本方法只对"第 2 场及以后的战斗"生效 —— 第 1 场战斗时这个 Power 还没被创建
        // （原版战斗结束会清空所有 Power）。真正的重置点在 DepthCmd.EnsurePower：
        // 那里以"Power 不存在"作为"本场战斗第一次"的判据，两场都能覆盖。
        await Task.CompletedTask;
    }

    public override async Task AfterCombatEnd(CombatRoom room)
    {
        var data = GetInternalData<Data>();

        // ---- 临时诊断日志（确认金币档位是否真的走到这里）----
        Log.Info($"[DepthPower] AfterCombatEnd 触发：depth={data.Depth} " +
                 $"TurnNumber={base.Owner?.Player?.PlayerCombatState?.TurnNumber.ToString() ?? "n/a"} " +
                 $"Owner={base.Owner?.GetType().Name ?? "null"} " +
                 $"Player={base.Owner?.Player?.GetType().Name ?? "null"}");

        // ⚠️ 同 OnDepthTierChanged：玩家 Creature 上 PetOwner 为 null，必须带上 Player。
        // 早先只判 PetOwner，导致 30 档金币 / 60 档遗物从来没发放过。
        if ((base.Owner?.Player ?? base.Owner?.PetOwner) is not Player player)
        {
            Log.Warn("[DepthPower] AfterCombatEnd：解析不出 Player，跳过金币/遗物");
            return;
        }

        var state = DepthRunData.Store.Get(player);

        // 30-60 层：按本场战斗的回合数给金币。
        // ⚠️ 必须**同时**判上界：早先只判下界，导致 61 层以上还在发金币，
        // 与"大于 60 层改为给遗物"的设计重叠（用户报的"高层丢失低层效果"）。
        //
        // ★ 2026-10-11：判定从"**结束时的层数快照在档内**"改成
        //   "**结束在档内 或 本场曾到过金币档**"（后者由 OnDepthTierChanged 的区间扫描记账）。
        //
        //   为什么必须这么改：只看结束快照的话，任何"一步跨过整档"的写入都吃不到金币 ——
        //   灵异录像带直接设到 61+、「这是对的！」翻到 30~38 之后又越过 60、
        //   「积累」把 63 层带进新战斗（开局 0→63），结束快照全在档外。
        //
        //   两者是**或**关系，且共用同一个发放体，所以一次战斗**只发一次**
        //   （不是两个分支各发一次）。
        bool endsInGoldTier = data.Depth >= DepthConstants.GoldTierMinDepth
                              && data.Depth <= DepthConstants.GoldTierMaxDepth;
        bool reachedGoldTier = endsInGoldTier || state.GoldTierReachedThisCombat;

        if (reachedGoldTier)
        {
            int turns = player.PlayerCombatState?.TurnNumber ?? 0;
            decimal gold = turns * DepthConstants.GoldPerTurn;

            if (gold > 0m)
            {
                await PlayerCmd.GainGold(gold, player);

                if (state.GoldTierReachedThisCombat && !endsInGoldTier)
                {
                    // 判据日志：跳档补发的情形（结束时已经不在档内，靠"曾到过"发钱）。
                    Log.Info($"[DepthPower] 金币档：本场曾到过 30-60（结束层数 {data.Depth}）" +
                             $"→ 发 {gold} 金币（{turns} 回合 × 10）");
                }
                else
                {
                    Log.Info($"[DepthPower] 30-60 档金币：depth={data.Depth} turns={turns} -> 发放 {gold} 金币");
                }
            }
            else
            {
                Log.Info($"[DepthPower] 30-60 档金币：turns=0，跳过");
            }
        }

        // 61 层起：金币档已结束，改走遗物档。
        // ⚠️ 2026-10-11：这里原先是 `else if (data.Depth > MaxDepth)` —— 一个**不给**的分支。
        // 新逻辑下"结束在 61+ 但本场曾到过金币档"照样要发钱，两者若还是 else if 互斥，
        // 就会出现"发了钱却打日志说本场不再给金币"的自相矛盾；而若把它改成无条件的裸日志，
        // 又会对"曾到过但已发钱"的情形说错话。所以这里降级为**纯说明性日志**，
        // 只在"确实没发钱"（即本场也从未到过金币档）时才打：它现在表达的是
        // "金币档整个没参与本场结算"，不是"到高层就不给低层收益了"。
        // 金币与遗物的互斥关系不受影响：遗物档是紧随其后的独立 if（本来就不是 else if）。
        else if (!state.GoldTierReachedThisCombat)
        {
            Log.Info($"[DepthPower] depth={data.Depth} 已超过金币档上界 " +
                     $"{DepthConstants.GoldTierMaxDepth}，本场金币档未参与（走遗物档）");
        }

        // 大于 60 层：每场战斗一次，50% 概率给一个遗物。
        if (data.Depth > DepthConstants.RelicChanceDepth && !state.RelicGrantedThisCombat)
        {
            state.RelicGrantedThisCombat = true;

            // ★★★ 2026-10-13 联机适配（**这条以前是"直接断线级"的 bug**）：
            //   原来是 `Rng.Chaotic.NextDouble()`。官方对 Chaotic 的定义是
            //   （`MegaCrit.Sts2.Core.Random\Rng.cs:20-25`）：
            //     "A non-deterministic RNG instance… **Good for when we need to
            //      randomize things that don't impact gameplay.**"
            //   —— 种子 = `DateTimeOffset.Now.ToUnixTimeSeconds()`，即**每台机器都不一样**。
            //
            //   而这次掷骰**恰恰影响游戏状态**：命中就置 `PendingRelicReward` ⇒ 奖励界面里
            //   多一个遗物（见 `MineRelic.TryModifyRewards`）。而联机是**确定性锁步 +
            //   周期校验和**：遗物列表和每玩家的奖励序号（`nextRewardIds`）都在
            //   `NetFullCombatState` 里被哈希 ⇒ 两端一命中一未命中 = 立刻
            //   `State divergence detected!` ⇒ 房主 `DisconnectClient(..., StateDivergence)`
            //   把客机踢回主菜单（`ChecksumTracker.cs:203-215` / `:151`）。
            //
            //   改法：用 "run 种子 + 玩家槽位 + 本 Power 的 Id + 本层数" 播种（官方推荐的
            //   `Rng(Player, ModelId, mixin)` 形式，`Rng.cs:38-52`）。三个好处：
            //     ① **确定性**：同一场战斗两端算出同一个 roll ⇒ 不会再分歧；
            //     ② **每场战斗独立**：mixin 取 `TotalFloor`（整局的层数），不然同一个种子
            //        永远掷出同一个值 ⇒ 一把里"要么每场都有遗物、要么一场都没有"；
            //     ③ **不消耗任何共享随机流** ⇒ 不会把原版/别的卡的随机序列推歪。
            var rng = new Rng(player, Id, (ulong)player.RunState.TotalFloor);
            double roll = rng.NextDouble();

            // ⚠️ 这条日志是 2026-10-03 补的。此前"遗物档"只在 **catch 失败**时打日志，
            //    成功和未命中都是静默的 —— 于是"到底有没有走到这一档、50% 有没有掷中"
            //    完全查不到（用户报"大于 61 层的遗物从来没出现过"，而日志里一行都没有）。
            //    另一条同样重要的信息：原版 RelicFactory 在**遗物池抽空**时会兜底给
            //    原版遗物 Circlet（就是那个"所有遗物都拿完了"的填充物），
            //    这条日志会把它标出来 —— 否则玩家只会看到一堆莫名其妙的 Circlet。
            Log.Info($"[DepthPower] 遗物档：depth={data.Depth}（门槛 >{DepthConstants.RelicChanceDepth}）" +
                     $" roll={roll:F3} 需 <{DepthConstants.RelicChance:P0} -> " +
                     $"{(roll < DepthConstants.RelicChance ? "命中" : "未命中（本场不再掷）")}");

            if (roll < DepthConstants.RelicChance)
            {
                // 掷中了：**不在这里直接发**，只置一个"待发放"标志。
                //
                // 2026-10-04 改：原来是在这里直接 RelicCmd.Obtain 塞进遗物栏 ——
                // 玩家只看到一闪而过的动画，没有任何"我拿到了什么"的确认，
                // 用户实测第一反应就是"遗物是坏的"。现在改成在**战斗奖励界面**里
                // 出现一个遗物奖励项（可见、可跳过），由 MineRelic.TryModifyRewards 加进去。
                state.PendingRelicReward = true;

                Log.Info("[DepthPower] 遗物档：命中 → 已标记待发放，会出现在本场战斗的奖励界面里");
            }
        }
    }

    // ===================== 沙地兽计数 =====================

    /// <summary>
    /// 当前沙地兽数量。
    ///
    /// <para>2026-10-03：沙地兽已经落地（<c>RayMod.Beasts</c>），这里从"硬编码 0"
    /// 换成真实计数。数的是<b>场上活着的沙地兽宠物</b>，所以
    /// "20 层起没有沙地兽时每回合 -6HP" 从此<b>精确</b>生效 ——
    /// 之前恒为 0，那条减益是无条件触发的（当时的预期行为，方便单独验证层数）。</para>
    /// </summary>
    private static int MinionCount(Player player)
    {
        return Beasts.BeastCmd.GetCount(player);
    }
}
