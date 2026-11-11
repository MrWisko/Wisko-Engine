// Screen-space ambient occlusion from the depth buffer alone (normals rebuilt from depth). Hemisphere samples within
// uRadius studs, each counted only when the occluder is near (range check), so distant geometry never darkens a
// surface and no dark halos form around objects. Output: 1 unoccluded, lower = occluded (ambient light only).

in vec2 vUv;
out vec4 FragColor;

uniform sampler2D uDepth;
uniform mat4 uProjection;
uniform mat4 uInverseProjection;
uniform vec2 uDepthTexel;      // 1 / depth texture size
uniform vec4 uParams;          // radius (studs), intensity, sample count, power
uniform vec3 uKernel[16];      // hemisphere directions (z up), lengths 0-1, denser near the centre
uniform vec4 uDepthRect;       // where the image lies in uDepth: offset xy, size zw (render scale)

vec3 viewPosition(vec2 uv)
{
    float depth = texture(uDepth, uv).r;
    vec4 clip = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    clip.xy = ((uv - uDepthRect.xy) / uDepthRect.zw) * 2.0 - 1.0;
    vec4 view = uInverseProjection * clip;
    return view.xyz / view.w;
}

void main()
{
    vec2 uv = uDepthRect.xy + vUv * uDepthRect.zw;
    float depth = texture(uDepth, uv).r;
    if (depth >= 1.0)
    {
        FragColor = vec4(1.0);
        return;
    }

    vec3 p = viewPosition(uv);

    // Normal from the smaller depth step in each direction (keeps silhouettes clean).
    vec3 pr = viewPosition(uv + vec2(uDepthTexel.x, 0.0));
    vec3 pl = viewPosition(uv - vec2(uDepthTexel.x, 0.0));
    vec3 pu = viewPosition(uv + vec2(0.0, uDepthTexel.y));
    vec3 pd = viewPosition(uv - vec2(0.0, uDepthTexel.y));
    vec3 dx = abs(pr.z - p.z) < abs(p.z - pl.z) ? pr - p : p - pl;
    vec3 dy = abs(pu.z - p.z) < abs(p.z - pd.z) ? pu - p : p - pd;
    vec3 n = normalize(cross(dx, dy));
    if (dot(n, -p) < 0.0)
    {
        n = -n;
    }

    // Per-pixel rotation of the kernel (interleaved gradient noise), removed by the blur pass.
    float noise = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
    float angle = noise * 6.2831853;
    vec3 random = vec3(cos(angle), sin(angle), 0.0);
    vec3 t = normalize(random - n * dot(random, n));
    vec3 b = cross(n, t);
    mat3 tbn = mat3(t, b, n);

    float radius = uParams.x;
    int samples = int(uParams.z + 0.5);
    float occlusion = 0.0;
    for (int i = 0; i < 16; i++)
    {
        if (i >= samples)
        {
            break;
        }

        vec3 s = p + tbn * uKernel[i] * radius;
        vec4 offset = uProjection * vec4(s, 1.0);
        offset.xy = offset.xy / offset.w * 0.5 + 0.5;
        vec2 sampleUv = uDepthRect.xy + offset.xy * uDepthRect.zw;
        if (offset.x < 0.0 || offset.x > 1.0 || offset.y < 0.0 || offset.y > 1.0)
        {
            continue;
        }

        float sceneZ = viewPosition(sampleUv).z;
        float range = smoothstep(0.0, 1.0, radius / max(abs(p.z - sceneZ), 1e-4));
        occlusion += (sceneZ >= s.z + 0.02 * radius ? 1.0 : 0.0) * range;
    }

    float visibility = 1.0 - occlusion / float(max(samples, 1)) * uParams.y;
    FragColor = vec4(vec3(pow(clamp(visibility, 0.0, 1.0), uParams.w)), 1.0);
}
