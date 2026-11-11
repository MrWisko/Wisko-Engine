$input a_position, a_color0, a_texcoord0
$output v_color0, v_texcoord0

// Screen-space UI: a_position is in logical px (top-left origin); u_viewProj is the UI ortho.
#include <bgfx_shader.sh>

void main()
{
	gl_Position = mul(u_viewProj, vec4(a_position, 0.0, 1.0));
	v_color0 = a_color0;
	v_texcoord0 = a_texcoord0;
}
