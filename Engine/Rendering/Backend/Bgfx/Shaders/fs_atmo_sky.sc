$input v_texcoord0

// Sky colour, sun disc, sky fog and image controls along the camera ray (see atmosphere.sh).
#include <bgfx_shader.sh>
#include "atmosphere.sh"

void main()
{
	vec3 dir = normalize(u_atmo[11].xyz + v_texcoord0.x * u_atmo[9].xyz + v_texcoord0.y * u_atmo[10].xyz);
	vec3 sky = atmoSky(dir);
	vec4 disc = atmoSunDisc(dir);
	sky = mix(sky, disc.rgb, disc.a);
	if (atmoOn())
	{
		sky = mix(sky, u_atmo[5].rgb, atmoSkyFog(dir));
	}

	gl_FragColor = vec4(atmoGrade(sky), 1.0);
}
