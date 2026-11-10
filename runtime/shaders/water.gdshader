shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, fog_disabled;

uniform vec3 shallow_color : source_color = vec3(0.14, 0.42, 0.46);
uniform vec3 deep_color : source_color = vec3(0.07, 0.25, 0.30);
uniform vec3 sky_color : source_color = vec3(0.55, 0.68, 0.80);
uniform vec3 sun_color : source_color = vec3(1.0, 0.96, 0.88);
uniform sampler2D water_noise_1;
uniform sampler2D water_noise_2;
uniform sampler2D depth_texture : hint_depth_texture;
uniform sampler2D screen_texture : hint_screen_texture, filter_linear_mipmap, repeat_disable;

uniform float depth_scale = 4.0;
uniform float refraction_strength = 0.40;
uniform float normal_scale = 0.22;
uniform float time_scale = 2.0;
uniform float uv_scale = 1.2;
uniform float fresnel_power = 5.0;
uniform float reflection_strength = 0.60;
uniform float specular_strength = 0.35;
uniform float specular_power = 180.0;
uniform float foam_strength = 0.35;
uniform vec3 sun_direction = vec3(0.0, 1.0, 0.0);
uniform sampler3D water_waves : repeat_enable;
uniform float wave_height = 0.12;
uniform float wave_scale = 0.02;
uniform float refraction_quality : hint_range(0.0, 1.0) = 1.0;
uniform float refraction_blur = 6.0;

// Tier ladder: 0 disables the effect, 1 is full quality.
uniform float caustics_quality : hint_range(0.0, 1.0) = 0.70;
uniform float caustics_strength = 0.38;
uniform float caustics_scale = 0.65;
uniform float ssr_quality : hint_range(0.0, 1.0) = 0.5;
uniform float ssr_strength = 0.45;
uniform float ssr_distance = 32.0;

varying vec4 world_uv;

void vertex() {
	vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	float wave = texture(water_waves, wp * wave_scale + vec3(TIME * 0.03, 0.0, 0.0)).r;
	VERTEX.y += (wave - 0.5) * wave_height;
	world_uv = MODEL_MATRIX * vec4(VERTEX, 1.0);
}

// Depth-graded refraction blur. Multi-tap at LOD 0 keeps submerged objects
// coherent; sampling the screen-texture mips produced a blocky hatch grid.
vec3 sample_refraction(vec2 uv, float blur_px) {
	vec2 o = fwidth(uv) * max(blur_px, 0.0);
	vec3 c = textureLod(screen_texture, uv, 0.0).rgb * 0.36;
	c += textureLod(screen_texture, uv + vec2(o.x, 0.0), 0.0).rgb * 0.16;
	c += textureLod(screen_texture, uv - vec2(o.x, 0.0), 0.0).rgb * 0.16;
	c += textureLod(screen_texture, uv + vec2(0.0, o.y), 0.0).rgb * 0.16;
	c += textureLod(screen_texture, uv - vec2(0.0, o.y), 0.0).rgb * 0.16;
	return c;
}

vec2 caustic_hash(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.xx + p3.yz) * p3.zy);
}

// One voronoi layer plus a cheap ripple breakup: same organic look at roughly
// half the per-pixel cost of the old two-layer network.
float caustic_veins(vec2 p, float t) {
	vec2 q = p * caustics_scale;
	vec2 s = q + vec2(t * 0.22, t * 0.14);
	s += 0.20 * vec2(sin(s.y * 0.8 + t * 0.2), cos(s.x * 0.7 - t * 0.17));
	vec2 n = floor(s);
	vec2 f = fract(s);
	float d1 = 8.0;
	float d2 = 8.0;
	for (int j = -1; j <= 1; ++j) {
		for (int i = -1; i <= 1; ++i) {
			vec2 g = vec2(float(i), float(j));
			vec2 o = caustic_hash(n + g);
			o = 0.5 + 0.5 * sin(t * 1.7 + 6.2831853 * o);
			vec2 r = g + o - f;
			float d = dot(r, r);
			if (d < d1) { d2 = d1; d1 = d; }
			else if (d < d2) { d2 = d; }
		}
	}
	float edge = sqrt(d2) - sqrt(d1);
	float line = pow(1.0 - clamp(edge * 2.4, 0.0, 1.0), 3.0);
	float ripple = 0.5 + 0.5 * sin(q.x * 2.6 + sin(q.y * 2.3 + t * 0.35) * 1.8 + t * 0.4);
	return clamp(line * (0.65 + 0.35 * ripple), 0.0, 1.0);
}

void fragment() {
	vec2 uv = world_uv.xz * uv_scale;
	float t = TIME * time_scale;
	float dx = sin(t + (uv.x + uv.y) * 25.0) * 0.008;
	float dy = cos(t + (uv.x - uv.y) * 25.0) * 0.008;
	vec2 duv = uv + vec2(dx, dy);

	vec3 n1 = texture(water_noise_1, duv * 0.8).rgb * 2.0 - 1.0;
	vec3 n2 = texture(water_noise_2, duv * 1.7).rgb * 2.0 - 1.0;
	vec3 normal = normalize(vec3((n1.x + n2.x) * normal_scale, 1.0, (n1.y + n2.y) * normal_scale));

	if (CAMERA_POSITION_WORLD.y < world_uv.y) {
		// Snell window: the sky is visible only inside the critical angle;
		// outside it the surface reflects the dark underwater environment.
		vec3 toward_surface = normalize(world_uv.xyz - CAMERA_POSITION_WORLD);
		float zenith = clamp(toward_surface.y, 0.0, 1.0);
		// A wide transition hides the screen-space edge that can read as tearing.
		float window = smoothstep(0.30, 0.98, zenith);
		vec3 sky = mix(sky_color * 1.05, sky_color * 1.30, pow(zenith, 3.0));
		vec3 under = mix(deep_color * 0.30, sky, window);
		under *= 0.92 + 0.08 * n1.x;
		float sun_through = pow(max(dot(toward_surface, normalize(sun_direction)), 0.0), 64.0);
		under += sun_color * sun_through * 1.1 * window;
		ALBEDO = under;
	} else {
		// Low-frequency, noise-driven screen distortion. The old fixed
		// sin/cos at *25 aliased into a visible hatch over submerged objects.
		vec3 jit = texture(water_noise_1, world_uv.xz * 0.06 + vec2(t * 0.010, -t * 0.008)).rgb * 2.0 - 1.0;
		vec2 suv = SCREEN_UV + vec2(jit.x, jit.y) * (0.0035 * refraction_quality);

		float depth_r = textureLod(depth_texture, SCREEN_UV, 0.0).r;
		vec4 view = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth_r, 1.0);
		view.xyz /= view.w;
		float thickness = max(VERTEX.z - view.z, 0.0);
		float deep = clamp(thickness / depth_scale, 0.0, 1.0);

		vec3 water = mix(shallow_color, deep_color, deep);
		vec3 refraction = sample_refraction(suv, deep * refraction_blur * refraction_quality);
		vec3 base = mix(water, refraction, refraction_strength * (1.0 - deep));

		float caustic = 0.0;
		if (caustics_quality > 0.0) {
			// Slower animation and an early-out keep the surface calm and cheap.
			// Barely visible when looking straight down; keep the hint at grazing angles.
			float topdown = clamp(abs(normalize(VIEW).y), 0.0, 1.0);
			caustic = caustic_veins(world_uv.xz, TIME * 0.55) * caustics_quality * caustics_strength
				* (1.0 - 0.75 * topdown)
				* (1.0 - smoothstep(0.0, depth_scale * 1.5, thickness))
				* max(normalize(sun_direction).y, 0.0);
			base += sun_color * vec3(0.75, 0.95, 0.85) * caustic;
		}

		float grazing = 1.0 - clamp(abs(normalize(VIEW).y), 0.0, 1.0);
		float fresnel = pow(grazing, fresnel_power);
		vec3 reflection = sky_color;

		// Cheap screen-space reflection: project the mirrored ray into the
		// opaque color buffer and fold it into the sky reflection once, so
		// fresnel is not applied twice. A small blur keeps it from shimmering.
		if (ssr_quality > 0.0) {
			vec3 view_position = (VIEW_MATRIX * vec4(world_uv.xyz, 1.0)).xyz;
			vec3 view_normal = normalize((VIEW_MATRIX * vec4(normal, 0.0)).xyz);
			vec3 view_reflected = reflect(normalize(view_position), view_normal);
			vec4 ssr_clip = PROJECTION_MATRIX * vec4(view_position + view_reflected * ssr_distance, 1.0);
			if (ssr_clip.w > 0.0) {
				vec2 ssr_uv = ssr_clip.xy / ssr_clip.w * 0.5 + 0.5;
				float ssr_valid = step(0.0, ssr_uv.x) * step(ssr_uv.x, 1.0) *
					step(0.0, ssr_uv.y) * step(ssr_uv.y, 1.0);
				vec3 ssr_color = sample_refraction(clamp(ssr_uv, vec2(0.0), vec2(1.0)), 3.0 * ssr_quality);
				reflection = mix(sky_color, ssr_color, clamp(ssr_strength * ssr_quality * ssr_valid, 0.0, 1.0));
			}
		}
		vec3 color = mix(base, reflection, clamp(fresnel * reflection_strength, 0.0, 0.85));

		vec3 view_dir = normalize(-VIEW);
		vec3 sun_dir = normalize(sun_direction);
		vec3 half_dir = normalize(view_dir + sun_dir);
		float spec = pow(max(dot(normal, half_dir), 0.0), specular_power) * specular_strength * max(sun_dir.y, 0.0);
		color += sun_color * spec;

		float foam = (1.0 - smoothstep(0.0, 0.8, thickness)) * foam_strength;
		foam *= clamp(0.35 + 0.65 * (n1.x * 0.5 + 0.5), 0.0, 1.0);
		color = mix(color, vec3(0.78, 0.85, 0.88), clamp(foam, 0.0, 0.6));

		ALBEDO = color;
	}
}
