// Universal surfaces: primitives, imported models (SKINNED: deformed by the bone block), the environment.
// Outputs world-space position, normal and tangent (w = handedness: bitangent = w * cross(normal, tangent) points
// "up" in the image), the UV and the linear vertex colour.

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec2 aUv;
layout (location = 3) in vec4 aTangent;
layout (location = 4) in vec4 aColor;
#ifdef SKINNED
layout (location = 5) in vec4 aJoints;
layout (location = 6) in vec4 aWeights;

layout (std140) uniform BoneBlock
{
    mat4 uBones[256];
};
#endif

uniform mat4 uModel;
uniform mat3 uNormalMatrix;
uniform mat4 uViewProjection;

// UV source: 0 = the mesh's UVs x uUvScale (repeats); 1 = projected from the object's own axes in studs: at tiling 1 one
// repeat every 4 studs, tiling 2 every 2 studs (uObjectSize: the unit mesh's scale, i.e. the Part's size).
uniform int uUvMode;
uniform vec2 uUvScale;
uniform vec3 uObjectSize;

out vec3 vWorldPos;
out vec3 vNormal;
out vec4 vTangent;
out vec2 vUv;
out vec4 vColor;

// Box projection on the dominant axis of the object-space normal: u, v in studs, tangent along +u. Images have their
// origin at the top-left, so v grows downwards (-Y on the sides).
void studsMapping(vec3 p, vec3 n, out vec2 uv, out vec4 tangent)
{
    vec3 a = abs(n);
    if (a.y >= a.x && a.y >= a.z)
    {
        float s = n.y >= 0.0 ? 1.0 : -1.0;
        uv = vec2(p.x, s * p.z);
        tangent = vec4(1.0, 0.0, 0.0, 1.0);
    }
    else if (a.x >= a.z)
    {
        float s = n.x >= 0.0 ? 1.0 : -1.0;
        uv = vec2(-s * p.z, -p.y);
        tangent = vec4(0.0, 0.0, -s, 1.0);
    }
    else
    {
        float s = n.z >= 0.0 ? 1.0 : -1.0;
        uv = vec2(s * p.x, -p.y);
        tangent = vec4(s, 0.0, 0.0, 1.0);
    }
}

void main()
{
    vec4 position = vec4(aPosition, 1.0);
    vec3 normal = aNormal;
    vec4 tangent = aTangent;
#ifdef SKINNED
    mat4 skin = aWeights.x * uBones[int(aJoints.x + 0.5)]
              + aWeights.y * uBones[int(aJoints.y + 0.5)]
              + aWeights.z * uBones[int(aJoints.z + 0.5)]
              + aWeights.w * uBones[int(aJoints.w + 0.5)];
    position = skin * position;
    mat3 skin3 = mat3(skin);
    normal = skin3 * normal;
    tangent.xyz = skin3 * tangent.xyz;
#endif

    vec2 uv = aUv * uUvScale;
    if (uUvMode == 1)
    {
        studsMapping(aPosition * uObjectSize, aNormal, uv, tangent);
        uv *= uUvScale * 0.25;
    }

    vec4 world = uModel * position;
    vWorldPos = world.xyz;
    vNormal = uNormalMatrix * normal;
    vTangent = vec4(mat3(uModel) * tangent.xyz, tangent.w);
    vUv = uv;
    vColor = aColor;
    gl_Position = uViewProjection * world;
}
