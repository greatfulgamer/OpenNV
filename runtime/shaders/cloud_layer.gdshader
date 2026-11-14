shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_front,
            shadows_disabled, fog_disabled;

// Transparent cloud deck that overlays the star sky.
// Dense cores reach ALPHA ~1 and hide stars; thin wisps stay see-through.
// Nothing here writes depth, so it composites over whatever is behind it.

// ---------------------------------------------------------------- geometry
uniform float deck_height : hint_range(100.0, 4000.0) = 750.0;
uniform float deck_scale  : hint_range(0.00005, 0.004) = 0.00055;
uniform vec2  wind = vec2(0.0035, 0.0012);
uniform vec2  seed = vec2(0.0);
uniform float cloud_speed : hint_range(0.0, 3.0) = 0.35;

// ---------------------------------------------------------------- shape
uniform float coverage : hint_range(0.0, 1.0) = 0.40;
uniform float softness : hint_range(0.02, 0.8) = 0.28;
uniform float erosion  : hint_range(0.0, 1.0) = 0.55;
uniform float density_curve : hint_range(0.4, 4.0) = 1.35;
uniform float density_contrast : hint_range(0.5, 5.0) = 2.2;
uniform float region_variation : hint_range(0.0, 0.7) = 0.40;

// ------------------------------------------- how much of the sky it blocks
uniform float optical_depth : hint_range(0.0, 20.0) = 5.5;
uniform float horizon_fade : hint_range(0.0, 0.4) = 0.10;

// ---------------------------------------------------------------- colour
uniform vec3 cloud_color  : source_color = vec3(0.10, 0.12, 0.17);
uniform vec3 shadow_color : source_color = vec3(0.03, 0.035, 0.05);
uniform float brightness : hint_range(0.0, 3.0) = 0.55;
uniform vec3 moon_dir = vec3(0.3, 0.5, -0.8);
uniform vec3 moon_color : source_color = vec3(0.10, 0.13, 0.20);
uniform float cloud_time = 0.0;
uniform int deck_octaves : hint_range(1, 4) = 4;
uniform int debug_view = 0;

const mat2 OCT = mat2(vec2(0.80, -0.60), vec2(0.60, 0.80));

float hash(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
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

// Normalised fBm: always 0..1 with mean ~0.5, whatever the octave count.
// Normalising is what lets `coverage` mean an actual percentage of sky
// instead of collapsing to "nothing" or "everything".
float fbm(vec2 p, int octaves, float detail) {
    float total = 0.0;
    float norm = 0.0;
    float amp = 0.5;
    for (int i = 0; i < 5; i++) {
        if (i >= octaves) break;
        float od = mix(1.0, detail, float(i) / 4.0);
        total += amp * noise(p) * od;
        norm += amp * od;
        p = OCT * p * 2.03;
        amp *= 0.5;
    }
    return total / max(norm, 1e-4);
}

void fragment() {
    vec3 dir = normalize((INV_VIEW_MATRIX * vec4(VIEW, 0.0)).xyz);

    // Project the view ray onto a horizontal deck above the camera, so banks
    // converge at the horizon and open up overhead like real clouds.
    float up = max(dir.y, 0.045);
    vec2 p = dir.xz * (deck_height / up) * deck_scale;

    vec2 drift = wind * cloud_time * cloud_speed + seed;

    // Domain warp: kills the "tiled sheet" look.
    vec2 w = vec2(fbm(p * 0.8 + drift * 0.6, 2, 1.0),
                  fbm(p * 0.8 + drift * 0.6 + vec2(5.2, 1.3), 2, 1.0)) - 0.5;
    vec2 q = p + w * 0.55;

    // Regional weather: some stretches of sky are clear, others banked up.
    float region = fbm(dir.xz * 2.2 + seed * 0.3, 2, 1.0);
    float cov = clamp(coverage + (region - 0.5) * region_variation, 0.0, 1.0);

    // Band-limit near the horizon: the projection's screen-space frequency
    // explodes there and would alias into stripes.
    float detail_fade = smoothstep(0.02, 0.30, dir.y);

    float bank = fbm(q + drift, clamp(deck_octaves, 1, 4), detail_fade);
    bank = clamp((bank - 0.5) * density_contrast + 0.5, 0.0, 1.0);

    float fine = fbm(q * 3.2 + drift * 2.0, 2, detail_fade);

    // Continuous body: 0 at the edge, 1 in the core. No binary threshold.
    float body = smoothstep(1.0 - cov, 1.0 - cov + softness, bank);
    body *= 1.0 - erosion * smoothstep(0.35, 0.75, fine);
    float d = pow(clamp(body, 0.0, 1.0), density_curve);

    // Grazing angles pass through more cloud, so the horizon thickens itself.
    float airmass = clamp(1.0 / up, 1.0, 6.0);
    float tau = d * optical_depth * airmass;
    tau *= smoothstep(0.0, horizon_fade, dir.y);

    float moon = pow(max(dot(dir, normalize(moon_dir)), 0.0), 8.0);
    vec3 cloudBody = mix(shadow_color, cloud_color, smoothstep(0.0, 0.55, d));
    cloudBody += moon_color * moon * (1.0 - d) * 0.6;
    ALBEDO = cloudBody * brightness;
    ALPHA  = (1.0 - exp(-tau)) * smoothstep(0.0, 0.12, max(dir.y, 0.0));
    if (debug_view == 1) { ALBEDO = vec3(ALPHA); ALPHA = 1.0; }
}
