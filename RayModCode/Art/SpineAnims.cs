namespace RayMod.Art;

/// <summary>
/// <b>骨架里真实存在的动画名清单</b> —— 全部 56 个，2026-10-04 由 Godot 自己报出来的
/// （<c>_pckcheck\list_anims.gd</c>：<c>SpineSkeletonDataResource.get_animations()</c>，
/// 比从 .skel 二进制字符串表里掏可靠 —— 掏的时候就把 <c>OnAttack</c> 误当成动画了，
/// 那个名字其实只存在于部件/事件表里）。
///
/// <para><b>为什么要有这份清单</b>：动画名写错的话 <c>CreatureAnimator.SetNextState</c>
/// 只会打一条 <c>could not find '&lt;名字&gt;' animation on '&lt;节点&gt;'</c> 的警告然后静默不动
/// （<c>CreatureAnimator.cs:92-96</c>）—— 卡面看不出来，只能看日志。
/// 所以名字一律从这里取，不要手写字符串。</para>
///
/// <para>骨架：Spine <b>4.2.0</b>，<c>res://spine/char_ray.skel</c>（1.15 MB 二进制，改不了 ——
/// 加动画要用 Spine 编辑器）。动作是<b>分段式</b>的：<c>Begin → Loop → End</c>，
/// 还有一整套 <c>_Down_</c>（卧姿）与 <c>*_Reload_*</c>（边做动作边装填）变体。</para>
///
/// <para>⚠️ <b>2026-10-07 补充：现在项目里有两套骨架，这份清单是按"两套的公共结构"写的。</b>
/// <list type="bullet">
/// <item><b>战斗</b>走 <c>RayMod\scenes\characters\RayMod_character.tscn</c> →
/// <c>res://RayMod/spine/skins/summer/…</c>（明日方舟皮肤，4.2.11）。</item>
/// <item><b>商店</b>走 <c>RayMod_merchant.tscn</c> → <c>res://spine/char_*</c>（<b>上面这个旧骨架，原始莱伊</b>）。</item>
/// </list>
/// 两套动画名一致（§J3 已核对），所以本清单对两者都成立；
/// 但<b>"战斗里是夏日皮肤、商店里是原始莱伊"这个不一致还在</b> —— 做换肤功能时要一并统一。</para>
///
/// <para>⚠️ 骨架里<b>没有</b> <c>relaxed_loop</c>：那是原版五个角色共用的"商店/休息处待机"名
/// （<c>CharacterModel.cs:27</c> 的 <c>relaxedAnim</c> + <c>NMerchantCharacter.cs:14</c> 直接点名要它），
/// 所以商店必须靠 <see cref="SpineAnimMap"/> 把它映射到本清单里的动画。</para>
/// </summary>
public static class SpineAnims
{
    // ── 基础 ──
    public const string Idle = "Idle";
    public const string Die = "Die";
    public const string Start = "Start";

    /// <summary>名字就叫 <c>Default</c> 的动画（语义未知，当备用候选）。</summary>
    public const string Default = "Default";

    // ── 攻击（站立 / 卧姿）──
    public const string AttackBegin = "Attack_Begin";
    public const string AttackLoop = "Attack_Loop";
    public const string AttackEnd = "Attack_End";
    public const string AttackDownBegin = "Attack_Down_Begin";
    public const string AttackDownLoop = "Attack_Down_Loop";
    public const string AttackDownEnd = "Attack_Down_End";

    // ── 技能 1（骨架里唯一没有 Reload 变体的一套）──
    public const string Skill1Begin = "Skill_1_Begin";
    public const string Skill1Loop = "Skill_1_Loop";
    public const string Skill1End = "Skill_1_End";

    // ── 技能 2 ──
    public const string Skill2Begin = "Skill_2_Begin";
    public const string Skill2Loop = "Skill_2_Loop";
    public const string Skill2End = "Skill_2_End";
    public const string Skill2Idle = "Skill_2_Idle";
    public const string Skill2ReloadBegin = "Skill_2_Reload_Begin";
    public const string Skill2ReloadLoop = "Skill_2_Reload_Loop";
    public const string Skill2ReloadBreak = "Skill_2_Reload_Break";
    public const string Skill2ReloadEnd = "Skill_2_Reload_End";

    // ── 技能 3 ──
    public const string Skill3Begin = "Skill_3_Begin";
    public const string Skill3Loop = "Skill_3_Loop";
    public const string Skill3End = "Skill_3_End";
    public const string Skill3Idle = "Skill_3_Idle";
    public const string Skill3ReloadBegin = "Skill_3_Reload_Begin";
    public const string Skill3ReloadLoop = "Skill_3_Reload_Loop";
    public const string Skill3ReloadBreak = "Skill_3_Reload_Break";
    public const string Skill3ReloadEnd = "Skill_3_Reload_End";

    // ── 装填（站姿 / 卧姿）──
    public const string ReloadBegin = "Reload_Begin";
    public const string ReloadLoop = "Reload_Loop";
    public const string ReloadBreak = "Reload_Break";
    public const string ReloadEnd = "Reload_End";
    public const string DownReloadBegin = "Down_Reload_Begin";
    public const string DownReloadLoop = "Down_Reload_Loop";
    public const string DownReloadBreak = "Down_Reload_Break";
    public const string DownReloadEnd = "Down_Reload_End";

    // ── 卧姿技能 1 ──
    public const string SkillDown1Begin = "Skill_Down_1_Begin";
    public const string SkillDown1Loop = "Skill_Down_1_Loop";
    public const string SkillDown1End = "Skill_Down_1_End";

    // ── 卧姿技能 2 ──
    public const string SkillDown2Begin = "Skill_Down_2_Begin";
    public const string SkillDown2Loop = "Skill_Down_2_Loop";
    public const string SkillDown2End = "Skill_Down_2_End";
    public const string SkillDown2Idle = "Skill_Down_2_Idle";
    public const string SkillDown2ReloadBegin = "Skill_Down_2_Reload_Begin";
    public const string SkillDown2ReloadLoop = "Skill_Down_2_Reload_Loop";
    public const string SkillDown2ReloadBreak = "Skill_Down_2_Reload_Break";
    public const string SkillDown2ReloadEnd = "Skill_Down_2_Reload_End";

    // ── 卧姿技能 3 ──
    public const string SkillDown3Begin = "Skill_Down_3_Begin";
    public const string SkillDown3Loop = "Skill_Down_3_Loop";
    public const string SkillDown3End = "Skill_Down_3_End";
    public const string SkillDown3Idle = "Skill_Down_3_Idle";
    public const string SkillDown3ReloadBegin = "Skill_Down_3_Reload_Begin";
    public const string SkillDown3ReloadLoop = "Skill_Down_3_Reload_Loop";
    public const string SkillDown3ReloadBreak = "Skill_Down_3_Reload_Break";
    public const string SkillDown3ReloadEnd = "Skill_Down_3_Reload_End";
}
