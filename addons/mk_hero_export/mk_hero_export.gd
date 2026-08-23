@tool
extends EditorPlugin
## Registers the raw-asset export hook. The hook itself lives in hero_raw_export.gd.

var _exporter = null


func _enter_tree():
	if _exporter == null:
		_exporter = preload("res://addons/mk_hero_export/hero_raw_export.gd").new()
	add_export_plugin(_exporter)


func _exit_tree():
	if _exporter != null:
		remove_export_plugin(_exporter)
		_exporter = null
