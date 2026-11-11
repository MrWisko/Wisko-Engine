// Wisko Universal renderer: helpers shared by every Universal shader. GLSL 330 core; the version line
// and any #defines are prepended by ShaderLibrary. All lighting is in linear space; display encoding (sRGB) happens
// exactly once, at the end (finalizeColor), so nothing is gamma-corrected twice.

const float PI = 3.14159265359;

vec3 linearToSrgb(vec3 c)
{
    c = max(c, vec3(0.0));
    vec3 lo = c * 12.92;
    vec3 hi = 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055;
    return mix(lo, hi, step(vec3(0.0031308), c));
}

vec3 srgbToLinear(vec3 c)
{
    c = clamp(c, 0.0, 1.0);
    vec3 lo = c / 12.92;
    vec3 hi = pow((c + 0.055) / 1.055, vec3(2.4));
    return mix(lo, hi, step(vec3(0.04045), c));
}

float luminance(vec3 c)
{
    return dot(c, vec3(0.2126, 0.7152, 0.0722));
}

// Neutral tone mapper: the highlight roll-off of Khronos PBR Neutral. Colours up to 0.8 stay exactly as lit (so a Part's
// colour in sun and in shade looks as authored), brighter ones roll off softly towards white. Khronos' low-end offset
// (meant to cancel a 4 % specular reflection) is left out: it darkened and over-saturated shaded colours.
vec3 tonemapNeutral(vec3 color)
{
    const float startCompression = 0.8;
    const float desaturation = 0.15;
    float peak = max(color.r, max(color.g, color.b));
    if (peak < startCompression)
    {
        return color;
    }

    const float d = 1.0 - startCompression;
    float newPeak = 1.0 - d * d / (peak + d - startCompression);
    color *= newPeak / peak;
    float g = 1.0 - 1.0 / (desaturation * (peak - newPeak) + 1.0);
    return mix(color, vec3(newPeak), g);
}

// ACES filmic (Stephen Hill's fit of the RRT + sRGB ODT). Matrices given as rows, applied as row-vector products.
vec3 tonemapFilmic(vec3 color)
{
    const mat3 inputMat = mat3(
        vec3(0.59719, 0.35458, 0.04823),
        vec3(0.07600, 0.90834, 0.01566),
        vec3(0.02840, 0.13383, 0.83777));
    const mat3 outputMat = mat3(
        vec3(1.60475, -0.53108, -0.07367),
        vec3(-0.10208, 1.10813, -0.00605),
        vec3(-0.00327, -0.07276, 1.07602));
    color = color * inputMat;
    vec3 a = color * (color + 0.0245786) - 0.000090537;
    vec3 b = color * (0.983729 * color + 0.4329510) + 0.238081;
    color = a / b;
    color = color * outputMat;
    return clamp(color, 0.0, 1.0);
}

// Final image controls, in order: exposure and white balance (linear), saturation and contrast (linear, around 18%
// grey), tone mapping (0 none, 1 neutral, 2 filmic), then sRGB encoding for the display.
// uOutput: x exposure multiplier, y saturation, z contrast, w tone mapping mode. uWhiteBalance: per-channel gain.
uniform vec4 uOutput;
uniform vec3 uWhiteBalance;

vec3 gradeLinear(vec3 c)
{
    c = max(c * uOutput.x * uWhiteBalance, vec3(0.0));
    float l = luminance(c);
    c = max(mix(vec3(l), c, uOutput.y), vec3(0.0));
    c = 0.18 * pow(c / 0.18 + 1e-6, vec3(uOutput.z));
    int mode = int(uOutput.w + 0.5);
    if (mode == 1)
    {
        c = tonemapNeutral(c);
    }
    else if (mode == 2)
    {
        c = tonemapFilmic(c);
    }

    return clamp(c, 0.0, 1.0);
}

vec3 finalizeColor(vec3 linearHdr)
{
    return linearToSrgb(gradeLinear(linearHdr));
}
