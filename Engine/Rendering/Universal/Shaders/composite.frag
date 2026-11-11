// The HDR image to the display: bloom added, exposure, white balance, saturation, contrast, tone mapping (common.glsl),
// an optional vignette, then sRGB encoding. Written once per frame into the 8-bit target the editor overlays share.

in vec2 vUv;
out vec4 FragColor;

uniform sampler2D uScene;
uniform sampler2D uBloom;
uniform int uBloomOn;
uniform float uBloomIntensity;
uniform vec2 uVignette;     // intensity (0 off), roundness
uniform vec4 uSourceRect;   // where the image lies in uScene (render scale): offset xy, size zw in 0-1

void main()
{
    vec2 uv = uSourceRect.xy + vUv * uSourceRect.zw;
    vec3 color = texture(uScene, uv).rgb;
    if (uBloomOn != 0)
    {
        color += texture(uBloom, uv).rgb * uBloomIntensity;
    }

    vec3 graded = gradeLinear(color);
    if (uVignette.x > 0.0)
    {
        vec2 d = (vUv - 0.5) * vec2(1.0, mix(1.0, 0.75, uVignette.y));
        float v = 1.0 - smoothstep(0.35, 0.95, length(d) * 1.25);
        graded *= mix(1.0, v, uVignette.x);
    }

    FragColor = vec4(linearToSrgb(graded), 1.0);
}
