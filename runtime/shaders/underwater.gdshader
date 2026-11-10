shader_type canvas_item;
render_mode unshaded;

uniform vec4 tint : source_color = vec4(0.03, 0.12, 0.16, 1.0);
uniform vec4 silt_color : source_color = vec4(0.16, 0.155, 0.12, 1.0);
uniform float depth_alpha : hint_range(0.0, 1.0) = 0.45;
uniform float vignette_strength : hint_range(0.0, 1.0) = 0.55;
uniform float silt_strength : hint_range(0.0, 1.0) = 0.26;

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
	float radial = length(uv - vec2(0.5));
	float vignette = smoothstep(0.30, 0.85, radial);
	// Light falls off toward the bottom of the view and the screen edges.
	float vertical = mix(0.72, 1.0, smoothstep(1.0, 0.0, uv.y));
	float alpha = depth_alpha * vertical + vignette * vignette_strength * depth_alpha;
	vec3 col = tint.rgb * mix(0.82, 1.10, smoothstep(0.0, 0.6, uv.y));
	// Suspended silt gathers near the floor, so weight it to the lower view.
	float silt_mask = smoothstep(0.30, 1.0, uv.y);
	float silt_noise = noise(uv * vec2(13.0, 9.0) + vec2(TIME * 0.025, -TIME * 0.018)) * 0.62
		+ noise(uv * vec2(31.0, 22.0) - vec2(TIME * 0.015, TIME * 0.010)) * 0.38;
	float silt = silt_mask * silt_strength * (0.50 + 0.50 * silt_noise);
	col = mix(col, silt_color.rgb, clamp(silt, 0.0, 0.65));
	alpha = max(alpha, silt * 0.7);
	COLOR = vec4(col, clamp(alpha, 0.0, 0.9));
}
