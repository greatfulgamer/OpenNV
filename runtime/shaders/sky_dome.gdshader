shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

uniform vec3 top_color : source_color = vec3(0.10, 0.28, 0.62);
uniform vec3 bottom_color : source_color = vec3(0.52, 0.68, 0.84);
uniform vec3 haze_color : source_color = vec3(0.72, 0.78, 0.86);
uniform vec3 sun_color : source_color = vec3(1.0, 0.92, 0.78);
uniform vec3 sun_direction = vec3(0.0, 1.0, 0.0);
uniform vec4 cloud_color : source_color = vec4(0.92, 0.94, 0.98, 1.0);
uniform vec3 cloud_shadow_color : source_color = vec3(0.45, 0.50, 0.60);
uniform float cloud_speed : hint_range(0.0, 5.0) = 0.25;
uniform float cloud_coverage : hint_range(0.0, 1.0) = 0.42;
uniform float cloud_scale : hint_range(0.1, 20.0) = 7.0;
uniform float cloud_brightness : hint_range(0.3, 2.0) = 0.95;
uniform float cloud_sharpness : hint_range(0.02, 0.5) = 0.16;
uniform float cloud_softness : hint_range(0.0, 1.0) = 0.35;
uniform float sun_glow : hint_range(0.0, 2.0) = 0.6;
uniform float horizon_glow : hint_range(0.0, 2.0) = 0.45;
uniform float haze_strength : hint_range(0.0, 1.0) = 0.35;
uniform float star_intensity : hint_range(0.0, 2.0) = 0.0;

uniform vec2 wind_direction = vec2(0.85, 0.28);
uniform float cirrus_coverage : hint_range(0.0, 1.0) = 0.34;
uniform float cirrus_opacity : hint_range(0.0, 1.0) = 0.38;
uniform float cirrus_scale : hint_range(0.1, 40.0) = 13.0;
uniform float cirrus_speed_mult : hint_range(1.0, 5.0) = 1.8;
uniform sampler2D star_field : repeat_enable;
uniform bool use_star_field = false;
uniform int cloud_octaves : hint_range(1, 5) = 5;
uniform float star_density : hint_range(0.0, 2.0) = 1.0;

varying vec3 world_pos;

float hash(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

vec3 hash3(vec3 p) {
    p = fract(p * vec3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return fract((p.xxy + p.yxx) * p.zyx);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash(i);
    float b = hash(i + vec2(1.0, 0.0));
    float c = hash(i + vec2(0.0, 1.0));
    float d = hash(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

const mat2 OCT = mat2(vec2(0.80, -0.60), vec2(0.60, 0.80));

float fbm(vec2 p, float detail) {
    float total = 0.0;
    float amp = 0.5;
    for (int i = 0; i < 5; i++) {
        if (i >= cloud_octaves) break;
        // Near the horizon the projection's screen-space frequency explodes;
        // band-limit the high octaves instead of aliasing them into stripes.
        float octave_detail = mix(1.0, detail, float(i) / 4.0);
        total += amp * noise(p) * octave_detail;
        p = OCT * p * 2.03;
        amp *= 0.5;
    }
    return total;
}

float star_points(vec3 dir) {
    vec3 p = dir * 140.0;
    vec3 cell = floor(p);
    vec3 f = fract(p) - 0.5;
    vec3 rnd = hash3(cell);
    if (rnd.x < 0.975) return 0.0;
    vec3 offset = (hash3(cell + 7.0) - 0.5) * 0.55;
    float d = length(f - offset);
    float size = 0.22 + 0.30 * rnd.y;
    float mag = 0.35 + 0.85 * rnd.z;
    float twinkle = 0.55 + 0.45 * sin(TIME * (0.4 + 2.2 * rnd.y) + rnd.z * 6.2831);
    return smoothstep(size, 0.0, d) * mag * twinkle;
}

float cloud_density(vec2 uv, float coverage, float sharpness, float softness, float warp_amount, float detail) {
    vec2 warp = vec2(fbm(uv * 0.6 + vec2(1.7, 9.2), detail), fbm(uv * 0.6 + vec2(8.3, 2.8), detail));
    vec2 wuv = uv + (warp - 0.5) * warp_amount;
    float n1 = fbm(wuv, detail);
    float n2 = fbm(OCT * wuv * 1.87 + vec2(2.1, 3.4), detail);
    float density = n1 * 0.65 + n2 * 0.35;
    float cluster = smoothstep(0.25, 0.75, fbm(uv * 0.25 + vec2(11.0, 5.0), detail));
    density *= mix(0.85, 1.0, cluster);
    float edge = sharpness + softness * (1.0 - density);
    return smoothstep(1.0 - coverage, 1.0 - coverage + edge, density);
}

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    vec3 dir = normalize(world_pos - CAMERA_POSITION_WORLD);
    float ed = dir.y;

    float gradient = clamp(pow(max(ed, 0.0), 0.55), 0.0, 1.0);
    vec3 col = mix(bottom_color, top_color, gradient);

    float horizon = exp(-abs(ed) * 7.0);
    col = mix(col, haze_color, clamp(horizon * haze_strength, 0.0, 1.0));
    col += bottom_color * horizon * horizon_glow;

    vec3 sd = normalize(sun_direction);
    float dusk_amt = exp(-abs(sd.y) * 9.0);
    float sun_dot = max(dot(dir, sd), 0.0);
    col += sun_color * pow(sun_dot, 900.0) * sun_glow * 1.6;
    col += sun_color * pow(sun_dot, 18.0) * sun_glow * 0.12;

    if (star_intensity > 0.0) {
        float night_mask = 1.0 - smoothstep(-0.10, 0.0, sd.y);
        float mask = smoothstep(0.02, 0.30, ed) * night_mask;
        if (mask > 0.001) {
            if (use_star_field) {
                float u = atan(dir.z, dir.x) / 6.2831853 + 0.5;
                float v = asin(clamp(dir.y, -1.0, 1.0)) / 3.14159265 + 0.5;
                vec3 tex_stars = texture(star_field, vec2(u, v)).rgb;
                col = max(col, tex_stars * star_intensity * 1.3 * mask);
            }
            col += vec3(star_points(dir)) * star_intensity * mask * 3.0 * star_density;
        }
    }

    if (ed > -0.05) {
        float horizon_fade = smoothstep(-0.02, 0.30, ed);
        float proj = 1.0 / (abs(ed) + 0.30);
        vec2 wind = normalize(wind_direction + vec2(1e-4, 0.0));
        vec2 base_uv = dir.xz * proj;

        vec2 uv1 = base_uv * cloud_scale + wind * TIME * cloud_speed * 0.6;
        float cloud_detail = smoothstep(0.02, 0.35, ed);
        float cumulus = cloud_density(uv1, cloud_coverage, cloud_sharpness, cloud_softness, 1.0, cloud_detail);

        vec2 uv2 = OCT * (base_uv * cirrus_scale) + wind * TIME * cloud_speed * cirrus_speed_mult * 0.6;
        float cirrus = cloud_density(uv2, cirrus_coverage, 0.10, 0.60, 0.6, cloud_detail) * cirrus_opacity;

        vec3 cirrus_col = mix(cloud_shadow_color, cloud_color.rgb, 0.65) * cloud_brightness;
        col = mix(col, cirrus_col, cirrus * pow(horizon_fade, 1.4) * 0.85);

        vec3 cloud_shade = mix(cloud_shadow_color, cloud_color.rgb, smoothstep(0.0, 0.75, cumulus));
        vec3 cloud_final = cloud_shade * cloud_brightness;
        cloud_final += sun_color * pow(sun_dot, 4.0) * (0.05 + 0.30 * dusk_amt);
        col = mix(col, cloud_final, cumulus * 0.92 * horizon_fade);
    }

    ALBEDO = col;
}
