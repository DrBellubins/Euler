// Shared raymarcher helpers, included from Raymarcher.comp via
// #include "RaymarcherHelpers.inc" (expanded in place by Resource.LoadShader).
//
// NOTE: no #version directive here - included files must never carry one;
// #version must remain the first line of the main (expanding) shader.

vec3 SkyColor(vec3 rd)
{
    float h = clamp(rd.y * 0.5 + 0.5, 0.0, 1.0);
    return mix(vec3(0.78, 0.87, 1.0), vec3(0.28, 0.47, 0.78), h);
}
