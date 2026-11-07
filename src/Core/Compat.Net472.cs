#if NETFRAMEWORK
// Bits of newer .NET the core uses (it was written against .NET 6 for the
// IL2CPP mods), for the games' Mono / net472 class libraries. Compiled only
// into net472 mods (NETFRAMEWORK: not netstandard2.1, which has them all);
// internal, so each mod has its own copy and none leak.
//
// MathF, Index, Range and the Split overload sit in namespace System so code
// reads the same on both runtimes. If a game's mscorlib already has one, the
// compiler takes this copy and warns (CS0436, silenced in the props).
// Math.Clamp can't be added to System.Math, so files that use it alias Math to
// Dvr.MathCompat under NETFRAMEWORK.
using System.Runtime.CompilerServices;

namespace System
{
    internal static class MathF
    {
        public const float PI = (float)Math.PI;
        public static float Abs(float x) => Math.Abs(x);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static float Min(float a, float b) => Math.Min(a, b);
        public static float Sqrt(float x) => (float)Math.Sqrt(x);
        public static float Atan(float x) => (float)Math.Atan(x);
        public static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
        public static float Pow(float x, float y) => (float)Math.Pow(x, y);
        public static float Exp(float x) => (float)Math.Exp(x);
        public static float Round(float x) => (float)Math.Round(x);
        public static float Round(float x, int digits) => (float)Math.Round(x, digits);
        public static float Ceiling(float x) => (float)Math.Ceiling(x);
        public static float Floor(float x) => (float)Math.Floor(x);
        public static float Sin(float x) => (float)Math.Sin(x);
        public static float Cos(float x) => (float)Math.Cos(x);
        public static float Tan(float x) => (float)Math.Tan(x);
        public static float Sign(float x) => Math.Sign(x);
    }

    internal readonly struct Index
    {
        private readonly int value;
        public Index(int value, bool fromEnd = false) { this.value = fromEnd ? ~value : value; }
        public static Index Start => new Index(0);
        public static Index End => new Index(0, true);
        public bool IsFromEnd => value < 0;
        public int Value => value < 0 ? ~value : value;
        public int GetOffset(int length) => IsFromEnd ? length - Value : Value;
        public static implicit operator Index(int value) => new Index(value);
    }

    internal readonly struct Range
    {
        public Index Start { get; }
        public Index End { get; }
        public Range(Index start, Index end) { Start = start; End = end; }
        public static Range StartAt(Index start) => new Range(start, Index.End);
        public static Range EndAt(Index end) => new Range(Index.Start, end);
        public static Range All => new Range(Index.Start, Index.End);
        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            int start = Start.GetOffset(length), end = End.GetOffset(length);
            return (start, end - start);
        }
    }

    internal static class StringCompat
    {
        public static string[] Split(this string s, char separator, StringSplitOptions options) => s.Split(new[] { separator }, options);
    }
}

namespace Dvr
{
    /// <summary>System.Math plus Clamp (not in net472). Core files alias Math to this under NETFRAMEWORK.</summary>
    internal static class MathCompat
    {
        public const double PI = System.Math.PI;
        public static int Abs(int x) => System.Math.Abs(x);
        public static float Abs(float x) => System.Math.Abs(x);
        public static double Abs(double x) => System.Math.Abs(x);
        public static int Max(int a, int b) => System.Math.Max(a, b);
        public static float Max(float a, float b) => System.Math.Max(a, b);
        public static double Max(double a, double b) => System.Math.Max(a, b);
        public static int Min(int a, int b) => System.Math.Min(a, b);
        public static float Min(float a, float b) => System.Math.Min(a, b);
        public static double Min(double a, double b) => System.Math.Min(a, b);
        public static int Sign(float x) => System.Math.Sign(x);
        public static double Round(double x) => System.Math.Round(x);
        public static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
        public static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
        public static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;
    }
}
#endif
