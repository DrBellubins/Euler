#!/usr/bin/env bash
# Rebuild the native raylib 6.0 shared library for Linux x64 with
# GRAPHICS_API_OPENGL_43 so raylib-cs gets compute shader support
# (the Raylib-cs 8.1.0 NuGet libraylib.so is an OpenGL 3.3 build).
#
# Usage:  ./Euler/native/build-raylib-linux.sh
# Result: Euler/native/linux-x64/libraylib.so (SONAME libraylib.so.600)
#         The csproj copies it over the NuGet copy on every build.
set -euo pipefail

RAYLIB_TAG=6.0
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC_DIR="$ROOT/lib/raylib"
NATIVE_OUT="$ROOT/Euler/native/linux-x64"

# --- dependencies ---------------------------------------------------------
command -v gcc >/dev/null || { echo "gcc not found (apt install build-essential)"; exit 1; }
command -v make >/dev/null || { echo "make not found (apt install build-essential)"; exit 1; }
pkg-config --exists x11 || { echo "libx11-dev not found (apt install libx11-dev libasound2-dev)"; exit 1; }

# --- clone source (pinned tag, shallow) -----------------------------------
if [ ! -d "$SRC_DIR/.git" ]; then
    echo "Cloning raylib $RAYLIB_TAG ..."
    mkdir -p "$ROOT/lib"
    # NOTE: GIT_SSL_NO_VERIFY fallback for machines with broken CA trust anchors
    git clone --depth 1 --branch "$RAYLIB_TAG" https://github.com/raysan5/raylib.git "$SRC_DIR" \
        || GIT_SSL_NO_VERIFY=true git clone --depth 1 --branch "$RAYLIB_TAG" \
           https://github.com/raysan5/raylib.git "$SRC_DIR"
fi

cd "$SRC_DIR"
make clean >/dev/null 2>&1 || true

# GRAPHICS=GRAPHICS_API_OPENGL_43 -> compute shaders (glDispatchCompute, SSBO,
# texture-image binding). RAYLIB_LIBTYPE=SHARED -> libraylib.so.600.
# LDLIBS override: drop -lGL (GLAD+GLFW resolve GL via dlopen at runtime,
# matching the raylib-cs NuGet binary's dependency profile), keep -lX11
# (raylib's X11 clipboard code references Xlib symbols directly).
make PLATFORM=PLATFORM_DESKTOP \
     RAYLIB_LIBTYPE=SHARED \
     GRAPHICS=GRAPHICS_API_OPENGL_43 \
     LDLIBS="-lc -lm -lpthread -ldl -lrt -lX11" \
     -j"$(nproc)"

# --- sanity checks ---------------------------------------------------------
if strings "src/libraylib.so.6.0.0" | grep -q "Compute shaders not enabled"; then
    echo "ERROR: build still reports the OpenGL 3.3 compute-shader warning." >&2
    exit 1
fi

# --- install into the project ---------------------------------------------
mkdir -p "$NATIVE_OUT"
cp -f "src/libraylib.so.6.0.0" "$NATIVE_OUT/libraylib.so"
echo "Installed $NATIVE_OUT/libraylib.so (SONAME $(readelf -d "$NATIVE_OUT/libraylib.so" | grep -o 'libraylib\.so\.[0-9]*'))"
echo "Run 'dotnet build' (or restart the app) to pick it up."
