// Display stage: blits the pixel buffer the compute stage
// (Raymarcher.comp) just wrote to the screen. The compute stage produces
// exactly one packed RGBA8 uint per pixel, so this is a 1:1 pixel read -
// no filtering or mipmaps involved.
//
// The vertex stage is raylib's built-in default full-screen pass-through
// (gl_Position = mvp * vertexPosition), so this file is loaded as
// ShaderType.Pixel with no explicit .vs.
//
// #version 430 is needed for the SSBO declaration; mixing it with the
// default GLSL 330 vertex stage is legal (each stage may use its own
// #version).
#version 430

uniform vec2 Resolution;            // framebuffer size in pixels

// The same pixel buffer Raymarcher.comp writes. Binding point 0 is
// context-global GL state, so one Rlgl.BindShaderBuffer(...) call makes it
// visible to BOTH the compute and this display program.
layout(std430, binding = 0) buffer Pixels
{
    uint data[];
} pixelBuf;

out vec4 fragColor;

void main()
{
    // gl_FragCoord is (pixelX + 0.5, pixelY + 0.5) with (0,0) at bottom-left.
    int x = int(gl_FragCoord.x) - 1;
    int y = int(gl_FragCoord.y) - 1;
    if (x < 0 || y < 0 || x >= int(Resolution.x) || y >= int(Resolution.y))
    {
        fragColor = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    uint px = pixelBuf.data[x + y * int(Resolution.x)];
    vec3 color = vec3(
        float(px & 0xFFu) / 255.0,
        float((px >> 8) & 0xFFu) / 255.0,
        float((px >> 16) & 0xFFu) / 255.0);

    fragColor = vec4(color, 1.0);
}
