$input v_color0, v_texcoord0

// Glyph quads: the R8 glyph atlas is coverage, used as alpha.
#include <bgfx_shader.sh>

SAMPLER2D(s_tex, 0);

void main()
{
	float coverage = texture2D(s_tex, v_texcoord0).r;
	gl_FragColor = vec4(v_color0.rgb, v_color0.a * coverage);
}
