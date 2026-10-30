using System;

namespace SharedLibrary.Game
{
    [Serializable]
    public struct Vector3
    {
        public float X;
        public float Y;
        public float Z;

        public Vector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public override string ToString() => $"({X}, {Y}, {Z})";

        public static Vector3 Zero => new Vector3(0f, 0f, 0f);

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3 operator *(float s, Vector3 v) => new Vector3(s * v.X, s * v.Y, s * v.Z);
        public static Vector3 operator *(Vector3 v, float s) => s * v;

        public static Vector3 Cross(Vector3 a, Vector3 b) =>
            new Vector3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }

    [Serializable]
    public struct Quaternion
    {
        public float X;
        public float Y;
        public float Z;
        public float W;

        public Quaternion(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public override string ToString() => $"({X}, {Y}, {Z}, {W})";

        public static Quaternion Identity => new Quaternion(0f, 0f, 0f, 1f);

        /// <summary>The inverse of a unit quaternion (its conjugate). A method, not a property, so it isn't serialized.</summary>
        public static Quaternion Inverse(Quaternion q) => new Quaternion(-q.X, -q.Y, -q.Z, q.W);

        /// <summary>Rotates <paramref name="v"/> by this unit quaternion, like Unity's Quaternion * Vector3.</summary>
        public static Vector3 operator *(Quaternion q, Vector3 v)
        {
            var u = new Vector3(q.X, q.Y, q.Z);
            Vector3 t = 2f * Vector3.Cross(u, v);
            return v + q.W * t + Vector3.Cross(u, t);
        }
    }

    /// <summary>
    /// An RGBA color with components in [0, 1]. Named so it doesn't clash with UnityEngine.Color
    /// in client files that use both namespaces.
    /// </summary>
    [Serializable]
    public struct RGBAColor
    {
        public float R;
        public float G;
        public float B;
        public float A;

        public RGBAColor(float r, float g, float b, float a = 1f)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public override string ToString() => $"({R}, {G}, {B}, {A})";
    }
}
