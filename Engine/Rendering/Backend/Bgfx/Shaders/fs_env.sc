$input v_color0, v_worldPos

// Environment colours lit by the ground light (relative to the plain environment), then haze, fog and image
// controls. The plain environment outputs the vertex colour unchanged, as fs_unlit does.
#include <bgfx_shader.sh>
#include "atmosphere.sh"

void main()
{
	if (!atmoOn())
	{
		gl_FragColor = v_color0;
		return;
	}

	vec3 color = atmoDistance(v_color0.rgb * u_atmo[8].rgb, v_worldPos);
	gl_FragColor = vec4(atmoGrade(color), 1.0);
}
