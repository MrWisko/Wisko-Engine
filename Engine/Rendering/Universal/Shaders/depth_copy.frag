// Copies the scene's depth into the window's depth buffer after the post-processed image is drawn there, so editor
// overlays (selection outlines, gizmos, labels, debug lines) are hidden behind scene objects exactly as before.

in vec2 vUv;
out vec4 FragColor;

uniform sampler2D uDepth;
uniform vec4 uSourceRect;   // where the image lies in uDepth: offset xy, size zw

void main()
{
    gl_FragDepth = texture(uDepth, uSourceRect.xy + vUv * uSourceRect.zw).r;
    FragColor = vec4(0.0);
}
