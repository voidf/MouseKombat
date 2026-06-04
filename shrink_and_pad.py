import os
from PIL import Image

def process_images(input_dir, output_dir, target_canvas_size=512, target_content_size=256):
    """
    读取目录下的所有图片，将整张图缩放到 target_content_size，
    然后放到一个 target_canvas_size 的透明画布正中央（Lanczos 3 插值）。
    """
    if not os.path.exists(output_dir):
        os.makedirs(output_dir)

    # 支持的图片格式
    valid_extensions = ('.png', '.jpg', '.jpeg', '.webp')
    
    # 获取目录下所有图片文件
    files = [f for f in os.listdir(input_dir) if f.lower().endswith(valid_extensions)]
    
    if not files:
        print(f"在目录 '{input_dir}' 中没有找到有效的图片文件。")
        return

    print(f"开始处理，共找到 {len(files)} 张图片...")

    for file_name in files:
        input_path = os.path.join(input_dir, file_name)
        output_path = os.path.join(output_dir, file_name)

        with Image.open(input_path) as img:
            # 确保转换为 RGBA 模式，以支持透明通道
            img = img.convert("RGBA")
            
            # 1. 使用 Lanczos 算法将整张 512x512 的图缩放到 256x256
            # (这会将原本画得太大的角色内容完美缩小一倍)
            img_resized = img.resize((target_content_size, target_content_size), resample=Image.Resampling.LANCZOS)

            # 2. 创建一张全新的 512x512 完全透明的画布
            canvas = Image.new("RGBA", (target_canvas_size, target_canvas_size), (0, 0, 0, 0))

            # 3. 计算居中粘贴的坐标
            # (512 - 256) // 2 = 128
            offset_x = (target_canvas_size - target_content_size) // 2
            offset_y = (target_canvas_size - target_content_size) // 2

            # 4. 将缩小后的内容粘贴到透明画布中心
            canvas.paste(img_resized, (offset_x, offset_y), img_resized)

            # 5. 保存结果
            canvas.save(output_path)
            print(f"已处理: {file_name} -> 缩放并居中成功")

    print(f"\n所有图片处理完成！已保存至目录: {output_dir}")

if __name__ == "__main__":
    # --- 参数配置 ---
    # 输入目录：'.' 代表当前脚本所在目录，你也可以换成绝对路径如 "C:/Users/Game/Sprites"
    input_directory = r"D:\[L1]SETU\nszyGallery\gif\Cs_Atk_1"  
    
    # 输出目录：处理后的图片会存放在该文件夹下，避免覆盖原图
    output_directory = "./processed_frames"  

    # 运行处理
    process_images(
        input_dir=input_directory, 
        output_dir=output_directory, 
        target_canvas_size=512,      # 最终画布大小
        target_content_size=256      # 内容缩放到的目标大小
    )