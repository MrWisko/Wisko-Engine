in vec3 vWorldPos;
in vec3 vColor;
out vec4 FragColor;

uniform vec3 uParticleLight;  // linear: how bright the scene's light makes an unlit particle (ambient + sun)

void main()
{
    vec3 color = atmoDistance(srgbToLinear(vColor) * uParticleLight, vWorldPos);
#ifdef DIRECT_OUTPUT
    FragColor = vec4(finalizeColor(color), 1.0);
#else
    FragColor = vec4(color, 1.0);
#endif
}
