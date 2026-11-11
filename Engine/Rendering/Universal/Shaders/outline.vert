// Gameplay outline (material Outline): the mesh pushed out along its normals by uThickness studs and drawn with
// front faces culled, so only a rim shows around the object. Separate from the editor's selection outline.

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
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
uniform float uThickness;

out vec3 vWorldPos;

void main()
{
    vec4 position = vec4(aPosition, 1.0);
    vec3 normal = aNormal;
#ifdef SKINNED
    mat4 skin = aWeights.x * uBones[int(aJoints.x + 0.5)]
              + aWeights.y * uBones[int(aJoints.y + 0.5)]
              + aWeights.z * uBones[int(aJoints.z + 0.5)]
              + aWeights.w * uBones[int(aJoints.w + 0.5)];
    position = skin * position;
    normal = mat3(skin) * normal;
#endif
    vec4 world = uModel * position;
    vec3 n = uNormalMatrix * normal;
    float len = length(n);
    if (len > 1e-6)
    {
        world.xyz += n / len * uThickness;
    }

    vWorldPos = world.xyz;
    gl_Position = uViewProjection * world;
}
