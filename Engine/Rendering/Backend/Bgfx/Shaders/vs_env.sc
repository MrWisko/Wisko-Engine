$input a_position, a_color0
$output v_color0, v_worldPos

// Environment (baseplate): vs_unlit plus the world position for haze and fog. The clip position is
// computed exactly as in vs_unlit, so the plain environment draws the same pixels.
#include <bgfx_shader.sh>

void main()
{
	v_color0 = vec4(a_color0, 1.0);
	v_worldPos = mul(u_model[0], vec4(a_position, 1.0)).xyz;
	gl_Position = mul(u_modelViewProj, vec4(a_position, 1.0));
}
