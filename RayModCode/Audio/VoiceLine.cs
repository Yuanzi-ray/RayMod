using System.Collections.Generic;
using System.Linq;

namespace RayMod.Audio;

/// <summary>
/// <b>莱伊语音条目</b>。枚举值 <b>就是</b> 游戏的 voiceId 编号 —— 文件名固定是
/// <c>cn_0NN.wav</c>（<see cref="VoicePlayer"/> 靠 <c>(int)line</c> 拼路径，所以别改数值）。
///
/// <para><b>来源与核对（2026-10-07）</b>：音频取自 PRTS，
/// <code>https://torappu.prts.wiki/assets/audio/{voice|voice_cn}/char_4117_ray/cn_0NN.wav</code>。
/// <b>编号→台词已用明日方舟本体数据交叉验证</b>：
/// <c>Kengxxiao/ArknightsGameData</c> 的 <c>zh_CN/gamedata/excel/charword_table.json</c> 里，
/// 莱伊 38 条记录的 <c>voiceId</c>（<c>CN_017</c>）↔ <c>voiceText</c> ↔ <c>voiceAsset</c> 三者一致，
/// 与 PRTS 的表格完全吻合 → <b>文件编号是对的，不用再怀疑</b>。</para>
///
/// <para>⚠️ 三个 URL 坑（别重踩）：后缀是 <c>.wav</c> 不是 <c>.mp3</c>（PRTS 的 widget JS 里那句
/// <c>.wav→.mp3</c> 替换是误导，照它推全 404）；文件名是<b>小写</b> <c>cn_017</c>；
/// <b>文件名各语种相同、只有目录不同</b>。</para>
/// </summary>
public enum VoiceLine
{
    None = 0,

    // ── 卡牌台词（当前在用的）──
    AnimalAlmanac = 1,    // 这本动物图册很漂亮 ← 动物图册
    SandBeastFan = 3,     // 沙地兽的背扇，能反映环境空气状况 ← 背扇 / 沙地兽背包
    ParanormalTape = 4,   // 这些灵异传说录像带…… ← 灵异录像带
    TravelDream = 9,      // 资料库里的传说故事，想亲眼看看 ← 旅行
    ChaseSunMoon = 14,    // 人们就不想追逐日月了吗 ← 追光者之瞳
    Squad = 17,           // 过来，沙地兽，我们要出发了 ← 选角 / 沙地兽系列
    GoodVisibility = 20,  // 视野很好，能看到的不止是危险 ← 广域警觉 / 探照灯
    BoltsReady = 21,      // 弩箭已经装好了 ← 狙击 / 狙击镜
    Deploy2 = 24,         // 如果有危险，你们不要下来 ← 防护类
    ThreatGone = 25,      // 我所在的方位有威胁——啊，马上就没有了 ← 全力以赴
    HeardMovement = 26,   // 我听见了移动的声音 ← 伏击 / 猎人直觉 / 定向猎杀
    ThisFar = 27,         // 你只能走到这里了 ← 你只能走到这了
    StayInDark = 28,      // 我不想……留在黑暗之中 ← 禁止明火 / 黑暗系列
    NewFood = 36,         // 过来，试试新的饲料 ← 饲养 / 饲料 / 哺育 / 沙地兽的祝福 / 萌宠博主

    // ── 战斗流程 ──
    Idle = 10,            // 别把博士挠醒了（暂未使用）
    Onboarding = 11,      // 我通过干员测试了（暂未使用）
    BattleStart = 19,     // 气流的声音，动物的足迹 ← 战斗开始
    SunOut = 29,          // ……太阳出来了。真漂亮（暂未使用）
    AreaClear = 30,       // 这片区域暂时没有危险了 ← 战斗胜利
    Defeat = 32,          // 你们快走 ← 玩家死亡
    Weather = 42,         // 外面的天气很好 ← 回合开始

    // ── 系统事件 / 暂未使用 ──
    Explosion = 2,        // 有爆炸声。稍等，我去下层检查（暂未使用）
    LightPlace = 7,       // 宿舍的灯坏了，我想待在明亮的地方（暂未使用）
    BigShadow = 8,        // 童年沙尘里的庞大影子（暂未使用）
    Promotion = 13,       // 那张纸被钳兽抢走了（留给"拿到遗物"用）
    Deploy1 = 23,         // 嗯，我会随时报告情况（暂未使用）
    ShortLeave = 38,      // 这是短时间离舰申请 ← 20-29 档拿药水
}

/// <summary>语音大类 —— 设置页按这个开关（用户 2026-10-07 指定）。</summary>
public enum VoiceCategory
{
    /// <summary>打出卡牌时的台词。</summary>
    Card,

    /// <summary>战斗流程（开始/回合/胜利/失败/闲置）。</summary>
    Combat,

    /// <summary>系统事件（选角/遗物/升级/召唤/药水）。</summary>
    System,
}

/// <summary>语音语种。</summary>
public enum VoiceLang
{
    /// <summary>日文（PRTS 的 <c>voice/</c> 目录）。</summary>
    Jp,

    /// <summary>中文（PRTS 的 <c>voice_cn/</c> 目录）。</summary>
    Cn,
}

/// <summary>
/// <b>卡牌 → 语音</b>的映射表。键是<b>卡牌的 C# 类名</b>（不含命名空间）——
/// 比拿本地化键或中文名去配对稳（那两样会随文案改动而变）。
///
/// <para><b>收录标准（2026-10-07 用户实机反馈后收紧）</b>：这句话<b>整句</b>得在讲这张卡。
/// 只是"共用一个词"的<b>一律不收</b> —— 第一版硬凑了 23 张，用户听到"播了不相干的句子"，
/// 最离谱的是「巨兽」配 008（那句整句在讲阿米娅）和「整装待发」配 023（跟装填毫无关系）。
/// <b>宁缺勿错</b>：没配的卡就是不打台词，别为了"每张都有"去凑。</para>
/// </summary>
public static class VoiceCatalog
{
    /// <summary>选角界面用的语音（用户指定：编入队伍）。</summary>
    public const VoiceLine CharacterSelect = VoiceLine.Squad;

    private static readonly Dictionary<string, VoiceLine> CardMap = new()
    {
        // ── 逐字对应（句子就是在讲这张卡）──
        ["OnlyGoThisFar"] = VoiceLine.ThisFar,            // 你只能走到这了 ← 「你只能走到这里了。」
        ["ParanormalTape"] = VoiceLine.ParanormalTape,    // 灵异录像带 ← 「这些灵异传说录像带…」
        ["AnimalGuide"] = VoiceLine.AnimalAlmanac,        // 动物图册 ← 「这本动物图册很漂亮。」
        ["BackFan"] = VoiceLine.SandBeastFan,             // 背扇 ← 「沙地兽的背扇…」
        ["Travel"] = VoiceLine.TravelDream,               // 旅行 ← 「…有些东西我想亲眼看看」
        ["LightEye"] = VoiceLine.ChaseSunMoon,            // 追光者之瞳 ← 「…不想追逐日月了吗」
        ["Snipe"] = VoiceLine.BoltsReady,                 // 狙击 ← 「弩箭已经装好了。」
        ["SniperScope"] = VoiceLine.BoltsReady,           // 狙击镜
        ["NoFire"] = VoiceLine.StayInDark,                // 禁止明火 ← 「我不想……留在黑暗之中。」
        ["EndlessDarkness"] = VoiceLine.StayInDark,       // 无尽黑暗
        ["Darkness"] = VoiceLine.StayInDark,              // 黑暗之下
        ["Raise"] = VoiceLine.NewFood,                    // 饲养 ← 「…过来，试试新的饲料。」
        ["Feed"] = VoiceLine.NewFood,                     // 饲料
        ["Nurture"] = VoiceLine.NewFood,                  // 哺育
        ["SandBeastBless"] = VoiceLine.NewFood,           // 沙地兽的祝福
        ["PetBlogger"] = VoiceLine.NewFood,               // 萌宠博主
        ["Ambush"] = VoiceLine.HeardMovement,             // 伏击 ← 「我听见了移动的声音。」
        ["HunterIntuition"] = VoiceLine.HeardMovement,    // 猎人直觉
        ["TargetedKilling"] = VoiceLine.HeardMovement,    // 定向猎杀
        ["GoAllOut"] = VoiceLine.ThreatGone,              // 全力以赴 ← 「我所在的方位有威胁——啊，马上就没有了。」
        ["WideareaVigilance"] = VoiceLine.GoodVisibility, // 广域警觉 ← 「视野很好…」
        ["Searchlight"] = VoiceLine.GoodVisibility,       // 探照灯
        ["SandBeast"] = VoiceLine.Squad,                  // 沙地兽 ← 「过来，沙地兽，我们要出发了。」
        ["DancingMoonlight"] = VoiceLine.Squad,           // 跳舞的月光
        ["SandBeastCraze"] = VoiceLine.Squad,             // 沙地兽狂潮
        ["SandBeastRaid"] = VoiceLine.Squad,              // 沙地兽突袭
        ["AttackIsDefense"] = VoiceLine.Deploy2,          // 以弹为盾 ← 「如果有危险，你们不要下来。」
        ["SandBeastShield"] = VoiceLine.Deploy2,          // 沙地兽之盾
        ["SandBeastRescue"] = VoiceLine.Deploy2,          // 沙地兽的拯救
        ["RayModDefend"] = VoiceLine.Deploy2,             // 防御
        ["SandBeastBackpack"] = VoiceLine.SandBeastFan,   // 沙地兽背包 ← 「探井人大都会带上一只」
    };

    /// <summary>按卡牌类名取语音；没配的返回 <see cref="VoiceLine.None"/>。</summary>
    public static VoiceLine ForCardClass(string className) =>
        CardMap.TryGetValue(className, out VoiceLine line) ? line : VoiceLine.None;

    /// <summary>配了语音的卡牌数（诊断用，应为 31）。</summary>
    public static int MappedCardCount => CardMap.Count;

    /// <summary>配了语音的卡牌类名（诊断用）。</summary>
    public static IEnumerable<string> MappedCardClasses => CardMap.Keys.OrderBy(k => k);
}
