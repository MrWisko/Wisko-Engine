// Universal surfaces: material, lights, shadows, ambient light and sky reflections, then the Atmosphere's distance
// haze and fog. HDR output (linear, before exposure) unless DIRECT_OUTPUT, which grades, tone-maps and encodes here.
//
// Lighting convention (docs/graphics.md): a white Lambertian surface facing a light of radiance L reflects L (no
// 1/pi), so Atmosphere and light intensities read like brightness; the specular BRDF is scaled by pi to match.

in vec3 vWorldPos;
in vec3 vNormal;
in vec4 vTangent;
in vec2 vUv;
in vec4 vColor;
out vec4 FragColor;

// ---- material ----
uniform vec4 uBaseColor;        // linear rgb, a = opacity
uniform vec3 uEmissive;         // linear rgb x intensity
uniform vec4 uMaterialParams;   // metallic, roughness, normal strength, occlusion strength
uniform vec4 uMaterialParams2;  // alpha cutoff, toon bands, shading mode (0 lit, 1 simple, 2 unlit, 3 toon), alpha mode (0 opaque, 1 mask, 2 blend)
uniform int uMaterialFlags;     // bits: 1 base tex, 2 metal-rough tex, 4 normal tex, 8 occlusion tex, 16 emissive tex,
                                // 32 flat shading, 64 receive shadows, 128 double sided, 256 vertex colours are sRGB
uniform sampler2D uBaseColorTex;
uniform sampler2D uMetalRoughTex;
uniform sampler2D uNormalTex;
uniform sampler2D uOcclusionTex;
uniform sampler2D uEmissiveTex;

// ---- lights (std140: 4 vec4 per light) ----
//   [0] position, range   [1] colour x intensity (linear), type (0 point, 1 spot, 2 directional)
//   [2] direction the light travels, cos(outer half angle)   [3] cos(inner half angle), shadow slot (-1 none), 0, 0
layout (std140) uniform LightBlock
{
    vec4 uLightData[256];
};
uniform int uLightCount;
uniform int uLightIndex[16];

// ---- sun (the Atmosphere's) ----
uniform vec3 uSunDirection;     // toward the sun
uniform vec3 uSunRadiance;      // linear colour x intensity
uniform int uSunShadowOn;
uniform sampler2DArrayShadow uSunShadowMap;
uniform mat4 uSunShadowMatrix[4];   // world -> [0,1] shadow-map space per cascade
uniform vec4 uCascadeFar;           // view distance where each cascade ends
uniform vec4 uCascadeTexel;         // world size of one shadow-map texel per cascade
uniform int uCascadeCount;
uniform vec4 uShadowParams;         // depth bias (texels), normal bias (texels), filter taps, shadow fade start (view distance)

// ---- spot and point shadows ----
uniform sampler2DShadow uSpotShadowAtlas;
uniform mat4 uSpotShadowMatrix[8];  // world -> clip of the spot light
uniform vec4 uSpotShadowRect[8];    // atlas offset (xy) and scale (zw)
uniform vec4 uSpotShadowTexel[8];   // x: texel size in atlas uv
uniform samplerCubeShadow uPointShadow0;
uniform samplerCubeShadow uPointShadow1;

// ---- ambient, reflections, ambient occlusion ----
uniform vec3 uAmbientUp;            // linear
uniform vec3 uAmbientDown;          // linear (equals up in Flat mode)
uniform samplerCube uEnvironment;   // prefiltered sky, roughness in the mip levels
uniform float uEnvironmentMaxLod;
uniform float uReflectionIntensity;
uniform float uDielectricReflection; // rough non-metals: the sky reflection in proportion to the ambient light (0.2-1)
uniform sampler2D uBrdfLut;
uniform sampler2D uAmbientOcclusion;
uniform int uAmbientOcclusionOn;
uniform vec2 uViewportOrigin;
uniform vec2 uViewportSize;

uniform vec3 uCameraPosition;
uniform vec3 uCameraForward;

bool hasFlag(int bit)
{
    return (uMaterialFlags & bit) != 0;
}

// Poisson-like offsets for shadow filtering (unit disc).
const vec2 kTaps[16] = vec2[16](
    vec2(-0.94201624, -0.39906216), vec2(0.94558609, -0.76890725), vec2(-0.09418410, -0.92938870), vec2(0.34495938, 0.29387760),
    vec2(-0.91588581, 0.45771432), vec2(-0.81544232, -0.87912464), vec2(-0.38277543, 0.27676845), vec2(0.97484398, 0.75648379),
    vec2(0.44323325, -0.97511554), vec2(0.53742981, -0.47373420), vec2(-0.26496911, -0.41893023), vec2(0.79197514, 0.19090188),
    vec2(-0.24188840, 0.99706507), vec2(-0.81409955, 0.91437590), vec2(0.19984126, 0.78641367), vec2(0.14383161, -0.14100790));

int shadowTaps()
{
    return int(uShadowParams.z + 0.5);
}

float filteredShadow2DArray(vec3 uv, float layer, float texel)
{
    int taps = shadowTaps();
    if (taps <= 1)
    {
        return texture(uSunShadowMap, vec4(uv.xy, layer, uv.z));
    }

    float sum = 0.0;
    float radius = taps <= 4 ? 0.9 : (taps <= 9 ? 1.4 : 1.8);
    for (int i = 0; i < 16; i++)
    {
        if (i >= taps)
        {
            break;
        }

        sum += texture(uSunShadowMap, vec4(uv.xy + kTaps[i] * radius * texel, layer, uv.z));
    }

    return sum / float(taps);
}

float sunShadow(vec3 worldPos, vec3 normal)
{
    if (uSunShadowOn == 0 || !hasFlag(64))
    {
        return 1.0;
    }

    float depth = dot(worldPos - uCameraPosition, uCameraForward);
    int cascade = -1;
    for (int i = 0; i < 4; i++)
    {
        if (i < uCascadeCount && depth <= uCascadeFar[i])
        {
            cascade = i;
            break;
        }
    }

    if (cascade < 0)
    {
        return 1.0;
    }

    float texelWorld = uCascadeTexel[cascade];
    float nl = clamp(dot(normal, uSunDirection), 0.0, 1.0);
    vec3 offset = normal * uShadowParams.y * texelWorld * (1.0 - 0.5 * nl);
    vec4 coord = uSunShadowMatrix[cascade] * vec4(worldPos + offset, 1.0);
    if (coord.x < 0.0 || coord.x > 1.0 || coord.y < 0.0 || coord.y > 1.0 || coord.z > 1.0)
    {
        return 1.0;
    }

    float mapSize = float(textureSize(uSunShadowMap, 0).x);
    float bias = uShadowParams.x / mapSize;
    float lit = filteredShadow2DArray(vec3(coord.xy, coord.z - bias), float(cascade), 1.0 / mapSize);

    // Fade out over the last stretch of the shadow distance instead of a hard edge.
    float far = uCascadeFar[uCascadeCount - 1];
    float fade = clamp((depth - uShadowParams.w) / max(far - uShadowParams.w, 0.001), 0.0, 1.0);
    return mix(lit, 1.0, fade);
}

float spotShadow(int slot, vec3 worldPos, vec3 normal)
{
    vec4 clip = uSpotShadowMatrix[slot] * vec4(worldPos + normal * 0.03, 1.0);
    if (clip.w <= 0.0)
    {
        return 1.0;
    }

    vec3 ndc = clip.xyz / clip.w * 0.5 + 0.5;
    if (ndc.x < 0.0 || ndc.x > 1.0 || ndc.y < 0.0 || ndc.y > 1.0 || ndc.z > 1.0)
    {
        return 1.0;
    }

    vec4 rect = uSpotShadowRect[slot];
    float texel = uSpotShadowTexel[slot].x;
    vec2 uv = rect.xy + ndc.xy * rect.zw;
    float reference = ndc.z - uShadowParams.x * 0.00015;
    int taps = min(shadowTaps(), 9);
    if (taps <= 1)
    {
        return texture(uSpotShadowAtlas, vec3(uv, reference));
    }

    float sum = 0.0;
    vec2 lo = rect.xy + texel;
    vec2 hi = rect.xy + rect.zw - texel;
    for (int i = 0; i < 9; i++)
    {
        if (i >= taps)
        {
            break;
        }

        sum += texture(uSpotShadowAtlas, vec3(clamp(uv + kTaps[i] * 1.2 * texel, lo, hi), reference));
    }

    return sum / float(taps);
}

float pointShadow(int slot, vec3 worldPos, vec3 normal, vec3 lightPos, float range)
{
    vec3 fromLight = worldPos + normal * 0.04 - lightPos;
    float reference = length(fromLight) / range - 0.004;
    return slot == 0 ? texture(uPointShadow0, vec4(fromLight, reference)) : texture(uPointShadow1, vec4(fromLight, reference));
}

// GGX / Smith height-correlated / Schlick (Filament's formulation).
float distributionGgx(float nh, float a)
{
    float a2 = a * a;
    float d = nh * nh * (a2 - 1.0) + 1.0;
    return a2 / max(PI * d * d, 1e-7);
}

float visibilitySmith(float nv, float nl, float a)
{
    float a2 = a * a;
    float gv = nl * sqrt(nv * nv * (1.0 - a2) + a2);
    float gl = nv * sqrt(nl * nl * (1.0 - a2) + a2);
    return 0.5 / max(gv + gl, 1e-6);
}

vec3 fresnelSchlick(vec3 f0, float vh)
{
    return f0 + (1.0 - f0) * pow(1.0 - vh, 5.0);
}

struct Surface
{
    vec3 albedo;
    float metallic;
    float roughness;
    vec3 n;
    vec3 v;
    vec3 f0;
    int mode;
    float bands;
};

// Light arriving from direction l with radiance (already attenuated and shadowed) -> light leaving towards the eye.
vec3 shade(Surface s, vec3 l, vec3 radiance)
{
    float nl = dot(s.n, l);
    if (s.mode == 3)
    {
        // Toon: hard bands of the diffuse term, plus a crisp highlight for smooth materials.
        // Band edges are anti-aliased over about one pixel.
        float lit = clamp(nl, 0.0, 1.0);
        float bands = max(s.bands, 2.0);
        float x = lit * bands;
        float whole = floor(x);
        float w = max(fwidth(x), 1e-4);
        float stepped = (whole + smoothstep(0.5 - w, 0.5 + w, x - whole)) / bands;
        vec3 h = normalize(l + s.v);
        float highlight = step(0.92 + 0.07 * s.roughness, dot(s.n, h)) * (1.0 - s.roughness) * step(0.0, nl);
        return (s.albedo * stepped + vec3(highlight) * 0.6) * radiance;
    }

    nl = clamp(nl, 0.0, 1.0);
    if (nl <= 0.0)
    {
        return vec3(0.0);
    }

    vec3 h = normalize(l + s.v);
    float nh = clamp(dot(s.n, h), 0.0, 1.0);
    float nv = clamp(dot(s.n, s.v), 1e-4, 1.0);
    if (s.mode == 1)
    {
        // Simple lit: Lambert + normalized Blinn-Phong (shininess from roughness).
        float shininess = 2.0 / max(s.roughness * s.roughness * s.roughness * s.roughness, 1e-4) - 2.0;
        float specular = (shininess + 8.0) / 8.0 * pow(nh, shininess) * 0.04;
        return (s.albedo + vec3(specular)) * radiance * nl;
    }

    float a = max(s.roughness * s.roughness, 0.002);
    vec3 f = fresnelSchlick(s.f0, clamp(dot(l, h), 0.0, 1.0));
    vec3 specular = distributionGgx(nh, a) * visibilitySmith(nv, nl, a) * f * PI;
    vec3 diffuse = (1.0 - f) * (1.0 - s.metallic) * s.albedo;
    return (diffuse + specular) * radiance * nl;
}

vec3 perturbNormal(vec3 n, vec3 worldPos)
{
    if (!hasFlag(4))
    {
        return n;
    }

    vec3 t = vTangent.xyz - n * dot(n, vTangent.xyz);
    float tl = length(t);
    if (tl < 1e-5)
    {
        // No usable tangent: build one from screen-space derivatives of position and UV.
        vec3 dpdx = dFdx(worldPos);
        vec3 dpdy = dFdy(worldPos);
        vec2 dudx = dFdx(vUv);
        vec2 dudy = dFdy(vUv);
        float det = dudx.x * dudy.y - dudy.x * dudx.y;
        t = (dpdx * dudy.y - dpdy * dudx.y) * (det < 0.0 ? -1.0 : 1.0);
        t = t - n * dot(n, t);
        tl = length(t);
        if (tl < 1e-6)
        {
            return n;
        }
    }

    t /= tl;
    vec3 b = cross(n, t) * (vTangent.w < 0.0 ? -1.0 : 1.0);
    vec3 m = texture(uNormalTex, vUv).xyz * 2.0 - 1.0;
    m.xy *= uMaterialParams.z;
    return normalize(t * m.x + b * m.y + n * max(m.z, 1e-3));
}

void main()
{
    // ---- material inputs ----
    vec4 base = uBaseColor;
    vec3 vertexColor = hasFlag(256) ? srgbToLinear(vColor.rgb) : vColor.rgb;
    base.rgb *= vertexColor;
    base.a *= vColor.a;
    if (hasFlag(1))
    {
        base *= texture(uBaseColorTex, vUv);
    }

    int alphaMode = int(uMaterialParams2.w + 0.5);
    if (alphaMode == 1 && base.a < uMaterialParams2.x)
    {
        discard;
    }

    float metallic = uMaterialParams.x;
    float roughness = uMaterialParams.y;
    if (hasFlag(2))
    {
        vec4 mr = texture(uMetalRoughTex, vUv);
        roughness *= mr.g;
        metallic *= mr.b;
    }

    roughness = clamp(roughness, 0.03, 1.0);
    metallic = clamp(metallic, 0.0, 1.0);

    float occlusion = 1.0;
    if (hasFlag(8))
    {
        occlusion = mix(1.0, texture(uOcclusionTex, vUv).r, uMaterialParams.w);
    }

    vec3 emissive = uEmissive;
    if (hasFlag(16))
    {
        emissive *= texture(uEmissiveTex, vUv).rgb;
    }

    int mode = int(uMaterialParams2.z + 0.5);

    // ---- normal ----
    vec3 n = normalize(vNormal);
    vec3 faceNormal = normalize(cross(dFdx(vWorldPos), dFdy(vWorldPos)));
    if (hasFlag(32) && !any(isnan(faceNormal)))
    {
        n = dot(faceNormal, n) < 0.0 ? -faceNormal : faceNormal;
    }

    if (!gl_FrontFacing)
    {
        n = -n;
    }

    n = perturbNormal(n, vWorldPos);
    vec3 v = normalize(uCameraPosition - vWorldPos);

    vec3 color;
    if (mode == 2)
    {
        color = base.rgb + emissive;
    }
    else
    {
        Surface s;
        s.albedo = base.rgb;
        s.metallic = mode == 0 ? metallic : 0.0;
        s.roughness = roughness;
        s.n = n;
        s.v = v;
        s.f0 = mix(vec3(0.04), base.rgb, s.metallic);
        s.mode = mode;
        s.bands = uMaterialParams2.y;

        // Sun.
        vec3 direct = vec3(0.0);
        if (dot(uSunRadiance, uSunRadiance) > 0.0)
        {
            direct += shade(s, uSunDirection, uSunRadiance * sunShadow(vWorldPos, n));
        }

        // Lights chosen for this object (strongest first, at most 16).
        for (int i = 0; i < 16; i++)
        {
            if (i >= uLightCount)
            {
                break;
            }

            int index = uLightIndex[i] * 4;
            vec4 p0 = uLightData[index];
            vec4 p1 = uLightData[index + 1];
            vec4 p2 = uLightData[index + 2];
            vec4 p3 = uLightData[index + 3];
            int type = int(p1.w + 0.5);
            vec3 l;
            float attenuation = 1.0;
            if (type == 2)
            {
                l = -p2.xyz;
            }
            else
            {
                vec3 toLight = p0.xyz - vWorldPos;
                float d2 = max(dot(toLight, toLight), 1e-4);
                float d = sqrt(d2);
                l = toLight / d;
                float ratio = d / p0.w;
                float window = clamp(1.0 - ratio * ratio * ratio * ratio, 0.0, 1.0);
                attenuation = window * window / d2;
                if (type == 1)
                {
                    float cd = dot(-l, p2.xyz);
                    attenuation *= smoothstep(p2.w, p3.x, cd);
                }
            }

            if (attenuation <= 0.0)
            {
                continue;
            }

            int slot = int(floor(p3.y + 0.5));
            if (slot >= 0 && hasFlag(64))
            {
                attenuation *= slot >= 100 ? pointShadow(slot - 100, vWorldPos, n, p0.xyz, p0.w) : spotShadow(slot, vWorldPos, n);
            }

            direct += shade(s, l, p1.rgb * attenuation);
        }

        // Ambient: sky above, ground below (equal in Flat mode); screen-space ambient occlusion applies here only.
        float ao = occlusion;
        if (uAmbientOcclusionOn != 0)
        {
            ao *= texture(uAmbientOcclusion, (gl_FragCoord.xy - uViewportOrigin) / uViewportSize).r;
        }

        vec3 irradiance = mix(uAmbientDown, uAmbientUp, clamp(n.y * 0.5 + 0.5, 0.0, 1.0));
        vec3 ambient;
        if (mode == 0)
        {
            float nv = clamp(dot(n, v), 1e-4, 1.0);
            vec2 brdf = texture(uBrdfLut, vec2(nv, roughness)).rg;
            vec3 r = reflect(-v, n);
            vec3 prefiltered = textureLod(uEnvironment, r, roughness * uEnvironmentMaxLod).rgb;
            vec3 fr = s.f0 + (max(vec3(1.0 - roughness), s.f0) - s.f0) * pow(1.0 - nv, 5.0);
            vec3 kd = (1.0 - fr) * (1.0 - s.metallic);
            float specularOcclusion = clamp(pow(nv + ao, exp2(-16.0 * roughness - 1.0)) - 1.0 + ao, 0.0, 1.0);
            // Non-metals: rough reflections (which overlap the ambient light) in proportion to it, glossy ones the full sky.
            float smoothness = 1.0 - roughness;
            float environmentScale = mix(mix(uDielectricReflection, 1.0, smoothness * smoothness * smoothness * smoothness), 1.0, s.metallic);
            ambient = kd * s.albedo * irradiance * ao
                + prefiltered * (s.f0 * brdf.x + brdf.y) * uReflectionIntensity * environmentScale * specularOcclusion;
        }
        else
        {
            ambient = s.albedo * irradiance * ao;
        }

        color = direct + ambient + emissive;
    }

    color = atmoDistance(color, vWorldPos);
    float alpha = alphaMode == 2 ? clamp(base.a, 0.0, 1.0) : 1.0;
#ifdef DIRECT_OUTPUT
    FragColor = vec4(finalizeColor(color), alpha);
#else
    FragColor = vec4(color, alpha);
#endif
}
