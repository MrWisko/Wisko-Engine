$input v_color0, v_worldPos, v_normal

// Port of Renderer's lit GLSL: smooth vertex normal when supplied, else the flat face normal from
// world-position derivatives; orientation from gl_FrontFacing only; the Atmosphere sun + ambient light, then haze,
// fog and image controls (atmosphere.sh; the plain environment keeps the former light and clamp exactly).
#include <bgfx_shader.sh>
#include "atmosphere.sh"

void main()
{
	vec3 normal = vec3(0.0, 0.0, 0.0);
	bool hasNormal = true;
	if (length(v_normal) > 1e-4)
	{
		normal = normalize(v_normal);
	}
	else
	{
		vec3 faceCross = cross(dFdx(v_worldPos), dFdy(v_worldPos));
		float faceLen = length(faceCross);
		if (faceLen > 1e-8)
		{
			normal = faceCross / faceLen;
		}
		else
		{
			hasNormal = false;
		}
	}

	float diffuse = 0.0;
	if (hasNormal)
	{
		if (!gl_FrontFacing)
		{
			normal = -normal;
		}
		diffuse = max(dot(normal, u_atmo[0].xyz), 0.0);
	}

	vec3 lit = v_color0.rgb * (u_atmo[2].rgb + u_atmo[1].rgb * diffuse);
	gl_FragColor = vec4(atmoGrade(atmoDistance(lit, v_worldPos)), 1.0);
}
