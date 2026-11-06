using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace CablesManagementMod;

 public static class CablesManagementObj
{
    public sealed class Piece
    {
        public readonly string Name;
        public readonly string MaterialName;
        public readonly List<Vector3> Vertices = new List<Vector3>();
        public readonly List<Vector3> Normals = new List<Vector3>();
        public readonly List<int> Triangles = new List<int>();

        internal Piece(string name, string materialName)
        {
            Name = name;
            MaterialName = materialName;
        }
    }

    public static List<Piece> Read(TextReader reader)
    {
        if (reader == null) throw new ArgumentNullException(nameof(reader));
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var pieces = new List<Piece>();
        var byGroup = new Dictionary<string, Piece>(StringComparer.Ordinal);
        int uvCount = 0, lineNumber = 0;
        string group = "CablesManagement", material = "DarkSlate_Plastic", line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            int comment = line.IndexOf('#');
            if (comment >= 0) line = line.Substring(0, comment);
            string[] fields = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) continue;
            try
            {
                switch (fields[0])
                {
                    case "v":
                        if (fields.Length < 4) throw new FormatException("A position needs three coordinates.");
                        positions.Add(Point(Number(fields[1]), Number(fields[2]), Number(fields[3])));
                        break;
                    case "vn":
                        if (fields.Length != 4) throw new FormatException("A normal needs three coordinates.");
                        var normal = Direction(Number(fields[1]), Number(fields[2]), Number(fields[3]));
                        if (normal.sqrMagnitude < 1e-12f) throw new FormatException("Zero normal.");
                        normals.Add(normal.normalized);
                        break;
                    case "vt":
                        if (fields.Length < 2 || fields.Length > 4) throw new FormatException("Invalid texture coordinate.");
                        for (int i = 1; i < fields.Length; i++) Number(fields[i]);
                        uvCount++;
                        break;
                    case "g":
                    case "o":
                        if (fields.Length < 2) throw new FormatException("Empty object/group name.");
                        group = string.Join("_", fields, 1, fields.Length - 1);
                        break;
                    case "usemtl":
                        if (fields.Length != 2) throw new FormatException("Invalid material name.");
                        material = fields[1];
                        break;
                    case "f":
                        if (fields.Length < 4) throw new FormatException("A face needs at least three vertices.");
                        string key = group + "\n" + material;
                        if (!byGroup.TryGetValue(key, out Piece piece))
                        {
                            piece = new Piece(group, material);
                            pieces.Add(piece);
                            byGroup.Add(key, piece);
                        }
                        AddFace(piece, fields, positions, normals, uvCount);
                        break;
                }
            }
            catch (Exception ex) when (ex is FormatException || ex is OverflowException)
            {
                throw new FormatException("CablesManagement OBJ line " + lineNumber + ": " + ex.Message, ex);
            }
        }
        if (pieces.Count == 0) throw new FormatException("CablesManagement OBJ contains no faces.");
        return pieces;
    }

    private static Vector3 Point(float x, float y, float z) => new Vector3(-x * .005f, -z * .005f, -(y + 1.45f) * .005f);
    private static Vector3 Direction(float x, float y, float z) => new Vector3(-x, -z, -y);

    private static float Number(string text)
    {
        float number = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (float.IsNaN(number) || float.IsInfinity(number)) throw new FormatException("Non-finite coordinate.");
        return number;
    }

    private static int Index(string text, int count)
    {
        int value = int.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        int index = value < 0 ? count + value : value - 1;
        if (value == 0 || index < 0 || index >= count) throw new FormatException("OBJ index outside the defined data.");
        return index;
    }

    private static void AddFace(Piece piece, string[] fields, List<Vector3> positions, List<Vector3> normals, int uvCount)
    {
        int count = fields.Length - 1;
        var points = new Vector3[count];
        var supplied = new Vector3[count];
        bool hasNormals = false, lacksNormals = false;
        for (int i = 0; i < count; i++)
        {
            string[] indices = fields[i + 1].Split('/');
            if (indices.Length > 3 || indices[0].Length == 0) throw new FormatException("Invalid face vertex.");
            points[i] = positions[Index(indices[0], positions.Count)];
            if (indices.Length >= 2 && indices[1].Length != 0) Index(indices[1], uvCount);
            if (indices.Length == 3 && indices[2].Length != 0)
            {
                supplied[i] = normals[Index(indices[2], normals.Count)];
                hasNormals = true;
            }
            else lacksNormals = true;
        }
        if (hasNormals && lacksNormals) throw new FormatException("Mixed missing and supplied normals within one face.");

        Vector3 faceNormal = Vector3.zero;
        for (int i = 0; i < count; i++)
        {
            Vector3 p = points[i], q = points[(i + 1) % count];
            faceNormal += new Vector3((p.y - q.y) * (p.z + q.z), (p.z - q.z) * (p.x + q.x), (p.x - q.x) * (p.y + q.y));
        }
        if (faceNormal.sqrMagnitude < 1e-16f) throw new FormatException("Degenerate face.");
        faceNormal = faceNormal.normalized;
        if (!hasNormals)
            for (int i = 0; i < count; i++) supplied[i] = faceNormal;
        for (int i = 1; i < count; i++)
            if (Math.Abs(Vector3.Dot(points[i] - points[0], faceNormal)) > .00001f)
                throw new FormatException("Non-planar face.");

        var remaining = new List<int>();
        for (int i = 0; i < count; i++) remaining.Add(i);
        var triangles = new List<int>();
        while (remaining.Count > 3)
        {
            bool clipped = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                int a = remaining[(i + remaining.Count - 1) % remaining.Count], b = remaining[i], c = remaining[(i + 1) % remaining.Count];
                if (Vector3.Dot(Vector3.Cross(points[b] - points[a], points[c] - points[b]), faceNormal) <= 1e-12f) continue;
                bool contains = false;
                foreach (int index in remaining)
                    if (index != a && index != b && index != c && Inside(points[index], points[a], points[b], points[c], faceNormal))
                    { contains = true; break; }
                if (contains) continue;
                triangles.Add(a); triangles.Add(b); triangles.Add(c);
                remaining.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped) throw new FormatException("Face cannot be triangulated (degenerate or intersecting outline).");
        }
        triangles.AddRange(remaining);
        int offset = piece.Vertices.Count;
        piece.Vertices.AddRange(points);
        piece.Normals.AddRange(supplied);
        for (int i = 0; i < triangles.Count; i += 3)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            Vector3 cross = Vector3.Cross(points[b] - points[a], points[c] - points[a]);
            if (cross.sqrMagnitude < 1e-18f) throw new FormatException("Degenerate triangle.");
            if (Vector3.Dot(cross, supplied[a] + supplied[b] + supplied[c]) < 0f)
            { int swap = b; b = c; c = swap; }
            piece.Triangles.Add(offset + a); piece.Triangles.Add(offset + b); piece.Triangles.Add(offset + c);
        }
    }

    private static bool Inside(Vector3 p, Vector3 a, Vector3 b, Vector3 c, Vector3 normal) =>
        Vector3.Dot(Vector3.Cross(b - a, p - a), normal) >= -1e-12f &&
        Vector3.Dot(Vector3.Cross(c - b, p - b), normal) >= -1e-12f &&
        Vector3.Dot(Vector3.Cross(a - c, p - c), normal) >= -1e-12f;
}
