// Environment reflections: one face of one mip level of the prefiltered sky cube map. Each texel averages the source
// cube map over a GGX lobe of the level's roughness (importance sampled, sampling lower source mips for wide lobes
// to avoid noise). Rendered only when the sky changes, a face at a time.

in vec2 vCorner;
out vec4 FragColor;

uniform samplerCube uSource;
uniform vec3 uFaceForward;
uniform vec3 uFaceRight;
uniform vec3 uFaceUp;
uniform float uRoughness;
uniform float uSourceSize;     // texels per face of the source's top level

float radicalInverse(uint bits)
{
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
    bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
    bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
    return float(bits) * 2.3283064365386963e-10;
}

vec3 importanceSampleGgx(vec2 xi, vec3 n, float a)
{
    float phi = 2.0 * PI * xi.x;
    float cosTheta = sqrt((1.0 - xi.y) / (1.0 + (a * a - 1.0) * xi.y));
    float sinTheta = sqrt(1.0 - cosTheta * cosTheta);
    vec3 h = vec3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);
    vec3 up = abs(n.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
    vec3 t = normalize(cross(up, n));
    vec3 b = cross(n, t);
    return normalize(t * h.x + b * h.y + n * h.z);
}

void main()
{
    vec3 n = normalize(uFaceForward + vCorner.x * uFaceRight + vCorner.y * uFaceUp);
    if (uRoughness <= 0.001)
    {
        FragColor = vec4(textureLod(uSource, n, 0.0).rgb, 1.0);
        return;
    }

    float a = uRoughness * uRoughness;
    const uint count = 64u;
    vec3 sum = vec3(0.0);
    float weight = 0.0;
    for (uint i = 0u; i < count; i++)
    {
        vec2 xi = vec2(float(i) / float(count), radicalInverse(i));
        vec3 h = importanceSampleGgx(xi, n, a);
        vec3 l = normalize(2.0 * dot(n, h) * h - n);
        float nl = dot(n, l);
        if (nl > 0.0)
        {
            float nh = max(dot(n, h), 0.0);
            float a2 = a * a;
            float d = nh * nh * (a2 - 1.0) + 1.0;
            float pdf = a2 / (PI * d * d) * 0.25 + 1e-4;
            float texelSolid = 4.0 * PI / (6.0 * uSourceSize * uSourceSize);
            float sampleSolid = 1.0 / (float(count) * pdf + 1e-4);
            float lod = max(0.5 * log2(sampleSolid / texelSolid) + 1.0, 0.0);
            sum += textureLod(uSource, l, lod).rgb * nl;
            weight += nl;
        }
    }

    FragColor = vec4(sum / max(weight, 1e-4), 1.0);
}
