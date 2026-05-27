import os
import sys
import math
from PIL import Image

def find_best_factors(n):
    """
    找到两个因数 a, b (a >= b)，使得 a * b = n 且 a, b 尽可能接近。
    通过从 sqrt(n) 向下搜索第一个能整除 n 的数 b。
    返回 (a, b)
    """
    for b in range(int(math.sqrt(n)), 0, -1):
        if n % b == 0:
            return n // b, b
    return n, 1  # fallback


def main():
    if len(sys.argv) != 2:
        print(f"用法: {sys.argv[0]} <图片目录>")
        sys.exit(1)

    dir_path = sys.argv[1]
    if not os.path.isdir(dir_path):
        print(f"错误: {dir_path} 不是一个目录")
        sys.exit(1)

    # 获取所有 .png 文件（非递归）
    files = [f for f in os.listdir(dir_path) if f.lower().endswith('.png')]
    if not files:
        print("目录中未找到任何 .png 文件。")
        sys.exit(1)
    files.sort()  # 按文件名字典序排序

    # 使用第一个图像的尺寸作为所有图像的 w, h
    first_img = Image.open(os.path.join(dir_path, files[0]))
    w, h = first_img.size
    n = len(files)
    print(f"w={w} h={h} n={n}")

    # 计算因数 a, b
    a, b = find_best_factors(n)
    # 保证 a >= b
    if a < b:
        a, b = b, a

    # 根据图像宽高比确定行列数
    if w > h:
        cols, rows = b, a
    else:
        cols, rows = a, b

    atlas_w = w * cols
    atlas_h = h * rows
    atlas = Image.new('RGBA', (atlas_w, atlas_h))

    # 拼接图像
    for idx, fname in enumerate(files):
        img = Image.open(os.path.join(dir_path, fname))
        if img.mode != 'RGBA':
            img = img.convert('RGBA')
        row = idx // cols
        col = idx % cols
        x = col * w
        y = row * h
        atlas.paste(img, (x, y))

    # 保存结果
    output_path = os.path.join(dir_path, 'atlas.png')
    atlas.save(output_path)
    print(f"图集已保存到 {output_path}")


if __name__ == "__main__":
    main()
