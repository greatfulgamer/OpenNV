shader_type canvas_item;
render_mode blend_add, unshaded;

uniform vec2 sun_uv = vec2(0.5, 0.12);
uniform float intensity = 0.0;
uniform vec3 ray_color : source_color = vec3(0.55, 0.85, 1.0);
uniform float time_scale = 0.30;

float hash21(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

float noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = hash21(i);
	float b = hash21(i + vec2(1.0, 0.0));
	float c = hash21(i + vec2(0.0, 1.0));
	float d = hash21(i + vec2(1.0, 1.0));
	return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

void fragment() {
	vec2 uv = UV;
	vec2 d = uv - sun_uv;
	float r = length(d);
	float a = atan(d.y, d.x);
	// Angular streaks radiating from the sun, scrolling outward over time.
	float streak = noise(vec2(a * 14.0, r * 3.0 - TIME * time_scale));
	streak = mix(streak, noise(vec2(a * 29.0, r * 5.0 - TIME * time_scale * 1.3)), 0.35);
	streak = smoothstep(0.25, 1.0, streak);
	// Fade right at the sun and well before the far edges; light dies with depth.
	float falloff = smoothstep(0.05, 0.28, r) * (1.0 - smoothstep(0.38, 1.10, r));
	float depth_fade = mix(0.30, 1.0, smoothstep(1.0, 0.0, uv.y));
	float rays = streak * falloff * depth_fade * intensity;
	COLOR = vec4(ray_color * rays, rays);
}
