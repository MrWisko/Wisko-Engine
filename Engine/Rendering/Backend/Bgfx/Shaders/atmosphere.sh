// Wisko Atmosphere V1: world-shader functions shared by both backends. OpenGL prepends this text to its
// GLSL 330 sources (embedded resource); bgfx shaders #include it after <bgfx_shader.sh>. Plain GLSL only (no bgfx
// macros), so the same lines compile for both.
//
// u_atmo, packed by Engine.Atmosphere.AtmosphereUniforms:
//   [0]  direction toward the sun (xyz), sun disc angular radius (w; 0 = no disc)
//   [1]  sun light = colour x intensity (rgb), sky halo strength (w)
//   [2]  ambient light (rgb), exposure multiplier (w)
//   [3]  sky colour at the zenith (rgb), saturation (w)
//   [4]  sky colour at the horizon (rgb), contrast (w)
//   [5]  fog colour (rgb), fog density (w; 0 = no fog)
//   [6]  fog start, fog end, haze amount, haze distance
//   [7]  camera position (xyz), mode (w; 0 = plain environment, 1 = Atmosphere)
//   [8]  ground light relative to the plain environment (rgb), highlight roll-off knee (w)
//   [9]  camera right x tan(horizontal half fov), [10] camera up x tan(vertical half fov), [11] camera forward
//   [12] sun disc colour (rgb), sun disc visibility (w)
// Mode 0 is the plain environment and reproduces the renderer from before Atmosphere exactly: flat sky colour, the
// same light, no haze, fog or grading (only the old clamp at 1).

uniform vec4 u_atmo[13];

bool atmoOn()
{
	return u_atmo[7].w > 0.5;
}

// Sky colour along a normalized world direction, without the sun disc. Horizon to zenith with an exponential
// falloff (more air toward the horizon), a slightly darker band below the horizon, and a forward-scattering halo
// around the sun tinted by its light (Henyey-Greenstein phase function, g = 0.8), wider near the horizon.
vec3 atmoSky(vec3 dir)
{
	if (!atmoOn())
	{
		return u_atmo[3].rgb;
	}

	float t = 1.0 - exp(-10.0 * max(dir.y, 0.0));
	vec3 sky = mix(u_atmo[4].rgb, u_atmo[3].rgb, t);
	sky = mix(sky, u_atmo[4].rgb * 0.88, clamp(-dir.y * 8.0, 0.0, 1.0));
	float g = 0.8;
	float mu = dot(dir, u_atmo[0].xyz);
	float phase = (1.0 - g * g) / pow(max(1.0 + g * g - 2.0 * g * mu, 0.0001), 1.5);
	return sky + u_atmo[1].rgb * (u_atmo[1].w * phase * (1.0 + 1.5 * (1.0 - t)));
}

// Sun disc and glow (rgb, alpha) for a sky direction; the plain environment's sun has the same shape.
vec4 atmoSunDisc(vec3 dir)
{
	float radius = u_atmo[0].w;
	if (radius <= 0.0)
	{
		return vec4(0.0, 0.0, 0.0, 0.0);
	}

	float dist = acos(clamp(dot(dir, u_atmo[0].xyz), -1.0, 1.0)) / radius;
	float disk = 1.0 - smoothstep(0.22, 0.32, dist);
	float glow = (1.0 - smoothstep(0.32, 1.0, dist)) * 0.75;
	float alpha = clamp(disk + glow, 0.0, 1.0) * u_atmo[12].w;
	if (atmoOn())
	{
		alpha *= smoothstep(-0.01, 0.02, dir.y);
	}

	return vec4(u_atmo[12].rgb, alpha);
}

// Fog amount for the sky itself (infinitely far): full density at the horizon, thinner overhead.
float atmoSkyFog(vec3 dir)
{
	return u_atmo[5].w * (1.0 - 0.65 * smoothstep(0.0, 0.5, dir.y));
}

// Distance effects for a world-space surface point: haze (aerial perspective: far geometry takes on the colour of
// the sky behind it; ground directions use the horizon), then fog (from Start to End, up to Density).
vec3 atmoDistance(vec3 color, vec3 worldPos)
{
	if (!atmoOn())
	{
		return color;
	}

	vec3 toPoint = worldPos - u_atmo[7].xyz;
	float dist = length(toPoint);
	vec3 dir = toPoint / max(dist, 0.0001);
	float haze = u_atmo[6].z * (1.0 - exp(-dist / u_atmo[6].w));
	vec3 hazeDir = normalize(vec3(dir.x, max(dir.y, 0.0) + 0.0001, dir.z));
	color = mix(color, atmoSky(hazeDir), haze);
	float fog = smoothstep(u_atmo[6].x, u_atmo[6].y, dist) * u_atmo[5].w;
	return mix(color, u_atmo[5].rgb, fog);
}

// Final colour: exposure, saturation, contrast, then a soft roll-off above the knee instead of a hard clip at 1.
// The plain environment keeps the old clamp only.
vec3 atmoGrade(vec3 color)
{
	if (!atmoOn())
	{
		return min(color, vec3(1.0, 1.0, 1.0));
	}

	vec3 c = color * u_atmo[2].w;
	float luma = dot(c, vec3(0.2126, 0.7152, 0.0722));
	c = max(mix(vec3(luma, luma, luma), c, u_atmo[3].w), vec3(0.0, 0.0, 0.0));
	c = max((c - vec3(0.5, 0.5, 0.5)) * u_atmo[4].w + vec3(0.5, 0.5, 0.5), vec3(0.0, 0.0, 0.0));
	float knee = u_atmo[8].w;
	vec3 over = max(c - vec3(knee, knee, knee), vec3(0.0, 0.0, 0.0));
	vec3 rolled = vec3(knee, knee, knee) + (1.0 - knee) * (vec3(1.0, 1.0, 1.0) - exp(-over / (1.0 - knee)));
	return mix(c, rolled, step(vec3(knee, knee, knee), c));
}
