// FXAA (after Timothy Lottes' FXAA 3.11, quality preset ~12): edge-aware smoothing of the final 8-bit image. An
// alternative or addition to MSAA; it also softens edges inside textures and alpha cut-outs.

in vec2 vUv;
out vec4 FragColor;

uniform sampler2D uSource;
uniform vec2 uSourceTexel;

float lumaOf(vec3 c)
{
    return dot(c, vec3(0.299, 0.587, 0.114));
}

void main()
{
    vec2 t = uSourceTexel;
    vec3 rgbM = texture(uSource, vUv).rgb;
    float lumaM = lumaOf(rgbM);
    float lumaNW = lumaOf(texture(uSource, vUv + vec2(-1.0, -1.0) * t).rgb);
    float lumaNE = lumaOf(texture(uSource, vUv + vec2(1.0, -1.0) * t).rgb);
    float lumaSW = lumaOf(texture(uSource, vUv + vec2(-1.0, 1.0) * t).rgb);
    float lumaSE = lumaOf(texture(uSource, vUv + vec2(1.0, 1.0) * t).rgb);
    float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
    float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));
    if (lumaMax - lumaMin < max(0.0312, lumaMax * 0.125))
    {
        FragColor = vec4(rgbM, 1.0);
        return;
    }

    vec2 dir = vec2(-((lumaNW + lumaNE) - (lumaSW + lumaSE)), (lumaNW + lumaSW) - (lumaNE + lumaSE));
    float reduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * 0.03125, 1.0 / 128.0);
    float scale = 1.0 / (min(abs(dir.x), abs(dir.y)) + reduce);
    dir = clamp(dir * scale, vec2(-8.0), vec2(8.0)) * t;
    vec3 a = 0.5 * (texture(uSource, vUv + dir * (1.0 / 3.0 - 0.5)).rgb + texture(uSource, vUv + dir * (2.0 / 3.0 - 0.5)).rgb);
    vec3 b = a * 0.5 + 0.25 * (texture(uSource, vUv - dir * 0.5).rgb + texture(uSource, vUv + dir * 0.5).rgb);
    float lumaB = lumaOf(b);
    FragColor = vec4((lumaB < lumaMin || lumaB > lumaMax) ? a : b, 1.0);
}
