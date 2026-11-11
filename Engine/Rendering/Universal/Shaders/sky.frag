// The Atmosphere sky in linear HDR (or graded for DIRECT_OUTPUT): the shared Atmosphere functions fed with linear
// colours. The view ray comes from a basis (camera, or a cube-map face when capturing reflections).

in vec2 vCorner;
out vec4 FragColor;

uniform vec3 uRayForward;
uniform vec3 uRayRight;   // camera right x tan(horizontal half fov)
uniform vec3 uRayUp;      // camera up x tan(vertical half fov)
uniform int uSunDiscOn;   // the reflection capture leaves the sharp disc out (its glow is in the light already)

void main()
{
    vec3 dir = normalize(uRayForward + vCorner.x * uRayRight + vCorner.y * uRayUp);
    vec3 sky = atmoSky(dir);
    if (uSunDiscOn != 0)
    {
        vec4 disc = atmoSunDisc(dir);
        sky = mix(sky, disc.rgb, disc.a);
    }

    if (atmoOn())
    {
        sky = mix(sky, u_atmo[5].rgb, atmoSkyFog(dir));
    }

#ifdef DIRECT_OUTPUT
    FragColor = vec4(finalizeColor(sky), 1.0);
#else
    FragColor = vec4(sky, 1.0);
#endif
}
