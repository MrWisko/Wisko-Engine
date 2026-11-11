$input a_position
$output v_texcoord0

// Atmosphere sky: one full-screen triangle (clip-space corners in a_position.xy), drawn before the world with no
// depth test or write. The clip-space corner reaches the fragment stage as the view ray's screen position.
#include <bgfx_shader.sh>

void main()
{
	v_texcoord0 = a_position.xy;
	gl_Position = vec4(a_position.xy, 0.5, 1.0);
}
