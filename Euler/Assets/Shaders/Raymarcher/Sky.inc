// Sky / horizon atmosphere for the raymarcher.
//
// Included from Raymarcher.comp. Owns every unlit background color: the sky
// gradient used for background rays, and the color distance fog fades toward
// (the fog itself is applied by the driver, which keeps the fog parameters).
// A different atmosphere (e.g. a space sky for 4D scenes) gets its own
// function here.
//
// NOTE: no #version directive here - included files must never carry one;
// #version must remain the first line of the main (expanding) shader.

/// Sky gradient driven by the ray's true-up component: bright horizon
/// (rd.y = 0) to deep zenith (rd.y = +1). Directions below the horizon
/// clamp to the horizon color.
vec3 SkyColor(vec3 rd)
{
    float h = clamp(rd.y * 0.5 + 0.5, 0.0, 1.0);
    return mix(vec3(0.78, 0.87, 1.0), vec3(0.28, 0.47, 0.78), h);
}

// The wormhole's two universes are tinted apart (the lower one warm, the
// upper one as-is) so the player can tell which side of the throat they are
// on - the sky and the distance fog it fades toward both pick up the tint
// (see WhTubeMarch's universe latch in Raymarcher.comp).
vec3 UniverseTint(int universe)
{
    return (universe < 0) ? vec3(1.0, 0.5, 0.5) : vec3(1.0);
}
