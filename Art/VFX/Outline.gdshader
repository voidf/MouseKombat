shader_type canvas_item;

// 暴露到面板的参数，可以随时调整描边颜色和粗细
uniform vec4 outline_color : source_color = vec4(.0, .0, .0, .0);
uniform float outline_width : hint_range(0.0, 512.0) = 1.0;

void fragment() {
    // 获取当前像素的颜色和透明度
    vec4 col = texture(TEXTURE, UV);
    // 计算基于像素的偏移步长
    vec2 size = TEXTURE_PIXEL_SIZE * outline_width;
    
    float alpha = 0.0;
    
    // 采样周围 8 个方向的像素 Alpha 值（类似卷积核）
    alpha += texture(TEXTURE, UV + vec2(size.x, 0.0)).a;
    alpha += texture(TEXTURE, UV + vec2(-size.x, 0.0)).a;
    alpha += texture(TEXTURE, UV + vec2(0.0, size.y)).a;
    alpha += texture(TEXTURE, UV + vec2(0.0, -size.y)).a;
    alpha += texture(TEXTURE, UV + vec2(size.x, size.y)).a;
    alpha += texture(TEXTURE, UV + vec2(-size.x, size.y)).a;
    alpha += texture(TEXTURE, UV + vec2(size.x, -size.y)).a;
    alpha += texture(TEXTURE, UV + vec2(-size.x, -size.y)).a;
    
    // 如果当前像素是透明的，但它周围存在不透明的像素，就将其染成描边颜色
    // 用 0.05 替代绝对的 0.0，增加容错率
    if (col.a < 0.05 && alpha > 0.1) {
        COLOR = vec4(outline_color.rgb, 1.0);
    } else {
        COLOR = col;
    }
}