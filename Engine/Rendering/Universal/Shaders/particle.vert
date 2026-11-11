// Script particles (rain, snow, sparks) in the Universal renderer: the same streamed vertices as the Classic renderer
// (position, display colour), lit like their surroundings and fogged.

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aColor;

uniform mat4 uViewProjection;

out vec3 vWorldPos;
out vec3 vColor;

void main()
{
    vWorldPos = aPosition;
    vColor = aColor;
    gl_Position = uViewProjection * vec4(aPosition, 1.0);
}
