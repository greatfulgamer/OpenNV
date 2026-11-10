shader_type canvas_item;
render_mode unshaded;

uniform vec4 top_color : source_color = vec4(0.016, 0.022, 0.030, 1.0);
uniform vec4 bottom_color : source_color = vec4(0.095, 0.055, 0.030, 1.0);
uniform vec4 glow_color : source_color = vec4(0.85, 0.52, 0.22, 1.0);

float hash21(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

void fragment() {
	vec2 uv = UV;
	vec3 col = mix(top_color.rgb, bottom_color.rgb, smoothstep(0.0, 1.0, uv.y));
	// Warm horizon band, subtle scanlines and a soft vignette.
	float horizon = exp(-abs(uv.y - 0.72) * 9.0);
	col += glow_color.rgb * horizon * 0.06;
	col *= 1.0 - 0.025 * step(0.5, fract(uv.y * 540.0));
	vec2 centered = (uv - 0.5) * vec2(1.15, 1.0);
	col *= 1.0 - 0.38 * smoothstep(0.42, 1.0, length(centered));
	col += (hash21(uv * 917.0) - 0.5) * 0.006;
	COLOR = vec4(col, 1.0);
}
