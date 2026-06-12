import os
from PIL import Image

def extend_sprite_sheet(input_path, output_path, current_grid_w, current_grid_h, target_grid_w, target_grid_h):
    """
    将 Atlas 里的每个格子（Grid）等距向外扩展到目标分辨率，并用透明像素填充。
    """
    # 1. 打开原图
    with Image.open(input_path) as img:
        img = img.convert("RGBA")  # 确保是 RGBA 通道以支持透明
        img_w, img_h = img.size

        # 2. 计算原图的行列数
        if img_w % current_grid_w != 0 or img_h % current_grid_h != 0:
            print(f"警告: 原图尺寸 ({img_w}x{img_h}) 不能被当前格子尺寸 ({current_grid_w}x{current_grid_h}) 整除！")
            return

        cols = img_w // current_grid_w
        rows = img_h // current_grid_h
        
        # 3. 计算新的大图尺寸
        new_img_w = cols * target_grid_w
        new_img_h = rows * target_grid_h
        
        # 创建一张全新的透明画布
        new_img = Image.new("RGBA", (new_img_w, new_img_h), (0, 0, 0, 0))

        # 4. 计算单个格子居中贴图时的偏移量 (Padding)
        offset_x = (target_grid_w - current_grid_w) // 2
        offset_y = (target_grid_h - current_grid_h) // 2

        if offset_x < 0 or offset_y < 0:
            print("错误: 目标格子尺寸不能小于当前格子尺寸！")
            return

        # 5. 循环切分每个小格子，并粘贴到新画布的对应居中位置
        for r in range(rows):
            for c in range(cols):
                # 计算当前格子在原图中的坐标区域
                box = (
                    c * current_grid_w,
                    r * current_grid_h,
                    (c + 1) * current_grid_w,
                    (r + 1) * current_grid_h
                )
                # 裁剪出该帧
                grid_crop = img.crop(box)

                # 计算在新画布上的目标左上角坐标（包含居中偏移）
                dest_x = c * target_grid_w + offset_x
                dest_y = r * target_grid_h + offset_y

                # 粘贴到新画布
                new_img.paste(grid_crop, (dest_x, dest_y))

        # 6. 保存新图
        new_img.save(output_path)
        print(f"成功处理! 新图已保存至: {output_path} (总尺寸: {new_img_w}x{new_img_h})")

if __name__ == "__main__":
    # --- 你可以在这里配置参数 ---
    
    # 1. 输入文件路径 (替换成你的文件名)
    input_file = r"D:\[L1]SETU\nszyGallery\gif\Ds_Kick_1\Ds_Kick_123.003.png"
    output_file = r"D:\[L1]SETU\nszyGallery\gif\Ds_Kick_1\Ds_Kick_1p.003.png"

    # 2. 当前每个格子的分辨率 (比如看你的图1是 3x3 排列，如果是256x256的总图，那每个格子大概是 85x85 等，请根据你实际美术导出时的单个格子大小填写)
    # 比如：如果原图总共 256x256，包含 2x2 个格子，那单格就是 128
    current_grid_width = 1024   
    current_grid_height = 1024  

    # 3. 你希望将【每个格子】扩展到的目标分辨率
    # 比如：如果你希望最终整张大图是 512x512，且依然是 2x2 个格子，那么目标单格就是 512 / 2 = 256
    target_grid_width = 512
    target_grid_height = 512

    # 执行转换
    if os.path.exists(input_file):
        extend_sprite_sheet(
            input_file, output_file, 
            current_grid_width, current_grid_height, 
            target_grid_width, target_grid_height
        )
    else:
        print(f"未找到输入文件: {input_file}，请检查路径。")