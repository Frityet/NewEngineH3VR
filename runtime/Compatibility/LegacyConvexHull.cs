using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace NativeH3VR
{
    internal static class LegacyConvexHull
    {
        private const int VertexLimit = 255;
        private const float NearParallel = 0.9986295f; // cosine of 3 degrees

        // The legacy PhysX cooker expanded the input hull's planes and intersected them
        // with an AABB. The selectively retained box planes bevel protruding hull edges.
        // https://nvidiagameworks.github.io/PhysX/3.4/PhysXGuide/Manual/Geometry.html
        internal static Mesh Create(Mesh source, float skinWidth)
        {
            if (!source.isReadable)
                return null;
            return Create(source.name, source.vertices, skinWidth, out _);
        }

        internal static Mesh Create(string sourceName, Vector3[] points, float skinWidth, out bool usedVertexLimit)
        {
            usedVertexLimit = false;
            if (!float.IsFinite(skinWidth) || skinWidth <= 0f || points.Length == 0 || points.Any(point => !Finite(point)))
                return null;

            Vector3 minimum = points[0], maximum = points[0];
            foreach (var point in points)
            {
                minimum = Vector3.Min(minimum, point);
                maximum = Vector3.Max(maximum, point);
            }
            Vector3 center = minimum + (maximum - minimum) * 0.5f;
            Vector3 size = maximum - minimum;
            if (!Finite(center) || !Finite(size))
                return null;
            for (int index = 0; index < points.Length; index++)
                points[index] -= center;

            // Axis-aligned thin input receives a box before building the initial hull.
            // Use the shortest available extent to give thin dimensions proportional depth.
            if (size.x <= 1e-6f || size.y <= 1e-6f || size.z <= 1e-6f || points.Length < 3)
            {
                float shortest = new[] { size.x, size.y, size.z }.Where(extent => extent > 1e-6f)
                    .DefaultIfEmpty(0.2f).Min();
                for (int axis = 0; axis < 3; axis++)
                    if (size[axis] <= 1e-6f)
                        size[axis] = shortest * 0.1f;
                points = BoxCorners(size * -0.5f, size * 0.5f);
            }

            float diameter = size.magnitude;
            float epsilon = Mathf.Max(diameter * 1e-6f, 1e-8f);
            if (!float.IsFinite(diameter))
                return null;
            var hull = BuildHull(points, epsilon);
            if (hull == null)
                return null;
            var planes = MergePlanes(hull);

            // Axis planes parallel to a hull face must leave room for that face to
            // expand. Other axis planes stay at the source bounds and form bevels.
            var lower = size * -0.5f;
            var upper = size * 0.5f;
            float padding = Mathf.Max(diameter * 0.5f, skinWidth + epsilon);
            if (!float.IsFinite(padding))
                return null;
            for (int axis = 0; axis < 3; axis++)
            {
                int component = axis;
                if (planes.Any(plane => plane.Normal[component] < -NearParallel))
                    lower[axis] -= padding;
                if (planes.Any(plane => plane.Normal[component] > NearParallel))
                    upper[axis] += padding;
            }

            var box = BoxCorners(lower, upper);
            var polygons = new List<List<Vector3>>
            {
                new List<Vector3> { box[0], box[3], box[2], box[1] },
                new List<Vector3> { box[4], box[5], box[6], box[7] },
                new List<Vector3> { box[0], box[1], box[5], box[4] },
                new List<Vector3> { box[3], box[7], box[6], box[2] },
                new List<Vector3> { box[0], box[4], box[7], box[3] },
                new List<Vector3> { box[1], box[2], box[6], box[5] }
            };
            var outerBox = new List<List<Vector3>>(polygons);
            foreach (var plane in planes)
                if (!Clip(polygons, plane.Normal, plane.Distance + skinWidth, epsilon))
                    return null;

            if (UniqueVertices(polygons, epsilon).Count > VertexLimit)
            {
                polygons = ClipToVertexLimit(outerBox, planes, skinWidth, epsilon);
                usedVertexLimit = true;
            }

            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            foreach (var polygon in polygons)
            {
                int first = FindOrAdd(vertices, polygon[0], epsilon);
                int previous = FindOrAdd(vertices, polygon[1], epsilon);
                for (int index = 2; index < polygon.Count; index++)
                {
                    int next = FindOrAdd(vertices, polygon[index], epsilon);
                    if (first != previous && first != next && previous != next)
                    {
                        triangles.Add(first);
                        triangles.Add(previous);
                        triangles.Add(next);
                    }
                    previous = next;
                }
            }
            if (vertices.Count < 4 || vertices.Count > VertexLimit || triangles.Count < 12)
                return null;
            for (int index = 0; index < vertices.Count; index++)
                vertices[index] += center;

            var mesh = new Mesh
            {
                name = sourceName + " (legacy convex inflation)",
                hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild
            };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            return mesh;
        }

        private static bool Finite(Vector3 point) =>
            float.IsFinite(point.x) && float.IsFinite(point.y) && float.IsFinite(point.z);

        private static Vector3[] BoxCorners(Vector3 low, Vector3 high) => new[]
        {
            low, new Vector3(high.x, low.y, low.z),
            new Vector3(high.x, high.y, low.z), new Vector3(low.x, high.y, low.z),
            new Vector3(low.x, low.y, high.z), new Vector3(high.x, low.y, high.z),
            high, new Vector3(low.x, high.y, high.z)
        };

        private struct Face
        {
            internal readonly int A, B, C;
            internal readonly Vector3 Normal;
            internal readonly float Distance, Area;

            internal Face(Vector3[] points, int a, int b, int c, Vector3 interior)
            {
                Vector3 normal = Vector3.Cross(points[b] - points[a], points[c] - points[a]);
                Area = normal.magnitude;
                if (Vector3.Dot(normal, interior - points[a]) > 0f)
                {
                    (b, c) = (c, b);
                    normal = -normal;
                }
                A = a;
                B = b;
                C = c;
                Normal = Area > 0f ? normal / Area : Vector3.zero;
                Distance = Vector3.Dot(Normal, points[a]);
            }
        }

        private static List<Face> BuildHull(Vector3[] points, float epsilon)
        {
            int a = 0;
            for (int i = 1; i < points.Length; i++)
                if (points[i].x < points[a].x)
                    a = i;
            int b = Farthest(points, point => (point - points[a]).sqrMagnitude);
            Vector3 line = points[b] - points[a];
            if (line.sqrMagnitude <= epsilon * epsilon)
                return null;
            int c = Farthest(points, point => Vector3.Cross(line, point - points[a]).sqrMagnitude);
            Vector3 plane = Vector3.Cross(line, points[c] - points[a]);
            if (plane.magnitude <= epsilon * line.magnitude)
                return null;
            int d = Farthest(points, point => Mathf.Abs(Vector3.Dot(plane, point - points[a])));
            if (Mathf.Abs(Vector3.Dot(plane, points[d] - points[a])) <= epsilon * plane.magnitude)
                return null;

            Vector3 interior = (points[a] + points[b] + points[c] + points[d]) * 0.25f;
            var faces = new List<Face>
            {
                new Face(points, a, b, c, interior), new Face(points, a, b, d, interior),
                new Face(points, a, c, d, interior), new Face(points, b, c, d, interior)
            };
            var horizon = new Dictionary<(int, int), (int, int)>();
            for (int index = 0; index < points.Length; index++)
            {
                horizon.Clear();
                for (int faceIndex = faces.Count - 1; faceIndex >= 0; faceIndex--)
                {
                    var face = faces[faceIndex];
                    if (Vector3.Dot(face.Normal, points[index]) - face.Distance <= epsilon)
                        continue;
                    ToggleEdge(horizon, face.A, face.B);
                    ToggleEdge(horizon, face.B, face.C);
                    ToggleEdge(horizon, face.C, face.A);
                    faces.RemoveAt(faceIndex);
                }
                foreach (var edge in horizon.Values)
                {
                    var face = new Face(points, edge.Item1, edge.Item2, index, interior);
                    if (face.Area > epsilon * epsilon)
                        faces.Add(face);
                }
                if (faces.Count < 4 || faces.Count > (VertexLimit - 2) * 2)
                    return null;
            }
            return faces;
        }

        private static int Farthest(Vector3[] points, Func<Vector3, float> distance)
        {
            int index = 0;
            float greatest = distance(points[0]);
            for (int i = 1; i < points.Length; i++)
            {
                float next = distance(points[i]);
                if (next > greatest)
                {
                    index = i;
                    greatest = next;
                }
            }
            return index;
        }

        private static void ToggleEdge(Dictionary<(int, int), (int, int)> edges, int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (!edges.Remove(key))
                edges.Add(key, (a, b));
        }

        private static List<Face> MergePlanes(List<Face> hull)
        {
            var planes = new List<Face>();
            foreach (var face in hull.OrderByDescending(face => face.Area))
                if (!planes.Any(plane => Vector3.Dot(plane.Normal, face.Normal) > NearParallel))
                    planes.Add(face);
            return planes;
        }

        private static List<List<Vector3>> ClipToVertexLimit(List<List<Vector3>> polygons, List<Face> planes,
            float skinWidth, float epsilon)
        {
            // Prioritize the planes that remove the largest fraction of the present hull.
            // Stop at the last valid hull when a further crop would exceed the convex limit.
            var remaining = new List<Face>(planes);
            var vertices = UniqueVertices(polygons, epsilon);
            while (remaining.Count != 0)
            {
                int best = -1;
                float strongest = 0.01f;
                for (int index = 0; index < remaining.Count; index++)
                {
                    var plane = remaining[index];
                    float inside = 0f, outside = 0f;
                    foreach (var vertex in vertices)
                    {
                        float distance = Vector3.Dot(plane.Normal, vertex) - plane.Distance - skinWidth;
                        inside = Mathf.Max(inside, -distance);
                        outside = Mathf.Max(outside, distance);
                    }
                    float depth = inside + outside;
                    float fraction = depth > epsilon ? outside / depth : 0f;
                    if (fraction <= strongest) continue;
                    best = index;
                    strongest = fraction;
                }
                if (best < 0) break;
                var next = remaining[best];
                var clipped = new List<List<Vector3>>(polygons);
                if (!Clip(clipped, next.Normal, next.Distance + skinWidth, epsilon)) break;
                var clippedVertices = UniqueVertices(clipped, epsilon);
                if (clippedVertices.Count > VertexLimit) break;
                polygons = clipped;
                vertices = clippedVertices;
                remaining.RemoveAt(best);
            }
            return polygons;
        }

        private static List<Vector3> UniqueVertices(List<List<Vector3>> polygons, float epsilon)
        {
            var vertices = new List<Vector3>();
            foreach (var polygon in polygons)
                foreach (var vertex in polygon)
                    FindOrAdd(vertices, vertex, epsilon);
            return vertices;
        }

        private static bool Clip(List<List<Vector3>> polygons, Vector3 normal, float distance, float epsilon)
        {
            if (!float.IsFinite(distance))
                return false;
            var cap = new List<Vector3>();
            for (int index = polygons.Count - 1; index >= 0; index--)
            {
                var polygon = polygons[index];
                var clipped = new List<Vector3>();
                Vector3 previous = polygon[polygon.Count - 1];
                float before = Vector3.Dot(normal, previous) - distance;
                foreach (var vertex in polygon)
                {
                    float after = Vector3.Dot(normal, vertex) - distance;
                    if ((before <= epsilon) != (after <= epsilon))
                    {
                        Vector3 crossing = Vector3.Lerp(previous, vertex, before / (before - after));
                        AddNext(clipped, crossing, epsilon);
                        FindOrAdd(cap, crossing, epsilon);
                    }
                    if (after <= epsilon)
                        AddNext(clipped, vertex, epsilon);
                    previous = vertex;
                    before = after;
                }
                if (clipped.Count > 1 && (clipped[0] - clipped[clipped.Count - 1]).sqrMagnitude <= epsilon * epsilon)
                    clipped.RemoveAt(clipped.Count - 1);
                if (clipped.Count < 3)
                    polygons.RemoveAt(index);
                else
                    polygons[index] = clipped;
            }
            if (cap.Count >= 3)
            {
                Vector3 center = cap.Aggregate(Vector3.zero, (sum, point) => sum + point) / cap.Count;
                Vector3 axis = Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right;
                Vector3 right = Vector3.Cross(axis, normal).normalized;
                Vector3 up = Vector3.Cross(normal, right);
                float Angle(Vector3 point) => Mathf.Atan2(Vector3.Dot(point - center, up), Vector3.Dot(point - center, right));
                cap.Sort((a, b) => Angle(a).CompareTo(Angle(b)));
                polygons.Add(cap);
            }
            return polygons.Count >= 4 && polygons.Count <= VertexLimit;
        }

        private static void AddNext(List<Vector3> vertices, Vector3 point, float epsilon)
        {
            if (vertices.Count == 0 || (vertices[vertices.Count - 1] - point).sqrMagnitude > epsilon * epsilon)
                vertices.Add(point);
        }

        private static int FindOrAdd(List<Vector3> vertices, Vector3 point, float epsilon)
        {
            for (int i = 0; i < vertices.Count; i++)
                if ((vertices[i] - point).sqrMagnitude <= epsilon * epsilon)
                    return i;
            vertices.Add(point);
            return vertices.Count - 1;
        }
    }
}
