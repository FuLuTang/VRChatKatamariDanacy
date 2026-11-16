// A minimal stand-in for the parts of UnityEngine that MotionMath.cs and KatamariMath.cs
// use, so those two files can be compiled unmodified on Linux (Tools/motion_sim).
//
// This file is NOT a Unity replacement and must never be shipped into the project: it
// declares exactly the members the motion math touches (Vector3, Quaternion, and six
// Mathf functions), with Unity's own semantics.  The point of the stub is that there is
// one implementation of the 13 motion programs - VRC_Katamari/Runtime/Pickup/MotionMath.cs
// - and this tool compiles that file as source instead of re-deriving it in C# or Python.
//
// Semantics deliberately copied from Unity (2022.3):
//   * Vector3 is a struct of three floats, and Vector3.zero is a static readonly field
//     (motion math only reads it).
//   * Mathf.PI is the float nearest pi; Mathf.Sin/Cos/Sqrt/Atan2 return
//     (float)System.Math.<same>(double argument), which is exactly what Unity does.
//   * Mathf.Max(a, b) returns a when a > b, else b (Unity's own comparison, not Math.Max,
//     so NaN propagates the same way).
//   * Quaternion is a struct of four floats in x, y, z, w order and stores no derived
//     state; KatamariMath.IsFiniteQuaternion only reads the components.
using System;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x;
        public float y;
        public float z;

        public static readonly Vector3 zero = new Vector3(0f, 0f, 0f);
        public static readonly Vector3 one = new Vector3(1f, 1f, 1f);
        public static readonly Vector3 up = new Vector3(0f, 1f, 0f);

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public override string ToString()
        {
            return "(" + x + ", " + y + ", " + z + ")";
        }
    }

    public struct Quaternion
    {
        public float x;
        public float y;
        public float z;
        public float w;

        public static readonly Quaternion identity = new Quaternion(0f, 0f, 0f, 1f);

        public Quaternion(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }
    }

    public static class Mathf
    {
        public const float PI = (float)Math.PI;

        public const float Infinity = float.PositiveInfinity;

        public static float Abs(float value)
        {
            return Math.Abs(value);
        }

        public static float Max(float a, float b)
        {
            return a > b ? a : b;
        }

        public static float Min(float a, float b)
        {
            return a < b ? a : b;
        }

        public static float Sin(float value)
        {
            return (float)Math.Sin(value);
        }

        public static float Cos(float value)
        {
            return (float)Math.Cos(value);
        }

        public static float Sqrt(float value)
        {
            return (float)Math.Sqrt(value);
        }

        public static float Atan2(float y, float x)
        {
            return (float)Math.Atan2(y, x);
        }

        public static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
