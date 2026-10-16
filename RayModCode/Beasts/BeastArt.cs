using Godot;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Logging;
// 2026-10-09：收退场节点要用房间自己的登记表接口 + 它的节点类型 NCreature。
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Beasts;

/// <summary>
/// 沙地兽的<b>形象</b>：优先用**跟随皮肤的 Spine 模型**，没有配对模型时回退到 30 帧 sprite。
///
/// <para><b>为什么是"运行时注入"而不是"换成自己的 creature_visuals 场景"</b>（2026-10-04 的结论，仍然成立）：
/// 沙地兽是 MinionLib 的 <c>MinionModel</c>，而 RitsuLib 给模组怪物换形象那条路是按"资源档案"登记的，
/// 我们没走那套；原版 <c>MonsterModel.CreateVisuals()</c> 要 <c>Instantiate&lt;NCreatureVisuals&gt;()</c>，
/// 自己写的普通场景根节点会被它静默拒掉。所以改成运行时往兜底场景的形象节点上挂东西 —— 只依赖公开 API。</para>
///
/// <para><b>★ 2026-10-07：加了 Spine 路线（用户反馈"沙地兽不配套"）。</b>
/// 三套皮肤的沙地兽资源：夏日/闪耀各有一套明日方舟 token 骨骼
/// （<c>token_ray_sndbst_summer/shining_data.tres</c>，实测各含 3 个动画 <c>Default/Idle/Start</c>），
/// <b>原始莱伊没有配对模型</b> → 那套皮肤下继续用 30 帧 sprite（<see cref="RaySkinCatalog"/> 里 <c>BeastSkeletonDataPath = null</c>）。
/// 卡片上那 92 张卡图不受影响（铁律：沙地兽的两种形象共存是用户拍板的）。</para>
///
/// <para>⚠️ <b><c>ZIndex</c> 必须留 0</b>（帧动画与 Spine 两条路都一样）：
/// Godot 里 <c>CanvasItem.ZIndex</c> 非 0 会<b>压过 Y 排序</b>，战斗场地是按 y 坐标排前后遮挡的，
/// 给个 1 就等于把沙地兽从排序里摘出来、永远画在最上层（2026-10-06 用户报过"图层有问题，貌似是最高的"）。</para>
/// </summary>
internal static class BeastArt
{
    private static readonly Logger Log = new("RayMod.Beasts", LogType.Generic);

    /// <summary>帧动画节点名（也是"这条兽已经画过"的幂等判据）。</summary>
    private const string FrameNodeName = "SandBeastArt";

    /// <summary>Spine 形象节点名。和帧动画分开命名，方便日志里区分走的哪条路。</summary>
    private const string SpineNodeName = "SandBeastSpine";

    private const string FramesPath = "res://RayMod/scenes/creature_visuals/sand_beast_frames.tres";
    private const string AnimationName = "Idle";

    /// <summary>
    /// Spine 形象的缩放。
    ///
    /// <para>2026-10-07 实机反馈<b>太大</b>：<c>1.0</c> 时沙地兽几乎和角色上半身一样大（用户截图确认），
    /// 所以降到 <c>0.5</c>（约等于原帧动画 <c>1.5</c> 的观感，屏幕上约 110px 宽）。
    /// <b>实机若还不合适，只改这一个数</b>（Spine 原点通常在脚底，不用像帧动画那样算包围盒）。</para>
    /// </summary>
    private const float SpineScale = 0.5f;

    // ── 帧动画那条路的画布与实测包围盒（256×256 画布，30 帧并集）──
    private const float CanvasCenter = 128f;
    private const float ArtBBoxLeft = 98f;
    private const float ArtBBoxRight = 150f;
    private const float ArtBBoxBottom = 205f;
    private const float ArtScale = 1.5f;

    /// <summary>脚底锚点（<c>y = 0</c> 就是地面线，原版兜底场景的 Bounds 底边也在 y≈0）。</summary>
    private static readonly Vector2 FeetAnchor = new(1f, 0f);

    private static SpriteFrames? _frames;
    private static bool _loggedFailure;

    /// <summary>印记层数徽章节点名（挂在兽脚下，显示"这只兽现在提供了几层增伤"）。</summary>
    private const string BadgeNodeName = "SandBeastMarkBadge";

    /// <summary>徽章相对**脚底锚点**的纵向偏移（Godot 的 Y 向下，所以正数 = 往下）。</summary>
    private const float BadgeOffsetY = 6f;

    /// <summary>
    /// ★ 2026-10-09：<b>把退场沙地兽的节点立刻收掉</b>（用户报的"大量替换时有残影"）。
    ///
    /// <para><b>为什么必须我们自己收</b>（反编译实证）：
    /// <c>CreatureCmd.Kill</c> 里是
    /// <c>nCreature.StartDeathAnim(shouldRemoveFromCombat &amp;&amp; creature.IsMonster)</c>，
    /// 而 <c>AnimDie(shouldRemove)</c> **只有 `shouldRemove=true` 才播淡出、才把节点收掉**
    /// （<c>NCreature.cs:1013/1034</c>）；同一方法里 <c>NCombatRoom.RemoveCreatureNode</c>
    /// **也只对 `IsMonster` 调**。
    /// 沙地兽是 <b>Pet</b>（`IsMonster == false`）→ 两条路都不走；
    /// MinionLib 的 <c>MinionKillPatch</c> 更是**只把它从战斗状态里摘掉**
    /// （`CombatManager.RemoveCreature` + `combatState.RemoveCreature`），**完全不碰节点**。
    /// → <b>节点连它的形象一起永远留在场上</b>；一次换 N 只就同时留 N 个 = 残影。</para>
    ///
    /// <para>这里先走游戏自己的 <c>RemoveCreatureNode</c>（从房间登记里摘掉、别留悬空引用），
    /// 再自己 <c>QueueFree</c>。全程吞异常 —— 美术/节点出问题绝不能影响结算。</para>
    /// </summary>
    internal static void RemoveArt(Creature? beast)
    {
        if (beast == null)
        {
            return;
        }

        // ① 找节点。★ 2026-10-09 第二轮：**只问房间登记表不够**。
        //    用户实机反馈"残影还在" —— 那一刻 `GetCreatureNode()` 是拿不到的
        //    （登记表里已经没它了），而原实现遇到 null 就**静默 early-return**，
        //    于是节点谁都不收、残影永远留在场上（日志里一条报错都没有，这就是它骗过上一轮的原因）。
        //    现在：登记表查不到就**扫一遍战斗房间的节点树**兜底。
        NCreature? node = ResolveNode(beast, out string how);

        if (node != null)
        {
            FreeNode(beast, node, how);
        }
        else
        {
            Log.Info($"[沙地兽] 收节点：**没找到**这只兽的节点（{how}）—— 交给孤儿清扫兜底");
        }

        // ② 兜底网：把战场上"已经不在册 / 已经死了"却还挂着的沙地兽节点一并收掉。
        //    这一轮不猜"是哪条路漏的"了 —— 只要它不该在场上，就收（判据很保守，见方法注释）。
        SweepOrphans(beast.PetOwner);
    }

    /// <summary>
    /// 找这只兽的战斗节点：**先问房间登记表，再扫节点树**。
    ///
    /// <para>为什么要扫树：<c>NCombatRoom.GetCreatureNode</c> 只是查 <c>_creatureNodes</c> 这张
    /// <b>登记表</b>（`CreatureNodes.FirstOrDefault(c =&gt; c.Entity == creature)`），
    /// 一旦有别的东西先把登记摘了（游戏自己的清理 / 别的 mod / 我们的删除），
    /// 节点就处于"<b>还在画、但查不到</b>"的状态 —— 这正是上一轮静默漏收的原因。</para>
    /// </summary>
    private static NCreature? ResolveNode(Creature beast, out string how)
    {
        try
        {
            var direct = beast.GetCreatureNode();

            if (direct != null && GodotObject.IsInstanceValid(direct))
            {
                how = "房间登记表";
                return direct;
            }
        }
        catch (Exception e)
        {
            WarnOnce($"[沙地兽] 取节点失败（只记一次）：{e.Message}");
        }

        try
        {
            var room = NCombatRoom.Instance;

            if (room != null)
            {
                var all = new List<NCreature>();
                CollectCreatureNodes(room, all);

                foreach (var nc in all)
                {
                    Creature? entity = null;

                    try
                    {
                        entity = nc.Entity;
                    }
                    catch
                    {
                        // 单个节点读不到就跳过。
                    }

                    if (entity == beast)
                    {
                        how = "扫节点树";
                        return nc;
                    }
                }
            }
        }
        catch (Exception e)
        {
            WarnOnce($"[沙地兽] 扫节点树失败（只记一次）：{e.Message}");
        }

        how = "登记表与节点树里都没有";
        return null;
    }

    /// <summary>深度优先收集战斗房间里的全部 <see cref="NCreature"/>。</summary>
    private static void CollectCreatureNodes(Node root, List<NCreature> into)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is NCreature nc)
            {
                into.Add(nc);
            }

            CollectCreatureNodes(child, into);
        }
    }

    /// <summary>收掉一个节点：先摘房间登记（还在册才摘），再自己 <c>QueueFree</c>。</summary>
    private static void FreeNode(Creature? beast, NCreature node, string how)
    {
        // 形象节点万一被挪到别处（不是它的子孙），单收壳会留下"只有画、没有壳"的残影。
        Node? visuals = null;

        try
        {
            visuals = node.Visuals;
        }
        catch (Exception e)
        {
            WarnOnce($"[沙地兽] 取形象节点失败（只记一次）：{e.Message}");
        }

        bool orphanVisuals = visuals != null
            && GodotObject.IsInstanceValid(visuals)
            && !node.IsAncestorOf(visuals);

        // 还在登记表里才摘 —— 不在表里硬摘会碰到 `_creatureNodes[0]`，白抛一次异常。
        bool registered = false;

        try
        {
            registered = beast != null && NCombatRoom.Instance?.GetCreatureNode(beast) != null;
        }
        catch
        {
            // 读不到就当没登记，下面的释放照样跑。
        }

        if (registered)
        {
            try
            {
                NCombatRoom.Instance?.RemoveCreatureNode(node);
            }
            catch (Exception e)
            {
                WarnOnce($"[沙地兽] 摘房间登记失败（只记一次，仍会释放节点）：{e.Message}");
            }
        }

        // 宠物这条路上**没有任何人会释放它** —— 自己收。
        try
        {
            node.QueueFree();
        }
        catch (Exception e)
        {
            WarnOnce($"[沙地兽] 释放节点失败（只记一次）：{e.Message}");
        }

        if (orphanVisuals)
        {
            try
            {
                visuals!.QueueFree();
            }
            catch (Exception e)
            {
                WarnOnce($"[沙地兽] 释放游离形象失败（只记一次）：{e.Message}");
            }
        }

        Log.Info($"[沙地兽] 收节点：{how}｜已登记={registered}｜形象游离={orphanVisuals}");
    }

    /// <summary>
    /// ★ 2026-10-09 第二轮加的<b>兜底网</b>：清扫场上的"孤儿形象" ——
    /// 还挂在战斗房间里、但它对应的沙地兽<b>已经不在宠物列表里 或 已经死了</b>的节点。
    ///
    /// <para>为什么需要它：上一轮只修了"<c>Retire</c> 时收节点"，但用户实机仍有残影 ——
    /// 说明有节点是<b>绕过 Retire</b> 留在场上的（游戏/别的 mod 摘了登记表、或当时没查到）。
    /// 与其继续猜是哪条路漏的，不如每次收节点时顺手把"不该在场的"扫掉。</para>
    ///
    /// <para>判据刻意保守：只认 <c>Entity.Monster is SandBeast</c> 的节点，且它的 Creature
    /// <b>死了 或 不在 <c>PlayerCombatState.Pets</c> 里</b>；<b>在册且活着的一律不碰</b>。
    /// 拿不到宠物列表时<b>什么都不做</b>（宁可不扫，也不能误删活着的兽）。</para>
    /// </summary>
    private static void SweepOrphans(Player? owner)
    {
        if (owner == null)
        {
            return;
        }

        try
        {
            var room = NCombatRoom.Instance;

            if (room == null)
            {
                return;
            }

            var pets = owner.PlayerCombatState?.Pets;

            if (pets == null)
            {
                return;   // 拿不到名单就不扫（保守：绝不误删）
            }

            var inRoster = new HashSet<Creature>();
            var alive = new HashSet<Creature>();

            foreach (var pet in pets)
            {
                inRoster.Add(pet);

                if (pet.IsAlive)
                {
                    alive.Add(pet);
                }
            }

            var all = new List<NCreature>();
            CollectCreatureNodes(room, all);

            int freed = 0;

            foreach (var nc in all)
            {
                if (!GodotObject.IsInstanceValid(nc))
                {
                    continue;
                }

                Creature? entity = null;

                try
                {
                    entity = nc.Entity;
                }
                catch
                {
                    continue;
                }

                if (entity?.Monster is not SandBeast || alive.Contains(entity))
                {
                    continue;   // 不是沙地兽，或在册且活着 —— 别碰
                }

                Log.Info($"[沙地兽] 孤儿清扫：收掉一个不该在场的沙地兽节点" +
                         $"（在册={inRoster.Contains(entity)}，存活={entity.IsAlive}）");

                FreeNode(entity, nc, "孤儿清扫");
                freed++;
            }

            if (freed > 0)
            {
                Log.Info($"[沙地兽] 孤儿清扫：共收掉 {freed} 个残影节点");
            }
        }
        catch (Exception e)
        {
            WarnOnce($"[沙地兽] 孤儿清扫失败（只记一次）：{e.Message}");
        }
    }

    /// <summary>
    /// ★ 2026-10-09：<b>在兽脚下显示"这只兽提供了几层增伤"</b>（用户要求）。
    ///
    /// <para>数值 = 这只兽当前<b>持有的印记层数</b>（<c>SandBeast.MarkedEnemies</c> 的条目数）——
    /// 每层让该敌人受到的攻击伤害 +10%（「沙地兽与她」时 +12%，见
    /// <c>SandBeastMarkPower.ModifyDamageMultiplicative</c>）。
    /// 所以这个数字就是"它一共贡献了几层增伤"。</para>
    ///
    /// <para>幂等：第一次调用建节点，之后只改文本；层数为 0 时整个隐藏。</para>
    /// </summary>
    internal static void RefreshMarkBadge(Creature? beast, int stacks)
    {
        try
        {
            var visuals = beast?.GetCreatureNode()?.Visuals;

            if (visuals == null)
            {
                return;   // 还没挂上形象（或已经退场）—— 不是错误
            }

            var badge = visuals.GetNodeOrNull<Label>(BadgeNodeName);

            if (badge == null)
            {
                badge = new Label
                {
                    Name = BadgeNodeName,
                    // 宽度给足、水平居中，锚点 x=0（兽的脚底原点就在 x=……见 FeetAnchor）
                    Position = new Vector2(-60f, BadgeOffsetY),
                    Size = new Vector2(120f, 32f),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    // ⚠️ 和形象一样必须留 0：非 0 会压过 Y 排序（见类注释）。
                    ZIndex = 0,
                };

                // 配色照游戏那套（能量计 / HUD 计数器）：暖白字 + 深蓝黑描边，压在画面上也读得清。
                badge.AddThemeFontSizeOverride("font_size", 22);
                badge.AddThemeColorOverride("font_color", new Color(1f, 0.9647059f, 0.8862745f));
                badge.AddThemeColorOverride("font_outline_color", new Color(0.08f, 0.18f, 0.24f));
                badge.AddThemeConstantOverride("outline_size", 8);

                visuals.AddChild(badge);
            }

            badge.Text = stacks > 0 ? stacks.ToString() : string.Empty;
            badge.Visible = stacks > 0;
        }
        catch (Exception e)
        {
            WarnOnce($"[沙地兽] 印记徽章刷新失败（只记一次）：{e.Message}");
        }
    }

    /// <summary>给刚召唤出来的沙地兽挂上形象（幂等；任何失败都只是"没有美术"，不影响玩法）。</summary>
    internal static void ApplyAnimatedArt(Creature? beast)
    {
        try
        {
            var visuals = beast?.GetCreatureNode()?.Visuals;

            if (visuals == null)
            {
                return;
            }

            // 已经挂过就跳过（满上限替换时是新兽，不会走到这里；纯粹防重复调用）。
            if (visuals.GetNodeOrNull<Node>(SpineNodeName) != null || visuals.GetNodeOrNull<Node>(FrameNodeName) != null)
            {
                RefreshBadgeOf(beast);
                return;
            }

            // 藏掉兜底场景里那张占位图（它其实是 images/monsters/error.png 报错图）。两条路都要藏。
            visuals.GetNodeOrNull<Sprite2D>("Visuals")?.SetVisible(false);

            string? beastSkeleton = Art.RaySkinCatalog.Get(Art.RaySkinState.Selected).BeastSkeletonDataPath;

            if (beastSkeleton != null && TryAttachSpine(visuals, beastSkeleton))
            {
                RefreshBadgeOf(beast);
                return;
            }

            AttachFrameSprite(visuals);
            RefreshBadgeOf(beast);
        }
        catch (Exception e)
        {
            WarnOnce($"[沙地兽] 挂形象失败（只记一次）：{e}");
        }
    }

    /// <summary>按这只兽**当前**持有的印记层数建/刷徽章（形象挂好之后立刻补一次，否则要等层数变化才出现）。</summary>
    private static void RefreshBadgeOf(Creature? beast)
    {
        if (beast?.Monster is SandBeast model)
        {
            RefreshMarkBadge(beast, model.MarkedEnemies.Count);
        }
    }

    /// <summary>
    /// 挂皮肤配套的 Spine 模型。<b>骨架在进树前就设好</b> —— 这样 spine 直接用我们要的数据建骨架，
    /// 不存在"换骨架把动画状态重置掉"的问题（铁律 18 的同款规避）。
    /// </summary>
    private static bool TryAttachSpine(Node visuals, string dataPath)
    {
        var data = ResourceLoader.Load<Resource>(dataPath);

        if (data == null)
        {
            Log.Warn($"[沙地兽] 皮肤骨骼加载失败：{dataPath} → 回退帧动画");
            return false;
        }

        // ⚠️ spine-godot 的 SpineSprite 类型**不在游戏程序集的引用里**，
        //    所以只能按 Godot 类名实例化（原版自己也是用 GetClass() 字符串判它的）。
        //    ClassDB.Instantiate 返回的是 Variant，要 AsGodotObject() 转出来。
        if (ClassDB.Instantiate("SpineSprite").AsGodotObject() is not Node2D spine)
        {
            Log.Warn("[沙地兽] 无法实例化 SpineSprite → 回退帧动画");
            return false;
        }

        spine.Name = SpineNodeName;
        spine.Set("skeleton_data_res", data);
        spine.Position = FeetAnchor;
        spine.Scale = new Vector2(SpineScale, SpineScale);
        spine.ZIndex = 0; // ⚠️ 理由见类注释，别改成 1。

        visuals.AddChild(spine);

        PlayIdleWhenReady(spine);

        Log.Info($"[沙地兽] 已挂 Spine 形象（{dataPath}，scale={SpineScale}）");
        return true;
    }

    /// <summary>
    /// 等骨架建好后播待机。<b>骨架是异步建的</b>：进树那一刻 <c>get_animation_state()</c> 还是空的，
    /// 直接调会抛异常，所以用 Godot 自带的 <see cref="Timer"/> 轮询几拍。
    ///
    /// <para>⚠️ 这里<b>故意不用自定义 C# Node</b> 去写 <c>_Process</c>：那样会引入
    /// "这个脚本类要不要先注册进 Godot"的不确定性（mod 的 C# 类注册走
    /// <c>RitsuLibFramework.EnsureGodotScriptsRegistered</c>）。<c>Timer</c> 是引擎自带节点，稳。</para>
    /// </summary>
    private static void PlayIdleWhenReady(Node spineNode)
    {
        var spine = new MegaSprite(spineNode);

        // ⚠️ 必须写全 Godot.Timer：ImplicitUsings 把 System.Threading 也带进来了，
        //    只写 Timer 会和 System.Threading.Timer 撞名（CS0104）。
        var timer = new Godot.Timer { WaitTime = 0.05, OneShot = false, Autostart = true };
        int ticks = 0;

        timer.Timeout += () =>
        {
            ticks++;

            try
            {
                if (spine.IsAnimationStateReady() && spine.HasAnimation(AnimationName))
                {
                    spine.GetAnimationState().SetAnimation(AnimationName, true);
                    Log.Info($"[沙地兽] Spine 待机已开始（等了 {ticks} 拍）");
                    timer.QueueFree();
                    return;
                }
            }
            catch (Exception e)
            {
                WarnOnce($"[沙地兽] 驱动 Spine 失败（只记一次）：{e.Message}");
                timer.QueueFree();
                return;
            }

            // 3 秒还没就绪就放弃，别把定时器永远留在场上。
            if (ticks >= 60)
            {
                WarnOnce("[沙地兽] Spine 骨架 3 秒内没就绪，形象可能是静止的");
                timer.QueueFree();
            }
        };

        spineNode.AddChild(timer);
    }

    /// <summary>回退路线：30 帧 sprite（原始莱伊那套皮肤，以及 Spine 路线失败时兜底）。</summary>
    private static void AttachFrameSprite(Node visuals)
    {
        _frames ??= GD.Load<SpriteFrames>(FramesPath);

        if (_frames == null)
        {
            WarnOnce($"[沙地兽] 载入帧资源失败：{FramesPath}");
            return;
        }

        // 让"包围盒底边的水平中点"落在脚底锚点上（AnimatedSprite2D 默认按画布中心定位，
        // 直接放大就会往下沉、脚陷进地毯）。
        float bboxCenterX = (ArtBBoxLeft + ArtBBoxRight) / 2f;

        var artPosition = new Vector2(
            FeetAnchor.X - (bboxCenterX - CanvasCenter) * ArtScale,
            FeetAnchor.Y - (ArtBBoxBottom - CanvasCenter) * ArtScale);

        var art = new AnimatedSprite2D
        {
            Name = FrameNodeName,
            SpriteFrames = _frames,
            Animation = AnimationName,
            Autoplay = AnimationName,
            Position = artPosition,
            Scale = new Vector2(ArtScale, ArtScale),
            ZIndex = 0, // ⚠️ 理由见类注释，别改成 1。
        };

        visuals.AddChild(art);

        Log.Info($"[沙地兽] 帧动画已挂上（{FrameNodeName}，{_frames.GetFrameCount(AnimationName)} 帧，" +
                 $"scale={ArtScale}，pos=({artPosition.X:F0},{artPosition.Y:F0})）");
    }

    private static void WarnOnce(string message)
    {
        if (_loggedFailure)
        {
            return;
        }

        _loggedFailure = true;
        Log.Warn(message);
    }
}
