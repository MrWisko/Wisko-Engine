// One triangle covering the viewport (corners -1,-1 / 3,-1 / -1,3); vUv runs 0-1 across the visible part.

layout (location = 0) in vec2 aCorner;

out vec2 vCorner;
out vec2 vUv;

uniform float uQuadDepth;  // clip-space depth to draw at (the sky uses the far plane)

void main()
{
    vCorner = aCorner;
    vUv = aCorner * 0.5 + 0.5;
    gl_Position = vec4(aCorner, uQuadDepth, 1.0);
}
