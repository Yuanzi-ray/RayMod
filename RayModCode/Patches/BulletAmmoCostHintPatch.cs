using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using RayMod.Powers;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名（同 CardPartArtPatch.cs）。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;
using LogType = MegaCrit.Sts2.Core.Logging.LogType;

namespace RayMod.Patches;

/// <summary>
/// ★ 2026-10-11：<b>子弹模式下，把"这张牌要消耗多少弹药"补在卡面描述最后一行。</b>
///
/// <para><b>为什么需要</b>（用户反馈原话）："子弹模式下所有牌费用都变成零，消耗等于能量的子弹。
/// 但这样的话卡面上就看不到它具体要消耗多少弹药了"。
/// 根因是 <c>BulletModePower.TryModifyEnergyCostInCombat</c> 把费用<b>物理改成 0</b>
/// （走 <c>CostModifiers.Global</c>，所以卡面那个费用圈显示的就是 0），
/// 而真正的价格在弹药上 —— 卡面没有任何地方写它。</para>
///
/// <para><b>★★★ 2026-10-12：为什么改挂【私有 3 参重载】而不是那个 public 的 2 参重载</b>
/// （这是本条功能"实现了却完全不生效"的真因，见<b>铁律 73</b>）：<br/>
/// 2 参那个重载整个方法体只有一行转发：</para>
/// <code>
/// public string GetDescriptionForPile(PileType pileType, Creature? target = null)
///     => GetDescriptionForPile(pileType, DescriptionPreviewType.None, target);   // 3 行 IL
/// </code>
/// <para>这种小方法会被 <b>JIT 内联</b>进调用方（<c>NCard.UpdateVisuals</c>，<c>NCard.cs:888</c>）——
/// 调用点里留下的是**原始 IL**，于是 <b>Harmony 的 detour 被绕过去</b>：
/// 补丁确实"挂上了"（<c>Harmony.GetPatchInfo</c> 能查到），但**永远不会被执行**，
/// 日志里一个字都没有。实测三件证据：① 自检显示该目标 <c>post 1</c>；
/// ② 同一局打了 17 张牌、卡面正常渲染；③ 补丁里的诊断日志 <b>0 条</b>。</para>
///
/// <para>⇒ 改挂那个**又大又不会被内联**的私有 3 参重载
/// （和 RitsuLib 的 <c>CardDescriptionPatchTarget</c> 同一个目标，人家一直是对的）。
/// 它是<b>所有</b>描述路径的唯一实现：2 参重载 → 它；<c>GetDescriptionForUpgradePreview</c> → 它；
/// <c>NCardLibrary:999</c>（卡组/图鉴那段纯文本）→ 2 参 → 它。
/// ⚠️ 因为它是 <b>private</b>、而且第二个参数是 <b>private 嵌套枚举</b> <c>CardModel.DescriptionPreviewType</c>
/// （成员就是 <c>None</c> / <c>Upgrade</c>），编译期拿不到类型 ⇒ 只能用
/// <c>[HarmonyPrepare]</c> + <c>[HarmonyTargetMethod]</c> 反射解析（本工程 <c>CardPlayVfxPatch</c> 同款）。</para>
///
/// <para><b>为什么还接一行字符串</b>：3 参重载返回的就是"整段富文本 + <c>\n</c> 分行"的成品
/// （<c>CardModel.cs:1403-1440</c> 用 <c>string.Join('\n', list.Where(非空))</c> 拼的，里面已带
/// <c>[gold]</c>/<c>{0}</c> 之类富文本）⇒ <b>接一行 = 直接拼 <c>"\n" + 新行</c></b>，
/// 不需要碰 <c>LocString</c>，也不改原逻辑。</para>
///
/// <para><b>升级预览不显示是【有意】的</b>：<c>previewType == Upgrade</c> 时直接跳过（那时牌还没进战斗费用语境）。
/// ⚠️ 它是私有枚举，判据只能 <c>ToString() == "Upgrade"</c>（RitsuLib 的 <c>IsUpgradePreview</c> 同款写法）。</para>
///
/// <para><b>刷新时机不用管</b>：打出「子弹模式」= 施加 Power →
/// <c>CombatStateTracker.NotifyCombatStateChanged</c> → 手牌逐张 <c>NCard.UpdateVisuals</c>
/// ⇒ 当回合手牌自动重画，本条无需任何轮询/订阅。</para>
///
/// <para>文案在 <c>localization\{zhs,eng}\card_keywords.json</c>（<c>RAY_KW_BULLET_AMMO_COST*</c>），
/// 与 <see cref="CardConceptHoverTipPatch"/> <b>同一张表、同一套 merge 机制</b>。
/// 数字走 SmartFormat 具名占位符 <c>{Amount}</c>（原版 <c>REPLAY.extraText</c> 的 <c>{Times}</c> 就是这个写法），
/// 句式留在文案里，中英各一条。</para>
/// </summary>
[HarmonyPatch]
internal static class BulletAmmoCostHintPatch
{
    /// <summary>出问题时的总开关（照 <see cref="CardTagIconOverlay.Enabled"/> 的做法）。</summary>
    internal const bool Enabled = true;

    /// <summary>本地化表名（与 <see cref="CardConceptHoverTipPatch"/> 同一张）。</summary>
    private const string Table = "card_keywords";

    /// <summary>普通牌：<c>消耗{Amount}点弹药。</c></summary>
    private const string KeyCost = "RAY_KW_BULLET_AMMO_COST.description";

    /// <summary>X 费牌：<c>消耗全部弹药（当前{Amount}）。</c></summary>
    private const string KeyCostX = "RAY_KW_BULLET_AMMO_COST_X.description";

    private static readonly Logger Log = new("RayMod.Patches", LogType.Generic);

    private static bool _loggedFailure;

    /// <summary>诊断用（2026-10-12）：已经报过的卡 id —— 每个卡只报一次。</summary>
    private static readonly System.Collections.Generic.HashSet<string> _diagDone = new();

    /// <summary>还能报几条"战斗内的卡"（★ 不用 const：`const` 会让下面的预算判断变成"不可达代码"警告）。</summary>
    private static int _diagBudget = 16;

    /// <summary>还能报几条"不是战斗中的卡"（只是用来证明"补丁确实被调用了"，所以给很少）。</summary>
    private static int _diagOutOfCombat = 3;

    private static MethodBase? _target;

    /// <summary>解析目标：私有 3 参重载（见类注释）。拿不到就 <c>false</c> ⇒ Harmony 跳过本类。</summary>
    [HarmonyPrepare]
    private static bool Prepare()
    {
        try
        {
            Type? previewType = typeof(CardModel).GetNestedType("DescriptionPreviewType", BindingFlags.NonPublic);

            if (previewType == null)
            {
                Log.Warn("[子弹模式] 找不到 CardModel.DescriptionPreviewType ⇒ 卡面弹药提示不启用" +
                         "（游戏改版？看 Entry 的 [补丁自检]）");
                return false;
            }

            _target = AccessTools.Method(typeof(CardModel), "GetDescriptionForPile",
                                         new[] { typeof(PileType), previewType, typeof(Creature) });

            if (_target == null)
            {
                Log.Warn("[子弹模式] 找不到 CardModel.GetDescriptionForPile(PileType, DescriptionPreviewType, Creature)" +
                         " ⇒ 卡面弹药提示不启用");
                return false;
            }

            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"[子弹模式] 目标解析失败（卡面弹药提示不启用）：{e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    [HarmonyTargetMethod]
    private static MethodBase? TargetMethod() => _target;

    /// <summary>
    /// <c>__args[1]</c> = 那个私有枚举 <c>DescriptionPreviewType</c>（<c>None</c> / <c>Upgrade</c>）。
    /// </summary>
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, object[] __args, ref string __result)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            // 升级预览里不显示（有意，见类注释）
            if (__args != null && __args.Length > 1 && !IsNormalPreview(__args[1]))
            {
                return;
            }

            string? line = BuildHintLine(__instance, out string why);

            if (line != null)
            {
                __result = string.IsNullOrEmpty(__result) ? line : __result + "\n" + line;
            }

            DiagOnce(__instance, why, line);
        }
        catch (Exception e)
        {
            // 补丁抛异常会污染整条卡面渲染链路：只记一次，绝不再抛。
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                Log.Error($"[子弹模式] BulletAmmoCostHintPatch 失败（只记这一次）：{e}");
            }
        }
    }

    /// <summary>私有枚举的判据只能这么写（RitsuLib 的 <c>IsUpgradePreview</c> 同款）：非 "Upgrade" 就是正常预览。</summary>
    private static bool IsNormalPreview(object? previewType) =>
        !string.Equals(previewType?.ToString(), "Upgrade", StringComparison.Ordinal);

    /// <summary>
    /// 算出"要追加的那一行"；返回 <c>null</c> = 这次不追加。
    /// <paramref name="why"/> 把**走到哪一条判据就停下了**写清楚（只进诊断日志）。
    /// </summary>
    private static string? BuildHintLine(CardModel card, out string why)
    {
        // ── 判据 ①：这是一张"在战斗中、有牌主"的卡 ───────────────────────────
        // CombatState 的 getter 是"Pile 是战斗牌堆 → 牌主的 Creature.CombatState"
        // （CardModel.cs:1018-1029）：卡组 / 图鉴 / 奖励里的牌为 null。
        // ⚠️ 顺序关键：必须**先**判它再碰 card.Owner —— Owner 的 getter 里是
        //    AssertMutable()（AbstractModel.cs:122），图鉴里的 canonical 卡一碰就抛
        //    CanonicalModelException（它连 _owner 都是 null，所以 CombatState 必然是 null）。
        if (card.CombatState == null)
        {
            why = "① CombatState=null（不是战斗中的牌：卡组/图鉴/奖励）";
            return null;
        }

        Player? owner = card.Owner;

        if (owner == null)
        {
            why = "② Owner=null";
            return null;
        }

        // ── 判据 ②：这个牌主身上挂着「子弹模式」──────────────────────────────
        // 本工程已有的 16 处同款判据（GetPower<T>() != null）。
        BulletModePower? power = owner.Creature.GetPower<BulletModePower>();

        if (power == null)
        {
            why = "③ 牌主身上没有「子弹模式」";
            return null;
        }

        // ── 判据 ③：这张牌**确实要花弹药** ──────────────────────────────────
        // 0 费牌 / 弹药为空的 X 费牌 → false → 不追加（"消耗0点弹药"是纯噪音；
        // ShouldPlay 对 cost<=0 直接放行，对空弹药的 X 费牌也直接否决）。
        // ⚠️ 绝不在这里自己算：公式的唯一出处是 BulletModePower.AmmoCostOf，
        //    X 费牌由 TryGetAmmoCost 走"当前弹药"那条（否则会显示 0，与实际"全扣"相反）。
        if (!power.TryGetAmmoCost(card, out int amount, out bool isX))
        {
            why = "④ TryGetAmmoCost=false（这张牌现在不用花弹药）";
            return null;
        }

        string key = isX ? KeyCostX : KeyCost;

        // 表里没这条键就静默跳过（例如只换了 dll 没换 pck）：LocString.GetRawText 对缺键
        // 是**抛 LocException**（LocTable.cs:44-55），别让它进异常日志。
        if (!LocString.Exists(Table, key))
        {
            why = $"⑤ 本地化表 {Table} 里没有键 {key}";
            return null;
        }

        var hint = new LocString(Table, key);

        // 具名变量（不是 {0}）：LocManager.SmartFormat 走 SmartFormat，
        // 原版就是 Add("Times", …) 这么用的（CardModel.cs:1432-1434）。
        hint.Add("Amount", amount);

        string line = hint.GetFormattedText();

        if (string.IsNullOrWhiteSpace(line))
        {
            why = "⑥ 文案渲染成空串";
            return null;
        }

        why = $"✅ 追加（弹药 {amount}{(isX ? "，X 费" : string.Empty)}）";
        return line;
    }

    /// <summary>
    /// ★ 诊断（2026-10-12）：用来一次实机就分清两件事 ——
    /// ① <b>补丁到底有没有被调用</b>（日志里一条 <c>[子弹模式·卡面]</c> 都没有 ⇒ 补丁没被执行，
    ///    先去看 <c>Entry</c> 的 <c>[补丁自检]</c>，再怀疑**目标方法被 JIT 内联**，见铁律 73）；
    /// ② <b>走到了哪一条判据</b>（<c>why</c> 直接写清楚了）。
    ///
    /// <para>预算分两档：战斗内的卡最多 16 条（每个卡 id 一条），不是战斗中的卡最多 3 条 ——
    /// 否则卡组/奖励界面那些 <c>CombatState=null</c> 的渲染会先把预算吃光。</para>
    /// </summary>
    private static void DiagOnce(CardModel card, string why, string? line)
    {
        // ① 这一类是"补丁被调用了"的凭据，但信息量低 ⇒ 只给 3 条
        if (why.StartsWith("①", StringComparison.Ordinal))
        {
            if (_diagOutOfCombat <= 0)
            {
                return;
            }

            _diagOutOfCombat--;
        }
        else
        {
            if (_diagBudget <= 0)
            {
                return;
            }

            _diagBudget--;
        }

        string id;

        try
        {
            id = card.Id.Entry;
        }
        catch (Exception)
        {
            id = card.GetType().Name;
        }

        if (!_diagDone.Add(id))
        {
            return;
        }

        Log.Info($"[子弹模式·卡面] {id}（{card.GetType().Name}）：{why}" +
                 (line == null ? string.Empty : $" ⇒ 「{line.Replace("\n", " ")}」"));
    }
}
