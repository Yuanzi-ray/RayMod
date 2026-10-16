using Godot;
using MegaCrit.Sts2.Core.Animation;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Entities.Characters;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Characters;
using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Godot;
using STS2RitsuLib.Scaffolding.Visuals.StateMachine;
using STS2RitsuLib.Scaffolding.Visuals.StateMachine.Backends;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Characters;

[RegisterCharacter]
public sealed class RayModCharacter : ModCharacterTemplate<RayModCardPool, RayModRelicPool, RayModPotionPool>,
    IModCharacterMerchantAnimationStateMachineFactory
{
    private static readonly Logger AnimLog = new("RayMod.Characters", LogType.Generic);

    public static readonly Color ThemeColor = new(0.42f, 0.65f, 0.72f);

    private const string SceneRoot = $"{Entry.ResPath}/scenes/characters";
    private const string ImageRoot = $"{Entry.ResPath}/images/characters";
    private const string CharacterScenePath = $"{SceneRoot}/RayMod_character.tscn";
    private const string EnergyCounterScenePath = $"{SceneRoot}/RayMod_energy_counter.tscn";
    private const string MerchantScenePath = $"{SceneRoot}/RayMod_merchant.tscn";
    private const string RestSiteScenePath = $"{SceneRoot}/RayMod_rest_site.tscn";
    private const string CharacterSelectBgScenePath = $"{SceneRoot}/RayMod_character_select_bg.tscn";

    // 顶栏头像走的是 CharacterModel.Icon（一个**场景**，不是贴图）。
    // ⚠️ 这个字段不填的话，RitsuLib 的 CharacterAssetProfiles.Resolve 会用
    //    PlaceholderCharacterId（= ironclad）的路径补齐 → 顶栏显示铁甲战士的头。
    //    结构照抄原版 scenes/ui/character_icons/ironclad_icon.tscn。
    private const string CharacterIconScenePath = $"{SceneRoot}/RayMod_character_icon.tscn";

    public override Color NameColor => ThemeColor;
    public override Color EnergyLabelOutlineColor => new(0.08f, 0.18f, 0.24f);
    public override Color MapDrawingColor => ThemeColor;

    public override CharacterGender Gender => CharacterGender.Feminine;
    public override int StartingHp => 75;
    public override int StartingGold => 99;

    public override CharacterAssetProfile AssetProfile => new(
        Scenes: new CharacterSceneAssetSet(
            VisualsPath: CharacterScenePath,
            EnergyCounterPath: EnergyCounterScenePath,
            MerchantAnimPath: MerchantScenePath,
            RestSiteAnimPath: RestSiteScenePath),
        Ui: new CharacterUiAssetSet(
            IconTexturePath: $"{ImageRoot}/RayMod_character_icon.png",
            IconOutlineTexturePath: $"{ImageRoot}/RayMod_character_icon_outline.png",
            IconPath: CharacterIconScenePath,
            CharacterSelectBgPath: CharacterSelectBgScenePath,
            CharacterSelectIconPath: $"{ImageRoot}/RayMod_character_select.png",
            CharacterSelectLockedIconPath: $"{ImageRoot}/RayMod_character_select_locked.png",
            MapMarkerPath: $"{ImageRoot}/RayMod_map_marker.png"),
        // ⚠️ 音频必须显式给：不给的话 RitsuLib 会拿 PlaceholderCharacterId（= ironclad）
        //    去补全 AttackSfx/CastSfx/DeathSfx —— 打攻击牌会响**铁甲战士的攻击音**（用户实机发现的）。
        //    下面这几条是"虚拟 FMOD 事件"（由 pck 里的 wav 支撑），注册在 Audio/CharacterSfx.cs。
        Audio: new CharacterAudioAssetSet(
            CharacterSelectSfx: Audio.CharacterSfx.SelectEvent,
            AttackSfx: Audio.CharacterSfx.AttackEvent,
            CastSfx: Audio.CharacterSfx.CastEvent,
            DeathSfx: Audio.CharacterSfx.DeathEvent));

    public override string? PlaceholderCharacterId => "ironclad";
    public override bool RequiresEpochAndTimeline => false;

    // 动画延迟
    public override float AttackAnimDelay => 0.15f;
    public override float CastAnimDelay => 0.25f;

    protected override NCreatureVisuals? TryCreateCreatureVisuals()
    {
        // 诊断日志（2026-10-07）：确认这条路径到底有没有被走到、走的是哪个场景。
        // 症状是"换了骨架但战斗里没变化"，靠日志才能区分"没走到"还是"走了但骨架没换"。
        AnimLog.Info($"[视觉] TryCreateCreatureVisuals -> {CharacterScenePath}");
        var visuals = RitsuGodotNodeFactories.CreateFromScenePath<NCreatureVisuals>(CharacterScenePath);
        AnimLog.Info($"[视觉] 场景创建结果 = {(visuals == null ? "null ❌" : visuals.GetType().Name)}");

        // ★ 换肤：在场景**进场景树之前**把骨架换成玩家选的那套。
        //   这个窗口是关键（见 RaySkinApplier 的说明）：SpineSprite 的 _ready 还没跑，
        //   骨架直接用我们塞的数据建 —— 不会有"换骨架重置动画状态"的问题（铁律 18）。
        //   没选过 / 资源坏了都会回退到场景自带的夏日皮肤，行为与以前完全一致。
        Art.RaySkinApplier.ApplyToVisualsScene(visuals);

        return visuals;
    }

    /// <summary>
    /// ★★ 2026-10-07：<b>故意返回 null —— 这个开关是我们"人物不动"的元凶，不要打开它。</b>
    ///
    /// <para><b>为什么现在是 null（有证据的结论，别再改回路径）</b></para>
    ///
    /// <para>RitsuLib 的 <c>CharacterCombatSpineOverridePatch</c> 是 <c>NCreature._Ready</c> 的
    /// <b>Postfix</b>（反编译 <c>STS2-RitsuLib.Runtime.dll</c> 确认），它在原方法体<b>跑完之后</b>才执行
    /// <c>MegaSprite.SetSkeletonDataRes(...)</c>。而我们的动画器是<b>在 <c>_Ready</c> 内部</b>建的：
    /// <c>NCreature.cs:511</c> → <c>GenerateAnimator</c> → <see cref="SetupCustomCreatureAnimator"/>，
    /// 而 <c>CreatureAnimator</c> 的构造函数<b>当场就播 Idle</b>
    /// （<c>CreatureAnimator.cs:43</c> → <c>SetNextState</c> → <c>SetAnimation("Idle", true)</c>）。</para>
    ///
    /// <para>于是顺序是：<b>播 Idle → 换骨架 → 重置 animation state → Idle 被抹掉</b>，
    /// 人物定格在绑定姿势。要等某张牌打出（Cast/Attack 链回到 Idle）才重新 SetAnimation ——
    /// 这正是用户反馈的"<i>要召唤一次沙地兽才会到 idle</i>"。</para>
    ///
    /// <para><b>而且这次替换是纯亏的</b>：<c>RayMod_character.tscn</c> 里 SpineSprite 的
    /// <c>skeleton_data_res</c> 已经是内联 SubResource（atlas = <c>char_4117_ray_summer.atlas</c>、
    /// skel = <c>char_4117_ray_summer.skel</c>），与
    /// <c>char_4117_ray_summer_data.tres</c> <b>引用的资源逐字段相同</b> ——
    /// 换过去骨架一模一样，<b>唯一效果就是重置动画状态</b>。所以这条路既没换来任何观感，还弄坏了动画。</para>
    ///
    /// <para><b>之前为什么会误判"场景里的 ext_resource 没生效"</b>：那是 §D16 那个坑 ——
    /// 场景里要填的是 <c>SpineSkeletonDataResource</c>，直接指 <c>.skel</c> 会报
    /// <c>Expected BoundObject to be a SpineSkeletonDataResource, but it is a SpineSkeletonFileResource!</c>
    /// 现在场景里用的是"atlas + skel 合成 SubResource"的正确写法，战斗里本来就换了模型。</para>
    ///
    /// <para>⚠️ <b>将来做换肤 UI 时</b>：那时确实需要在运行时换骨架，但<b>不能只靠这个开关</b> ——
    /// 必须"换完骨架再重建/重播动画状态"（见 <c>Patches/CombatIdleAnimationGuardPatch.cs</c>，
    /// 那里的守护补丁会在骨架被动过之后把 Idle 补回来）。</para>
    /// </summary>
    public override string? CustomCombatSpineSkeletonDataPath => null;

    // 🎬 动画：名字全部来自映射表（Art/SpineAnimMap.cs），这里只负责"把表变成动画器"。
    //
    // 之前这段是把 "Attack_Begin" / "Skill_1_Begin" 这类字符串写死在这里的（用户 2026-10-04 说"映射太死板"）：
    // 换动画要改代码、骨架里 Skill_2/3、Reload_* 这些动作一个都用不上、商店那条路也接不上。
    // 现在分派规则（触发器 → 类别 → 变体）全在 SpineAnimMap 里，改观感只动那一个文件。
    //
    // ⚠️ 2026-10-04（0.111.0 兼容）：这里原本是 `override GenerateAnimator(MegaSprite)`，
    //    直接覆盖游戏 CharacterModel 的方法。游戏 0.111.0 把它改成了
    //    `GenerateAnimator(MegaSprite, Creature)`（多一个 Creature 参数）——
    //    旧签名在 0.111.0 上编译直接 CS0115（没有可重写的方法），
    //    而新签名在 0.110.x 上又不存在。两条路互斥，不能靠一套签名吃两版。
    //
    //    出路：RitsuLib 的 ModCharacterTemplate 提供了一个**工厂钩子**
    //    `SetupCustomCreatureAnimator(MegaSprite)`，它在
    //      · STS2.RitsuLib.Compat.0.110.0（0.110.0 兼容包）
    //      · STS2.RitsuLib（0.111.0 主线包）
    //    里**都存在且签名一致**（都是 `protected virtual CreatureAnimator?`）。
    //    RitsuLib 通过 IModCreatureAnimatorFactory 把游戏侧的调用路由到它，
    //    所以改用它 = 一套代码同时兼容 0.110.x 与 0.111.0，不必条件编译。
    //    返回 null 时 RitsuLib 会回退到原版 CharacterModel.GenerateAnimator。
    protected override CreatureAnimator? SetupCustomCreatureAnimator(MegaSprite controller)
    {
        AnimState idle = Art.SpineAnimMap.CreateIdleState();

        // 诊断日志（2026-10-07）：换了皮肤后"人物不会动"，要确认①这个方法被调用了、
        // ②它拿到的骨架里到底有没有 Idle 动画（RitsuLib 会在节点就绪后换骨架，时序很关键）。
        try
        {
            bool hasIdle = controller.HasAnimation(Art.SpineAnims.Idle);
            bool hasAttack = controller.HasAnimation(Art.SpineAnims.AttackBegin);
            AnimLog.Info($"[动画] SetupCustomCreatureAnimator 被调用；" +
                         $"HasAnimation(Idle)={hasIdle} HasAnimation(Attack_Begin)={hasAttack}");
        }
        catch (Exception ex)
        {
            AnimLog.Warn($"[动画] 骨架自检失败：{ex.Message}");
        }

        CreatureAnimator animator = new(idle, controller);

        animator.AddAnyState(CreatureAnimator.idleTrigger, idle);

        // 原版商店/休息处点名要的 "relaxed_loop"（CharacterModel.relaxedAnim）—— 骨架里没有这个名字，
        // 通过 AnimState 这条路进来的一律指到待机（商店实际走的是下面的状态机工厂）。
        animator.AddAnyState("Relaxed", idle);

        // 复活：骨架里没有专门的复活动作，先指到待机（候选是 SpineAnims.Start）。
        animator.AddAnyState(CreatureAnimator.reviveTrigger, idle);

        // 其余触发器（Attack / Cast / PowerUp / Hit / Dead）按映射表逐条挂上条件分支。
        foreach (string trigger in new[]
                 {
                     CreatureAnimator.attackTrigger,
                     CreatureAnimator.castTrigger,
                     CreatureAnimator.powerUpTrigger,
                     CreatureAnimator.hitTrigger,
                     CreatureAnimator.deathTrigger,
                 })
        {
            foreach (var (condition, chain) in Art.SpineAnimMap.BranchesFor(trigger))
            {
                animator.AddAnyState(trigger, Art.SpineAnimMap.BuildChainHead(chain, idle), condition);
            }
        }

        return animator;
    }

    // ===================== 商店（商人房间）形象 =====================

    /// <summary>
    /// <b>商店里那只会动的莱伊</b>：RitsuLib 会把这个工厂的返回值当"商人形象的状态机"用
    /// （<c>ModMerchantCharacterVisualPlaybackPatch</c> 把 <c>NMerchantCharacter.PlayAnimation</c>
    /// 路由到它，触发名由 <c>MapWorldAnimationToStateMachineTrigger</c> 从 <c>"relaxed_loop"</c> 翻成 <c>Relaxed</c>）。
    ///
    /// <para><b>为什么必须实现它</b>：原版 <c>NMerchantCharacter._Ready</c> 直接对这个 Spine 播
    /// <c>"relaxed_loop"</c>，而我们的骨架<b>没有</b>这个动画 —— 没有这层映射就只会打一条
    /// <c>could not find 'relaxed_loop' animation</c> 的警告然后站着不动。</para>
    ///
    /// <para>⚠️ <b>不能走 <c>ModAnimStateMachines.StandardMerchantCue</c></b>（2026-10-04 实测踩到）：
    /// 它内部用 <c>CompositeBackendFactory</c> 从根节点自动挑后端，而那个探测找的是
    /// <c>NCreatureVisuals</c> 那种包装节点（<c>HasSpineAnimation</c> / <c>SpineBody</c>）——
    /// 我们的商店根是 <c>NMerchantCharacter</c> + 一个裸 <c>SpineSprite</c>，
    /// 于是直接抛 <c>No animation backend could be built for 'RayModCharacterMerchant'</c>，
    /// 商店里就变成一个<b>冻住</b>的莱伊（日志里能看到那条 WARN）。
    /// 所以这里<b>自己建 <see cref="SpineAnimationBackend"/></b>，绕开自动探测。</para>
    /// </summary>
    public ModAnimStateMachine? TryCreateMerchantAnimationStateMachine(Node merchantRoot, CharacterModel character)
    {
        try
        {
            // ★ 商店形象现在有三套（跟随玩家选的皮肤），所以**优先在"当前可见"的那个插槽里找骨架** ——
            //   否则状态机会去驱动一个藏起来的骨架，动画打了但看不见。
            //   单个插槽是普通 Node2D，C# 认得出（SpineSprite 本身认不出，见铁律 19）。
            Node? spine = FindSpineSprite(FindVisibleMerchantSlot(merchantRoot) ?? merchantRoot);

            if (spine == null)
            {
                AnimLog.Warn("[动画] 商店场景里找不到 SpineSprite，商店形象会静止");
                return null;
            }

            var machine = new ModAnimStateMachine(new SpineAnimationBackend(new MegaSprite(spine)));

            // 待机（循环）。原版点名的 "relaxed_loop" 会被 RitsuLib 翻成 "Relaxed"，
            // 两个名字都挂上：不管它点哪个，出来的都是骨架里真实存在的 Idle。
            var idle = new ModAnimState(Art.SpineAnims.Idle, isLooping: true);

            machine.AddAnyState("Idle", idle);
            machine.AddAnyState("Relaxed", idle);
            machine.AddAnyState("Dead", new ModAnimState(Art.SpineAnims.Die));

            machine.Start(idle);

            AnimLog.Info($"[动画] 商店状态机已建立（{spine.Name} → {Art.SpineAnims.Idle}）");

            return machine;
        }
        catch (Exception e)
        {
            // 工厂返回 null 时 RitsuLib 会退回"单次视觉提示播放"（Spine 根上没有提示 = 站着不动），
            // 也就是退化成"商店里一个静止的莱伊"—— 不崩，但要知道原因。
            AnimLog.Warn($"[动画] 商店状态机创建失败，商店形象会静止：{e.Message}");
            return null;
        }
    }

    /// <summary>
    /// 找商店场景里**当前可见**的那个皮肤插槽（<c>MerchantSlot{i}</c>）。
    /// 显隐是 <c>MerchantSkinSelector.gd</c> 按玩家选的皮肤设好的；这里只读结果。
    /// 找不到就返回 null，调用方退回"整棵树搜"的老行为。
    /// </summary>
    private static Node? FindVisibleMerchantSlot(Node merchantRoot)
    {
        try
        {
            for (int i = 0; i < merchantRoot.GetChildCount(); i++)
            {
                Node child = merchantRoot.GetChild(i);

                if (child is Node2D slot && child.Name.ToString().StartsWith("MerchantSlot", StringComparison.Ordinal) && slot.Visible)
                {
                    return slot;
                }
            }
        }
        catch (Exception e)
        {
            AnimLog.Warn($"[动画] 找可见的商店皮肤插槽失败（退回全树搜索）：{e.Message}");
        }

        return null;
    }

    /// <summary>
    /// 在节点树里找 <c>SpineSprite</c>（优先第一个子节点 —— 原版 <c>_Ready</c> 也是
    /// <c>new MegaSprite(GetChild(0))</c>）。
    /// ⚠️ 用 <c>GetClass()</c> 字符串判断而不是 C# 类型：spine-godot 的 <c>SpineSprite</c> 类型
    /// 不在游戏程序集的引用里（RitsuLib 自己也是这么判的）。
    /// </summary>
    private static Node? FindSpineSprite(Node root)
    {
        for (int i = 0; i < root.GetChildCount(); i++)
        {
            if (IsSpineSprite(root.GetChild(i)))
            {
                return root.GetChild(i);
            }
        }

        for (int i = 0; i < root.GetChildCount(); i++)
        {
            Node? found = FindSpineSprite(root.GetChild(i));

            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static bool IsSpineSprite(Node node) => node.GetClass().ToString() == "SpineSprite";

    public override List<string> GetArchitectAttackVfx()
    {
        return
        [
            "vfx/vfx_attack_blunt",
            "vfx/vfx_heavy_blunt",
            "vfx/vfx_attack_slash",
            "vfx/vfx_bloody_impact",
            "vfx/vfx_rock_shatter"
        ];
    }
}