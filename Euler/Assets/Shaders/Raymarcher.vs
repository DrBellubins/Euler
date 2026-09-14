// Vertex stage: maps the full-screen quad through raylib's 2D mvp.
// Nothing else - all work happens in the fragment stage.
//
// NOTE: raylib 6.0 compiles custom shader code verbatim - it does NOT
// inject attribute/uniform declarations or define VERTEX/FRAGMENT macros
// (the old 5.x "#if defined(VERTEX)" single-file convention no longer
// works). Shaders must be complete, versioned sources, per the
// examples/shaders/resources/shaders/glsl100/*.vs|*.fs convention.
#version 330

in vec3 vertexPosition;
uniform mat4 mvp;

void main()
{
    gl_Position = mvp * vec4(vertexPosition, 1.0);
}
