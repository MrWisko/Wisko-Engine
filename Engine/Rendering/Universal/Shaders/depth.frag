// Depth-only pass. Alpha-masked materials cut out (uAlphaCutoff > 0), so a fence casts a fence-shaped shadow. For
// point-light cube maps (uDistanceRange > 0) the depth written is the distance to the light / range, which the
// surface shader compares with a cube shadow sampler.

in vec2 vUv;
in vec3 vWorldPos;
in float vAlpha;

uniform float uAlphaCutoff;       // 0: no cut-out
uniform float uBaseAlpha;
uniform int uHasBaseTexture;
uniform sampler2D uBaseColorTex;
uniform vec3 uLightPosition;
uniform float uDistanceRange;     // 0: ordinary depth

void main()
{
    if (uAlphaCutoff > 0.0)
    {
        float alpha = uBaseAlpha * vAlpha;
        if (uHasBaseTexture != 0)
        {
            alpha *= texture(uBaseColorTex, vUv).a;
        }

        if (alpha < uAlphaCutoff)
        {
            discard;
        }
    }

#ifdef DISTANCE_DEPTH
    // Only the point-light variant writes depth itself (it would disable early depth testing everywhere else).
    gl_FragDepth = clamp(length(vWorldPos - uLightPosition) / uDistanceRange, 0.0, 1.0);
#endif
}
