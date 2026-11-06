using System;
using System.Collections.Generic;
using UnityEngine;

namespace CablesManagementMod;

public static class CablesManagementPath
{
    public struct Sample
    {
        public Vector3 Position;
        public Vector3 Direction;
        public Sample(Vector3 position, Vector3 direction) { Position = position; Direction = direction; }
    }

    public static List<Sample> Build(Vector3 start, Vector3 end, Vector3 startDirection,
        Vector3 endDirection, bool guideStart, bool guideEnd)
    {
        var result = new List<Sample>(32);
        Vector3 chord = end - start;
        float distance = chord.magnitude;
        if (distance < .00001f) return result;
        Vector3 axis = chord / distance;
        startDirection = startDirection.sqrMagnitude > .000001f ? startDirection.normalized : axis;
        endDirection = endDirection.sqrMagnitude > .000001f ? endDirection.normalized : axis;

        const float straightConeCos = .6427f;
        bool bendStart = guideStart && Math.Abs(Vector3.Dot(startDirection, axis)) < straightConeCos;
        bool bendEnd = guideEnd && Math.Abs(Vector3.Dot(endDirection, axis)) < straightConeCos;

        float transition = (float)Math.Min(.12, distance * .22);
        float collar = transition * .22f;
        float handle = transition * .42f;
        Vector3 straightStart = bendStart ? start + axis * transition : start;
        Vector3 straightEnd = bendEnd ? end - axis * transition : end;
        Add(result, start, bendStart ? startDirection : axis);
        if (bendStart)
        {
            Vector3 mouth = start + startDirection * collar;
            Add(result, mouth, startDirection);
            Curve(result, mouth, mouth + startDirection * handle, straightStart - axis * handle, straightStart);
        }
        Add(result, straightEnd, axis);
        if (bendEnd)
        {
            Vector3 mouth = end - endDirection * collar;
            Curve(result, straightEnd, straightEnd + axis * handle, mouth - endDirection * handle, mouth);
            Add(result, end, endDirection);
        }
        return result;
    }

    public static List<Sample> SubdivideStraight(List<Sample> path, int steps)
    {
        if (steps <= 1 || path.Count < 2) return path;
        Vector3 axis = (path[path.Count - 1].Position - path[0].Position).normalized;
        var result = new List<Sample>(path.Count + steps);
        result.Add(path[0]);
        for (int i = 1; i < path.Count; i++)
        {
            Sample a = path[i - 1], b = path[i];
            Vector3 span = b.Position - a.Position;
            if (Vector3.Dot(a.Direction, axis) > .999f && Vector3.Dot(b.Direction, axis) > .999f
                && Vector3.Cross(span, axis).sqrMagnitude < span.sqrMagnitude * .000001f)
                for (int j = 1; j < steps; j++) result.Add(new Sample(a.Position + span * (j / (float)steps), axis));
            result.Add(b);
        }
        return result;
    }

    private static void Curve(List<Sample> result, Vector3 a, Vector3 c1, Vector3 c2, Vector3 b)
    {
        const int subdivisions = 12;
        for (int i = 1; i <= subdivisions; i++)
        {
            float t = i / (float)subdivisions, s = 1f - t;
            Vector3 position = s * s * s * a + 3f * s * s * t * c1 + 3f * s * t * t * c2 + t * t * t * b;
            Vector3 tangent = 3f * s * s * (c1 - a) + 6f * s * t * (c2 - c1) + 3f * t * t * (b - c2);
            Add(result, position, tangent);
        }
    }

    private static void Add(List<Sample> samples, Vector3 position, Vector3 direction)
    {
        if (samples.Count > 0 && (samples[samples.Count - 1].Position - position).sqrMagnitude < 1e-14f) return;

        float magnitude = direction.magnitude;
        Vector3 unit = magnitude > 1e-12f ? direction / magnitude :
            samples.Count > 0 ? samples[samples.Count - 1].Direction : Vector3.forward;
        samples.Add(new Sample(position, unit));
    }
}

public static class CablesManagementCurve
{
    public static Vector3 StraightHandle(Vector3 chord) => chord * (1f / 3f);

    public static float MouthOverlap(Vector3 chord) => (float)Math.Min(.0015, chord.magnitude * .1);

    public static Vector3 Handle(Vector3 direction, Vector3 chord, bool alignedGuides)
    {
        float distance = chord.magnitude;
        if (distance < .00001f || direction.sqrMagnitude < .00001f) return Vector3.zero;
        if (alignedGuides) return chord * (1f / 3f);
        return direction.normalized * (float)Math.Min(.12, distance / 3.0);
    }

    public static bool Aligned(Vector3 outwardA, Vector3 inwardB, Vector3 chord)
    {
        if (chord.sqrMagnitude < .00000001f) return false;
        Vector3 direction = chord.normalized;
        return Vector3.Dot(outwardA.normalized, direction) > .999f && Vector3.Dot(inwardB.normalized, direction) > .999f;
    }
}

public static class CablesManagementFrames
{
    public static Quaternion[] Build(IReadOnlyList<CablesManagementPath.Sample> path,
        Vector3 startUp, Vector3 endUp, bool guideStart, bool guideEnd)
    {
        var frames = new Quaternion[path.Count];
        if (path.Count == 0) return frames;
        var ups = new Vector3[path.Count];
        var distances = new float[path.Count];
        Vector3 direction = path[0].Direction;
        ups[0] = ProjectUp(startUp, direction, endUp);
        for (int i = 1; i < path.Count; i++)
        {
            Vector3 next = path[i].Direction;
            Vector3 carried = Vector3.Dot(direction, next) < -.9999f
                ? ups[i - 1]
                : Quaternion.FromToRotation(direction, next) * ups[i - 1];
            ups[i] = ProjectUp(carried, next, ups[i - 1]);
            distances[i] = distances[i - 1] + (path[i].Position - path[i - 1].Position).magnitude;
            direction = next;
        }

        int last = path.Count - 1;
        Vector3 target = ProjectUp(endUp, direction, ups[last]);
        float roll = Vector3.SignedAngle(ups[last], target, direction);
        bool flipStart = false;
        if ((guideStart || guideEnd) && Mathf.Abs(roll) > 90f)
        {
            roll += roll > 0f ? -180f : 180f;
            flipStart = guideStart && !guideEnd;
        }

        for (int i = 0; i < path.Count; i++)
        {
            float t = distances[last] > .000001f ? distances[i] / distances[last] : 0f;
            Vector3 up = flipStart ? -ups[i] : ups[i];
            direction = path[i].Direction;
            up = Quaternion.AngleAxis(roll * t, direction) * up;
            frames[i] = Quaternion.LookRotation(direction, up);
        }
        return frames;
    }

    private static Vector3 ProjectUp(Vector3 up, Vector3 direction, Vector3 fallback)
    {
        Vector3 projected = Vector3.ProjectOnPlane(up, direction);
        if (projected.sqrMagnitude < .00000001f)
            projected = Vector3.ProjectOnPlane(fallback, direction);
        if (projected.sqrMagnitude < .00000001f)
            projected = Vector3.ProjectOnPlane(Mathf.Abs(direction.y) < .9f ? Vector3.up : Vector3.right, direction);
        return projected.normalized;
    }
}
