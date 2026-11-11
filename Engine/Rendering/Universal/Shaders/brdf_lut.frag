// Split-sum environment BRDF (Karis 2013): for (n.v, roughness) the scale and bias applied to F0. Computed once at
// start-up into a small two-channel texture.

in vec2 vUv;
out vec4 FragColor;

float radicalInverse(uint bits)
{
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
    bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
    bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
    return float(bits) * 2.3283064365386963e-10;
}

void main()
{
    float nv = max(vUv.x, 1e-3);
    float roughness = vUv.y;
    float a = roughness * roughness;
    vec3 v = vec3(sqrt(1.0 - nv * nv), 0.0, nv);
    float scale = 0.0;
    float bias = 0.0;
    const uint count = 256u;
    for (uint i = 0u; i < count; i++)
    {
        vec2 xi = vec2(float(i) / float(count), radicalInverse(i));
        float phi = 2.0 * PI * xi.x;
        float cosTheta = sqrt((1.0 - xi.y) / (1.0 + (a * a - 1.0) * xi.y));
        float sinTheta = sqrt(1.0 - cosTheta * cosTheta);
        vec3 h = vec3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);
        vec3 l = normalize(2.0 * dot(v, h) * h - v);
        float nl = max(l.z, 0.0);
        float nh = max(h.z, 0.0);
        float vh = max(dot(v, h), 0.0);
        if (nl > 0.0)
        {
            float k = a * a;
            float gv = nl * sqrt(nv * nv * (1.0 - k) + k);
            float gl = nv * sqrt(nl * nl * (1.0 - k) + k);
            float vis = 0.5 / max(gv + gl, 1e-6);
            float g = vis * 4.0 * nl * vh / max(nh, 1e-6);
            float fc = pow(1.0 - vh, 5.0);
            scale += (1.0 - fc) * g;
            bias += fc * g;
        }
    }

    FragColor = vec4(scale / float(count), bias / float(count), 0.0, 1.0);
}
