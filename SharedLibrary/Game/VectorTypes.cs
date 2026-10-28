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
    }
}
