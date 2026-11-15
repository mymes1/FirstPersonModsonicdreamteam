using System;

namespace FirstPersonCamera.Port.Runtime;

/// <summary>
/// CPU-side copy of the Unity math types. Layout matches <c>UnityEngine.Vector3</c> /
/// <c>Quaternion</c> field-for-field (3 and 4 consecutive floats), which is what lets these
/// structs be handed straight to <c>il2cpp_runtime_invoke</c> for by-value parameters.
/// </summary>
public readonly struct Vec3 : IEquatable<Vec3>
{
    public readonly float X, Y, Z;

    public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

    public static readonly Vec3 Zero = new(0f, 0f, 0f);
    public static readonly Vec3 One = new(1f, 1f, 1f);
    public static readonly Vec3 Up = new(0f, 1f, 0f);
    public static readonly Vec3 Forward = new(0f, 0f, 1f);
    public static readonly Vec3 Right = new(1f, 0f, 0f);

    public float SqrMagnitude => X * X + Y * Y + Z * Z;
    public float Magnitude => MathF.Sqrt(SqrMagnitude);

    public Vec3 Normalized
    {
        get
        {
            var m = Magnitude;
            return m > 1e-6f ? new Vec3(X / m, Y / m, Z / m) : Zero;
        }
    }

    public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vec3 Cross(Vec3 a, Vec3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3 operator *(Vec3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator *(float s, Vec3 a) => a * s;
    public static Vec3 operator /(Vec3 a, float s) => new(a.X / s, a.Y / s, a.Z / s);

    /// <summary>Equivalent of <c>UnityEngine.Vector3.Lerp</c>.</summary>
    public static Vec3 Lerp(Vec3 a, Vec3 b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new Vec3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
    }

    public static float Distance(Vec3 a, Vec3 b) => (a - b).Magnitude;

    public bool Equals(Vec3 other) => X == other.X && Y == other.Y && Z == other.Z;
    public override bool Equals(object obj) => obj is Vec3 v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})";
}

/// <summary>CPU-side copy of <c>UnityEngine.Quaternion</c> (x, y, z, w order, like Unity).</summary>
public readonly struct Quat
{
    public readonly float X, Y, Z, W;

    public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

    public static readonly Quat Identity = new(0f, 0f, 0f, 1f);

    /// <summary>Same convention as <c>UnityEngine.Quaternion.Euler(pitch, yaw, roll)</c>: Z, X, Y order.</summary>
    public static Quat Euler(float pitchDegrees, float yawDegrees, float rollDegrees)
    {
        const float Deg2Rad = MathF.PI / 180f;
        float halfPitch = pitchDegrees * Deg2Rad * 0.5f;
        float halfYaw = yawDegrees * Deg2Rad * 0.5f;
        float halfRoll = rollDegrees * Deg2Rad * 0.5f;

        float sx = MathF.Sin(halfPitch), cx = MathF.Cos(halfPitch);
        float sy = MathF.Sin(halfYaw), cy = MathF.Cos(halfYaw);
        float sz = MathF.Sin(halfRoll), cz = MathF.Cos(halfRoll);

        // Unity builds the rotation as Y * X * Z, i.e. roll first, then pitch, then yaw.
        return new Quat(
            cx * sy * cz + sx * cy * sz,
            cy * sz * cx - sy * sx * cz,
            sy * cx * cz - cy * sx * sz,
            cy * cx * cz + sy * sx * sz);
    }

    public Vec3 Forward
    {
        get
        {
            float x = 2f * (X * Z + W * Y);
            float y = 2f * (Y * Z - W * X);
            float z = 1f - 2f * (X * X + Y * Y);
            return new Vec3(x, y, z);
        }
    }

    public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3}, {W:F3})";
}

/// <summary>CPU-side copy of <c>UnityEngine.Bounds</c> (center + extents, 6 floats).</summary>
public readonly struct Bounds
{
    public readonly Vec3 Center;
    public readonly Vec3 Extents;

    public Bounds(Vec3 center, Vec3 extents) { Center = center; Extents = extents; }

    public Vec3 Min => new(Center.X - Extents.X, Center.Y - Extents.Y, Center.Z - Extents.Z);
    public Vec3 Max => new(Center.X + Extents.X, Center.Y + Extents.Y, Center.Z + Extents.Z);

    /// <summary>Equivalent of <c>UnityEngine.Bounds.Encapsulate(Bounds)</c>.</summary>
    public Bounds Encapsulate(Bounds other)
    {
        var min = Min;
        var max = Max;
        var omin = other.Min;
        var omax = other.Max;

        var nmin = new Vec3(MathF.Min(min.X, omin.X), MathF.Min(min.Y, omin.Y), MathF.Min(min.Z, omin.Z));
        var nmax = new Vec3(MathF.Max(max.X, omax.X), MathF.Max(max.Y, omax.Y), MathF.Max(max.Z, omax.Z));

        return FromMinMax(nmin, nmax);
    }

    public static Bounds FromMinMax(Vec3 min, Vec3 max)
    {
        var center = new Vec3((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f, (min.Z + max.Z) * 0.5f);
        var extents = new Vec3((max.X - min.X) * 0.5f, (max.Y - min.Y) * 0.5f, (max.Z - min.Z) * 0.5f);
        return new Bounds(center, extents);
    }
}
