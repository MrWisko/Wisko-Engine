// Edge-aware 4x4 blur and upsample of the ambient occlusion: removes the kernel rotation noise and writes at full
// resolution, weighting each sample by how close its linear depth is to this pixel's (relative to the distance), so
// occlusion never bleeds from one object onto the one behind it (no dark halos around silhouettes).

in vec2 vUv;
out vec4 FragColor;

uniform sampler2D uSource;     // raw occlusion (half or full resolution)
uniform sampler2D uDepth;      // the scene's full-resolution depth
uniform vec2 uSourceTexel;     // 1 / raw occlusion size
uniform vec4 uDepthRect;
uniform vec2 uNearFar;         // camera near and far planes (linearizes the depth buffer)

float linearDepth(vec2 uv)
{
    float z = texture(uDepth, uDepthRect.xy + uv * uDepthRect.zw).r * 2.0 - 1.0;
    float near = uNearFar.x;
    float far = uNearFar.y;
    return 2.0 * near * far / (far + near - z * (far - near));
}

void main()
{
    float center = linearDepth(vUv);
    // Depth differences beyond a few percent of the distance count as another surface.
    float tolerance = 0.03 * center + 0.05;
    float sum = 0.0;
    float weights = 0.0;
    for (int y = -2; y < 2; y++)
    {
        for (int x = -2; x < 2; x++)
        {
            vec2 offset = (vec2(float(x), float(y)) + 0.5) * uSourceTexel;
            float k = abs(linearDepth(vUv + offset) - center) / tolerance;
            float w = 1.0 / (1.0 + k * k * 16.0);
            sum += texture(uSource, vUv + offset).r * w;
            weights += w;
        }
    }

    FragColor = vec4(vec3(weights > 1e-3 ? sum / weights : texture(uSource, vUv).r), 1.0);
}
