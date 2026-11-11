$input a_position, a_color0, a_normal
$output v_color0, v_worldPos, v_normal

// Port of Renderer's lit GLSL (part surfaces). World position feeds the derivative-normal fallback.
#include <bgfx_shader.sh>

// Inverse-transpose of the model's upper 3x3 (computed on the CPU per draw, exactly the GL shader's
// transpose(inverse(mat3(uModel)))), so non-uniform scaling shades correctly. A zero vertex normal
// (flat primitives) stays zero and triggers the derivative fallback in the fragment stage.
uniform mat4 u_normalMatrix;

void main()
{
	v_color0 = vec4(a_color0, 1.0);
	vec4 world = mul(u_model[0], vec4(a_position, 1.0));
	v_worldPos = world.xyz;
	v_normal = mul(u_normalMatrix, vec4(a_normal, 0.0)).xyz;
	gl_Position = mul(u_viewProj, world);
}
