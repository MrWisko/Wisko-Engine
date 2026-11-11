$input a_position, a_color0
$output v_color0

// Port of Renderer's unlit GLSL: environment, outlines, gizmos, label backgrounds.
#include <bgfx_shader.sh>

void main()
{
	v_color0 = vec4(a_color0, 1.0);
	gl_Position = mul(u_modelViewProj, vec4(a_position, 1.0));
}
