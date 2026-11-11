$input v_color0, v_texcoord0

// Solid UI geometry: rgb + alpha straight from the vertex.
#include <bgfx_shader.sh>

void main()
{
	gl_FragColor = v_color0;
}
