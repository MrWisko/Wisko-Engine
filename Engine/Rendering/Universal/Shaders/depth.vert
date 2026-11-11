// Depth-only pass (depth prepass, shadow maps): position (skinned when SKINNED) and the UV for alpha cut-outs.

layout (location = 0) in vec3 aPosition;
layout (location = 2) in vec2 aUv;
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
uniform mat4 uViewProjection;
uniform vec2 uUvScale;

out vec2 vUv;
out vec3 vWorldPos;
out float vAlpha;

void main()
{
    vec4 position = vec4(aPosition, 1.0);
#ifdef SKINNED
    mat4 skin = aWeights.x * uBones[int(aJoints.x + 0.5)]
              + aWeights.y * uBones[int(aJoints.y + 0.5)]
              + aWeights.z * uBones[int(aJoints.z + 0.5)]
              + aWeights.w * uBones[int(aJoints.w + 0.5)];
    position = skin * position;
#endif
    vec4 world = uModel * position;
    vWorldPos = world.xyz;
    vUv = aUv * uUvScale;
    vAlpha = aColor.a;
    gl_Position = uViewProjection * world;
}
