shader_type canvas_item;
render_mode unshaded;

uniform vec4 tint : source_color = vec4(0.03, 0.12, 0.16, 1.0);
uniform float depth_alpha : hint_range(0.0, 1.0) = 0.45;
uniform float vignette_strength : hint_range(0.0, 1.0) = 0.55;

void fragment() {
	vec2 uv = UV;
	float radial = length(uv - vec2(0.5));
	float vignette = smoothstep(0.30, 0.85, radial);
	// Light falls off toward the bottom of the view and the screen edges.
	float vertical = mix(0.72, 1.0, smoothstep(1.0, 0.0, uv.y));
	float alpha = depth_alpha * vertical + vignette * vignette_strength * depth_alpha;
	vec3 col = tint.rgb * mix(0.82, 1.10, smoothstep(0.0, 0.6, uv.y));
	COLOR = vec4(col, clamp(alpha, 0.0, 0.85));
}
