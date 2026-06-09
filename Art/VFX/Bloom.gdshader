shader_type canvas_item;

// 粒子专用的发光参数
uniform vec4 flare_color : source_color = vec4(1.0, 5.0, 5.0, 1.0); // 核心发光颜色（HDR， cyanish white）
uniform float flare_intensity : hint_range(0.0, 10.0) = 3.0; // 发光强度

void fragment() {
    // 获取当前粒子的基础纹理和颜色（来自过程材质）
    vec4 tex_color = texture(TEXTURE, UV);
    
    // 1. 计算核心发光（一个 pow 函数可以创造一个非常锐利、超亮的核心）
    vec3 flare_core = flare_color.rgb * pow(tex_color.a, 3.0) * flare_intensity;
    
    // 2. 将核心发光叠加到基础纹理上
    tex_color.rgb += flare_core;
    
    // 3. 将最终颜色和粒子本身的颜色（来自过程材质）相乘
    COLOR = tex_color * COLOR;
}