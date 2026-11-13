shader_type spatial;
render_mode unshaded, cull_disabled, depth_prepass_alpha;

uniform sampler2D albedo_map : source_color, filter_linear_mipmap_anisotropic, repeat_disable;
uniform float alpha_scissor : hint_range(0.0, 1.0) = 0.5;
uniform float wind_strength : hint_range(0.0, 0.8) = 0.10;
uniform float wind_speed : hint_range(0.0, 6.0) = 1.4;
uniform vec2 wind_direction = vec2(0.8, 0.6);
uniform float grass_light : hint_range(0.0, 2.0) = 1.0;
varying float grass_variation;
varying float grass_warm;

void vertex() {
    // Per-instance brightness/tint from the instance id; the desert should
    // not read as one flat dark stamp.
    float h = fract(sin(float(INSTANCE_ID) * 12.9898) * 43758.5453);
    grass_variation = 0.95 + h * 0.45;
    grass_warm = fract(sin(float(INSTANCE_ID) * 78.233) * 43758.5453);
    // Bend the tip, keep the rooted base fixed. The texture's V=0 edge is the
    // top of the card, so the tip weight is 1 - UV.y; the smoothstep keeps the
    // bottom quarter rigid. Phase varies per world position so neighbouring
    // clumps do not sway in lockstep.
    float height = smoothstep(0.35, 1.0, 1.0 - clamp(UV.y, 0.0, 1.0));
    vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    float phase = dot(world.xz, vec2(0.21, 0.17)) + TIME * wind_speed;
    float gust = sin(phase) * 0.65 + sin(phase * 2.17 + 1.7) * 0.35;
    vec2 offset = normalize(wind_direction + vec2(1e-4, 0.0)) * gust * wind_strength * height;
    VERTEX.x += offset.x;
    VERTEX.z += offset.y;
}

void fragment() {
    vec4 source = texture(albedo_map, UV);
    if (source.a < alpha_scissor) discard;
    vec3 tint = vec3(1.0 + (grass_warm - 0.5) * 0.12, 1.0, 1.0 - (grass_warm - 0.5) * 0.10);
    ALBEDO = source.rgb * grass_light * grass_variation * tint;
}
