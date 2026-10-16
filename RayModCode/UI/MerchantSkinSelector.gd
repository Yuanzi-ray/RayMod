extends Node2D

# 让**商店里的莱伊**跟随玩家选的皮肤。
#
# ⚠️ 为什么这件事用 GDScript 做、而且直接读配置文件：
#   SpineSprite 是 GDExtension 类，**C# 拿不到它的托管包装**（GetNodeOrNull / GetChild 都可能拿到 null），
#   所以"切显隐"这种要碰到骨架节点的活在 C# 里很脆。GDScript 是动态的，没有这个问题。
#   而"玩家选了哪套皮肤"的权威来源是 C# 的 RaySkinState → 它写的就是这个文件
#   （user://raymod_skin.cfg，section=skin，key=selected，值是枚举名），所以两边天然一致。
#
# ⚠️ ORDER 的顺序必须和 C# 的 RaySkinCatalog.Entries 一致（枚举名不能改，改了就丢存档）。

const CONFIG_PATH := "user://raymod_skin.cfg"
const CONFIG_SECTION := "skin"
const CONFIG_KEY := "selected"

const ORDER := ["Summer", "Shining", "Original"]

const SLOT_PREFIX := "MerchantSlot"


func _ready() -> void:
	var skin := ORDER[0]

	var config := ConfigFile.new()
	if config.load(CONFIG_PATH) == OK:
		skin = str(config.get_value(CONFIG_SECTION, CONFIG_KEY, ORDER[0]))

	var index := ORDER.find(skin)
	if index < 0:
		index = 0   # 存档里有不认识的值 → 用默认皮肤，绝不留下"三个都不显示"

	var found := 0
	for i in ORDER.size():
		var slot := get_node_or_null("%s%d" % [SLOT_PREFIX, i])
		if slot == null:
			continue
		found += 1
		slot.visible = (i == index)

	print("[RayMod.Skin] [皮肤] 商店形象跟随皮肤：%s（插槽 %d/%d）" % [skin, found, ORDER.size()])
