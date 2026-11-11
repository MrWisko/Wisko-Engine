// Bloom (progressive downsample / upsample, as in "Next Generation Post Processing in Call of Duty: Advanced
// Warfare"). uMode 0: prefilter (soft-knee threshold, Karis average against fireflies) + first downsample;
// 1: 13-tap downsample; 2: 9-tap tent upsample, blended additively onto the next larger level.

in vec2 vUv;
out vec4 FragColor;

uniform sampler2D uSource;
uniform vec2 uSourceTexel;     // 1 / source size
uniform int uMode;
uniform vec4 uThreshold;       // threshold, knee, clamp, upsample weight (radius)

vec3 tap(vec2 uv)
{
    return texture(uSource, uv).rgb;
}

float karisWeight(vec3 c)
{
    return 1.0 / (1.0 + max(c.r, max(c.g, c.b)));
}

vec3 downsample13(vec2 uv, bool karis)
{
    vec2 t = uSourceTexel;
    vec3 a = tap(uv + t * vec2(-2.0, -2.0));
    vec3 b = tap(uv + t * vec2(0.0, -2.0));
    vec3 c = tap(uv + t * vec2(2.0, -2.0));
    vec3 d = tap(uv + t * vec2(-1.0, -1.0));
    vec3 e = tap(uv + t * vec2(1.0, -1.0));
    vec3 f = tap(uv + t * vec2(-2.0, 0.0));
    vec3 g = tap(uv);
    vec3 h = tap(uv + t * vec2(2.0, 0.0));
    vec3 i = tap(uv + t * vec2(-1.0, 1.0));
    vec3 j = tap(uv + t * vec2(1.0, 1.0));
    vec3 k = tap(uv + t * vec2(-2.0, 2.0));
    vec3 l = tap(uv + t * vec2(0.0, 2.0));
    vec3 m = tap(uv + t * vec2(2.0, 2.0));
    if (!karis)
    {
        return (d + e + i + j) * 0.125 + (a + b + g + f) * 0.03125 + (b + c + h + g) * 0.03125
            + (f + g + l + k) * 0.03125 + (g + h + m + l) * 0.03125;
    }

    vec3 g0 = (d + e + i + j) * 0.25;
    vec3 g1 = (a + b + g + f) * 0.25;
    vec3 g2 = (b + c + h + g) * 0.25;
    vec3 g3 = (f + g + l + k) * 0.25;
    vec3 g4 = (g + h + m + l) * 0.25;
    float w0 = karisWeight(g0) * 0.5;
    float w1 = karisWeight(g1) * 0.125;
    float w2 = karisWeight(g2) * 0.125;
    float w3 = karisWeight(g3) * 0.125;
    float w4 = karisWeight(g4) * 0.125;
    return (g0 * w0 + g1 * w1 + g2 * w2 + g3 * w3 + g4 * w4) / max(w0 + w1 + w2 + w3 + w4, 1e-5);
}

vec3 prefilter(vec3 c)
{
    float threshold = uThreshold.x;
    float knee = max(uThreshold.y, 1e-5);
    float brightness = max(c.r, max(c.g, c.b));
    float soft = clamp(brightness - threshold + knee, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee + 1e-5);
    float contribution = max(soft, brightness - threshold) / max(brightness, 1e-5);
    return min(c * contribution, vec3(uThreshold.z));
}

void main()
{
    if (uMode == 0)
    {
        FragColor = vec4(prefilter(downsample13(vUv, true)), 1.0);
    }
    else if (uMode == 1)
    {
        FragColor = vec4(downsample13(vUv, false), 1.0);
    }
    else
    {
        vec2 t = uSourceTexel;
        vec3 s = tap(vUv + t * vec2(-1.0, -1.0)) + 2.0 * tap(vUv + t * vec2(0.0, -1.0)) + tap(vUv + t * vec2(1.0, -1.0))
            + 2.0 * tap(vUv + t * vec2(-1.0, 0.0)) + 4.0 * tap(vUv) + 2.0 * tap(vUv + t * vec2(1.0, 0.0))
            + tap(vUv + t * vec2(-1.0, 1.0)) + 2.0 * tap(vUv + t * vec2(0.0, 1.0)) + tap(vUv + t * vec2(1.0, 1.0));
        FragColor = vec4(s * (uThreshold.w / 16.0), 1.0);
    }
}
