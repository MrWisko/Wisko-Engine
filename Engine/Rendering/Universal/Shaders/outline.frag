in vec3 vWorldPos;
out vec4 FragColor;

uniform vec3 uOutlineColor;   // linear

void main()
{
    vec3 color = atmoDistance(uOutlineColor, vWorldPos);
#ifdef DIRECT_OUTPUT
    FragColor = vec4(finalizeColor(color), 1.0);
#else
    FragColor = vec4(color, 1.0);
#endif
}
