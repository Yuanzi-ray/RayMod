extends SpineSprite

# 让选角界面的皮肤预览**自己**循环播 Idle。
#
# ⚠️ 为什么这段逻辑在 GDScript 而不是 C#：
#   SpineSprite 是 GDExtension 类，**C# 拿不到它的托管包装** ——
#   `GetNodeOrNull<Node2D>("SkinPreview0")` 会返回 null（无头环境必现，游戏里也不保证），
#   结果就是"预览静默消失/三套叠在一起"。GDScript 是动态的，完全没这个问题。
#   所以：**C# 只命令普通 Node2D 插槽（位置/缩放/显隐），骨架自己管自己的动画。**
#
# 骨架是**异步**建起来的（图集/贴图要等帧），所以这里轮询到"骨架 + 动画列表"都有了再播。

const ANIMATION := "Idle"

## 最多等多少帧（约 2 秒）就放弃，避免永远轮询。
const MAX_FRAMES := 120

var _frames := 0
var _started := false


func _process(_delta: float) -> void:
	if _started:
		return

	_frames += 1

	var skeleton := get_skeleton()
	var state := get_animation_state()

	if skeleton != null and state != null and skeleton.get_data() != null:
		var data := skeleton.get_data()
		var anims := data.get_animations()
		for i in anims.size():
			if anims[i].get_name() == ANIMATION:
				state.set_animation(ANIMATION, true)
				_started = true
				set_process(false)
				print("[RayMod.Skin] [皮肤] 预览骨架已开始播 %s" % ANIMATION)
				return

	if _frames >= MAX_FRAMES:
		_started = true
		set_process(false)
		push_warning("[RayMod.Skin] 预览骨架 2 秒内没就绪，可能是静止的")
