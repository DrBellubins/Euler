using System.Numerics;

namespace Euler.Utils;

// Helper class for math functions
public static class GMath
{
    public const float ColorDivisor = 0.00392156862745f; // 1 / 255, divide byte based colors
    
    // XorWow
    private static uint x, y, z, w, v;
    private static uint d;

    public static void Init()
    {
        var seed = (uint)(new Random().Next(int.MinValue, int.MaxValue));
        
        // Initialize the state with the seed
        x = seed ^ 0x6B5FCA7D; // XOR with a constant to avoid zero state
        y = seed + 0x9E3779B9;
        z = seed ^ 0xDEADBEEF;
        w = seed + 0x12345678;
        v = seed ^ 0xCAFEBABE;
        d = 362437;

        // Run a few iterations to mix the seed
        for (int i = 0; i < 10; i++)
        {
            Next();
        }
    }

    // Returns a float between 0 (inclusive) and 1 (exclusive)
    public static float NextFloat()
    {
        return (float)(Next() / 4294967296.0); // Divide by 2^32
    }

    // Returns an integer in the range [min, max) (max exclusive)
    public static int NextInt(int min, int max)
    {
        if (min >= max)
            throw new ArgumentException("min must be less than max");
        
        uint range = (uint)(max - min);
        return (int)(min + (Next() % range));
    }

    // Returns a float in the range [min, max) (max exclusive)
    public static float NextFloat(float min, float max)
    {
        if (min >= max)
            throw new ArgumentException("min must be less than max");
        
        return min + (max - min) * NextFloat();
    }
    
    private static uint Next()
    {
        uint t = (x ^ (x >> 2));
        x = y; y = z; z = w; w = v;
        v = (v ^ (v << 4)) ^ (t ^ (t << 1));
        d += 362437;
        return v + d;
    }
    
    // Other math functions
    public static float ToRadians(float degrees)
    {
        return degrees * (MathF.PI / 180.0f);
    }
    
    public static double ToRadians(double degrees)
    {
        return degrees * (MathF.PI / 180.0d);
    }

    public static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * t;
    }
    
    public static float Smoothstep(float input)
    {
        // Clamp x to the range [0, 1]
        input = Math.Clamp(input, 0f, 1f);
    
        // Smoothstep formula: 3x² - 2x³
        return input * input * (3f - 2f * input);
    }

    public static float Repeat(float value, float min, float max)
    {
        if (value < min)
            value = max;
        else
        {
            if (value > max)
                value = min;
        }

        return value;
    }

    public static int Repeat(int value, int min, int max)
    {
        if (value < min)
            value = max;
        else
        {
            if (value > max)
                value = min;
        }

        return value;
    }

    
    public static bool InRange(float input, float min, float max)
    {
        return input >= min && input <= max;
    }
    
    public static bool InRangeNotEqual(float input, float min, float max)
    {
        return input > min && input < max;
    }
    
    public static float Clamp(float value, float min, float max)
    {
        if (value < min)
            return min;
        
        if (value > max)
            return max;
        
        return value;
    }
    
    public static double Clamp(double value, double min, double max)
    {
        if (value < min)
            return min;
        
        if (value > max)
            return max;
        
        return value;
    }
    
    public static int Clamp(int value, int min, int max)
    {
        if (value < min)
            return min;
        
        if (value > max)
            return max;
        
        return value;
    }
    
    public static void MatrixToAxisAngle(Matrix4x4 m, out Vector3 axis, out float angleDeg)
    {
        float angleRad = MathF.Acos((m.M11 + m.M22 + m.M33 - 1f) / 2f);
        angleDeg = angleRad * (180f / MathF.PI);

        axis = new Vector3(
            m.M32 - m.M23,
            m.M13 - m.M31,
            m.M21 - m.M12
        );
        if (axis.LengthSquared() > 0.0001f)
            axis = Vector3.Normalize(axis);
        else
            axis = Vector3.UnitY; // fallback axis
    }
    
    public static int Hash2i(int x, int z, int seed)
    {
        unchecked
        {
            uint h = (uint)seed;
            h ^= (uint)x * 0x27D4EB2Du;
            h = (h << 13) | (h >> 19);
            h ^= (uint)z * 0x165667B1u;
            h *= 0x9E3779B9u; // Knuth's golden ratio (uint)
            return (int)h;
        }
    }
    
    public static int Hash3i(int x, int y, int z, int seed)
    {
        unchecked
        {
            uint h = (uint)seed;

            // Mix X
            h ^= (uint)x * 0x9E3779B1u;
            h = (h << 13) | (h >> 19);

            // Mix Y
            h ^= (uint)y * 0x85EBCA77u;
            h = (h << 15) | (h >> 17);

            // Mix Z
            h ^= (uint)z * 0xC2B2AE3Du;
            h = (h << 16) | (h >> 16);

            // Final avalanche
            h ^= h >> 16;
            h *= 0x9E3779B9u;
            h ^= h >> 13;
            h *= 0x85EBCA6Bu;
            h ^= h >> 16;

            return (int)h;
        }
    }
}