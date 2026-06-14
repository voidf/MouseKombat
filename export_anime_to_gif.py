import os
import re
from PIL import Image

def convert_res_path(res_path, project_root):
    """将 Godot 的 res:// 路径转换为本地系统路径"""
    return os.path.join(project_root, res_path.replace("res://", ""))

def export_godot_animations(tscn_path, output_dir):
    if not os.path.exists(output_dir):
        os.makedirs(output_dir)

    project_root = os.path.dirname(os.path.abspath(tscn_path))
    
    with open(tscn_path, 'r', encoding='utf-8') as f:
        content = f.read()

    # 1. 建立资源映射表 (id -> 文件路径)
    ext_resources = {}
    for match in re.finditer(r'\[ext_resource.*?path="([^"]+)".*?id="([^"]+)"\]', content):
        ext_resources[match.group(2)] = match.group(1)

    # 2. 建立 AtlasTexture 映射表 (id -> {img_path, region})
    atlas_textures = {}
    atlas_regex = r'\[sub_resource type="AtlasTexture" id="([^"]+)"\]\natlas = ExtResource\("([^"]+)"\)\nregion = Rect2\(([^)]+)\)'
    for match in re.finditer(atlas_regex, content):
        tex_id = match.group(1)
        ext_id = match.group(2)
        region = [float(x) for x in match.group(3).split(',')]
        atlas_textures[tex_id] = {
            'img_path': ext_resources.get(ext_id),
            'region': region  # x, y, w, h
        }

    # 3. 解析所有的 SpriteFrames 动画块
    # 按 SpriteFrames 分割场景文本，避免不同角色的同名动画混乱
    sf_blocks = re.split(r'\[sub_resource type="SpriteFrames" id="([^"]+)"\]', content)[1:]
    
    # 图片缓存，避免多次读取同一张大图 Atlas
    loaded_images = {}

    for i in range(0, len(sf_blocks), 2):
        sf_id = sf_blocks[i]
        sf_content = sf_blocks[i+1]

        # 匹配单个动画的配置: "frames": [...], ..., "name": &"xxx", "speed": 12.0
        anim_regex = r'"frames": \[(.*?)\].*?"name":\s*&"([^"]+)".*?"speed":\s*([\d.]+)'
        
        for anim_match in re.finditer(anim_regex, sf_content, re.DOTALL):
            frames_content = anim_match.group(1)
            anim_name = anim_match.group(2)
            speed = float(anim_match.group(3))

            # 匹配具体的帧配置
            frame_regex = r'"duration":\s*([\d.]+).*?"texture":\s*SubResource\("([^"]+)"\)'
            frames = []
            for f_match in re.finditer(frame_regex, frames_content, re.DOTALL):
                frames.append({
                    'duration': float(f_match.group(1)),
                    'tex_id': f_match.group(2)
                })

            if not frames:
                continue

            gif_frames = []
            gif_durations = []
            
            # 计算基准帧长 (毫秒)。Godot 的 speed 就是 FPS。
            base_frame_time_ms = 1000.0 / speed if speed > 0 else 100.0

            for frame in frames:
                tex_data = atlas_textures.get(frame['tex_id'])
                if not tex_data:
                    continue

                img_res_path = tex_data['img_path']
                if not img_res_path:
                    continue

                img_local_path = convert_res_path(img_res_path, project_root)
                
                # 读取并缓存图片
                if img_local_path not in loaded_images:
                    if not os.path.exists(img_local_path):
                        print(f"[!] 找不到图片: {img_local_path}")
                        continue
                    loaded_images[img_local_path] = Image.open(img_local_path).convert("RGBA")
                
                atlas_img = loaded_images[img_local_path]
                
                # 裁切出 Atlas 中的单帧
                x, y, w, h = tex_data['region']
                cropped = atlas_img.crop((x, y, x+w, y+h))
                
                # 创建一个纯透明背景，方便 GIF 叠加处理
                # 这能有效防止 GIF 导出时边缘出现黑色杂边或图层残留
                frame_canvas = Image.new("RGBA", cropped.size, (255, 255, 255, 0))
                frame_canvas.alpha_composite(cropped)
                
                gif_frames.append(frame_canvas)

                # 将 Godot 里的 duration 权重乘入实际播放时间
                actual_duration = base_frame_time_ms * frame['duration']
                gif_durations.append(int(actual_duration))

            # 导出 GIF
            if gif_frames:
                # 命名格式: SpriteFramesID_动画名.gif，防止仓鼠和袋鼠的重名动画（比如 WALK）相互覆盖
                file_name = f"{sf_id}_{anim_name}.gif"
                out_path = os.path.join(output_dir, file_name)
                
                gif_frames[0].save(
                    out_path,
                    format='GIF',
                    save_all=True,
                    append_images=gif_frames[1:],
                    duration=gif_durations,
                    loop=0,
                    disposal=2  # Disposal Method 2 (Restore to Background Color)，对透明 GIF 必选项
                )
                print(f"[√] 已导出: {file_name} (共 {len(gif_frames)} 帧)")

if __name__ == "__main__":
    # 配置你要解析的场景文件路径，以及输出的文件夹
    TSCN_FILE = "MFEntry.tscn" 
    OUTPUT_DIR = "Exported_GIFs"
    
    if os.path.exists(TSCN_FILE):
        print(f"开始解析 {TSCN_FILE} ...\n" + "-"*30)
        export_godot_animations(TSCN_FILE, OUTPUT_DIR)
        print("-"*30 + "\n提取完成！")
    else:
        print(f"错误: 找不到场景文件 {TSCN_FILE}，请检查路径。")