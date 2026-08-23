@tool
extends EditorExportPlugin
## Heroes/*/images is .gdignore'd so the Godot editor does not import hundreds of frame
## PNGs, but that also removes the folder from the normal export pipeline (include_filter does
## NOT override .gdignore) — exported MKEditor/game then found no frame art. This hook injects
## those PNGs (and raw SoundFXOGG/*.ogg, also .gdignore'd) into the pck as raw files, which is
## exactly what HeroLibrary.PackHeroAtlas reads through FileAccess.


func _get_name() -> String:
	return "MK Hero Raw Asset Export"


func _export_begin(_features: PackedStringArray, _is_debug: bool, _path: String, _flags: int) -> void:
	_add_raw("res://Heroes", "Heroes")
	_add_raw("res://SoundFXOGG", "SoundFXOGG")


func _add_raw(src: String, virt: String) -> void:
	var dir := DirAccess.open(src)
	if dir == null:
		return
	dir.list_dir_begin()
	var f := dir.get_next()
	while f != "":
		if f.begins_with("."):
			f = dir.get_next()
			continue
		var full := src + "/" + f
		var v := virt + "/" + f
		if dir.current_is_dir():
			_add_raw(full, v)
		else:
			var ext := f.get_extension().to_lower()
			var is_frame_png := virt.begins_with("Heroes/") and "/images/" in v and ext == "png"
			var is_sound_ogg := virt.begins_with("SoundFXOGG/") and ext == "ogg"
			if is_frame_png or is_sound_ogg:
				var bytes := FileAccess.get_file_as_bytes(full)
				if bytes.size() > 0:
					add_file(v, bytes, false)
		f = dir.get_next()
	dir.list_dir_end()
