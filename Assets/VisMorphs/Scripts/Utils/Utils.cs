using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using GeoJSON.Net;
using UniRx;
using UnityEngine;
using GeoJSON.Net.Converters;
using GeoJSON.Net.Geometry;
using GeoJSON.Net.Feature;

namespace DxR
{
    public static class Utils
    {
		/// <summary>
		/// Normalises a value from an input range into an output range.
		/// </summary>
		/// <param name="value">The value to normalise.</param>
		/// <param name="i0">The minimum input range.</param>
		/// <param name="i1">The maximum input range.</param>
		/// <param name="j0">The minimum output range.</param>
		/// <param name="j1">The maximum output range.</param>
		/// <returns>The normalised value.</returns>
    	public static float NormaliseValue(float value, float i0, float i1, float j0 = 0, float j1 = 1)
    	{
    		float L = (j0 - j1) / (i0 - i1);
    		return (j0 - (L * i0) + (L * value));
    	}

    	public static double NormaliseValue(double value, double i0, double i1, double j0 = 0, double j1 = 1)
    	{
    		double L = (j0 - j1) / (i0 - i1);
    		return (j0 - (L * i0) + (L * value));
    	}

		public static TValue GetValueOrDefault<TKey,TValue>(this IDictionary<TKey, TValue> dictionary, TKey key)
		{
			TValue ret;
			// Ignore return value
			dictionary.TryGetValue(key, out ret);
			return ret;
		}

        public static System.Object GetPropValue(this System.Object obj, String name) {
            foreach (String part in name.Split('.')) {
                if (obj == null) { return null; }

                Type type = obj.GetType();
                PropertyInfo pInfo = type.GetProperty(part);
                if (pInfo != null) {
                    obj = pInfo.GetValue(obj, null);
                    continue;
                }

                FieldInfo fInfo = type.GetField(part);
                if (fInfo != null) {
                    obj = fInfo.GetValue(obj);
                    continue;
                }

                return null;
            }
            return obj;
        }

        public static T GetPropValue<T>(this System.Object obj, String name) {
            System.Object retval = GetPropValue(obj, name);
            if (retval == null) { return default(T); }

            // throws InvalidCastException if types are incompatible
            return (T) retval;
        }

		/// <summary>
        /// Creates an anonymous observable which only emits the provided value once
        /// </summary>
        /// <param name="value">The item to emit once by the observable</param>
        /// <returns></returns>
		public static IObservable<dynamic> CreateAnonymousObservable(dynamic value)
		{
			var observable = Observable.Create<dynamic>(observer =>
            {
                observer.OnNext(value);
                observer.OnCompleted();
                return Disposable.Empty;
            });

            return observable;
        }

        /// <summary>
        /// From https://answers.unity.com/questions/1134997/string-to-vector3.html
        /// </summary>
        public static Vector3 StringToVector3(string sVector)
        {
            // Remove the parentheses
            if (sVector.StartsWith ("(") && sVector.EndsWith (")")) {
                sVector = sVector.Substring(1, sVector.Length-2);
            }

            // split the items
            string[] sArray = sVector.Split(',');

            // store as a Vector3
            Vector3 result = new Vector3(
                float.Parse(sArray[0]),
                float.Parse(sArray[1]),
                float.Parse(sArray[2]));

            return result;
        }
    }

    public class Triangulator
    {
        private List<Vector2> m_points;

        public Triangulator(Vector2[] points)
        {
            m_points = new List<Vector2>(points);
        }

        public Triangulator(List<Vector2> points)
        {
            m_points = points;
        }

        public Triangulator(Vector3[] points)
        {
            m_points = points.Select(vertex => new Vector2(vertex.x, vertex.y)).ToList();
        }

        public static bool Triangulate(Vector3[] vertices, int[] indices, int indexOffset = 0, int vertexOffset = 0, int numVertices = 0)
        {
            if(numVertices == 0)
                numVertices = vertices.Length;

            if(numVertices < 3)
                return false;

            var workingIndices = new int[numVertices];
            if(Area(vertices, vertexOffset, numVertices) > 0)
            {
                for(int v = 0; v < numVertices; v++)
                    workingIndices[v] = v;
            }
            else
            {
                for(int v = 0; v < numVertices; v++)
                    workingIndices[v] = (numVertices - 1) - v;
            }

            int nv = numVertices;
            int count = 2 * nv;
            int currentIndex = indexOffset;
            for(int m = 0, v = nv - 1; nv > 2;)
            {
                if(count-- <= 0)
                    return false;

                int u = v;
                if(nv <= u)
                    u = 0;

                v = u + 1;
                if(nv <= v)
                    v = 0;

                int w = v + 1;
                if(nv <= w)
                    w = 0;

                if(Snip(vertices, u, v, w, nv, workingIndices))
                {
                    indices[currentIndex++] = workingIndices[u];
                    indices[currentIndex++] = workingIndices[v];
                    indices[currentIndex++] = workingIndices[w];
                    m++;

                    for(int s = v, t = v + 1; t < nv; s++, t++)
                        workingIndices[s] = workingIndices[t];

                    nv--;
                    count = 2 * nv;
                }
            }

            return true;
        }

        public static float Area(Vector3[] vertices, int vertexOffset = 0, int numVertices = 0)
        {
            if(numVertices == 0)
                numVertices = vertices.Length;

            float area = 0.0f;
            for(int p = vertexOffset + numVertices - 1, q = 0; q < numVertices; p = q++)
                area += vertices[p].x * vertices[q].y - vertices[q].x * vertices[p].y;

            return area * 0.5f;
        }

        private static bool Snip(Vector3[] vertices, int u, int v, int w, int n, int[] workingIndices)
        {
            Vector2 A = vertices[workingIndices[u]];
            Vector2 B = vertices[workingIndices[v]];
            Vector2 C = vertices[workingIndices[w]];

            if(Mathf.Epsilon > (((B.x - A.x) * (C.y - A.y)) - ((B.y - A.y) * (C.x - A.x))))
                return false;

            for(int p = 0; p < n; p++)
            {
                if((p == u) || (p == v) || (p == w))
                    continue;

                Vector2 P = vertices[workingIndices[p]];

                if(InsideTriangle(A, B, C, P))
                    return false;
            }

            return true;
        }

        public int[] Triangulate()
        {
            var indices = new List<int>();

            int n = m_points.Count;
            if(n < 3)
                return indices.ToArray();

            var V = new int[n];
            if(Area() > 0)
            {
                for(int v = 0; v < n; v++)
                    V[v] = v;
            }
            else
            {
                for(int v = 0; v < n; v++)
                    V[v] = (n - 1) - v;
            }

            int nv = n;
            int count = 2 * nv;
            for(int m = 0, v = nv - 1; nv > 2;)
            {
                if(count-- <= 0)
                    return indices.ToArray();

                int u = v;
                if(nv <= u)
                    u = 0;

                v = u + 1;
                if(nv <= v)
                    v = 0;

                int w = v + 1;
                if(nv <= w)
                    w = 0;

                if(Snip(u, v, w, nv, V))
                {
                    int a = V[u];
                    int b = V[v];
                    int c = V[w];
                    indices.Add(a);
                    indices.Add(b);
                    indices.Add(c);
                    m++;

                    for(int s = v, t = v + 1; t < nv; s++, t++)
                        V[s] = V[t];

                    nv--;
                    count = 2 * nv;
                }
            }

    //		indices.Reverse();
            return indices.ToArray();
        }

        private float Area()
        {
            int n = m_points.Count;
            float A = 0.0f;
            for(int p = n - 1, q = 0; q < n; p = q++)
            {
                Vector2 pval = m_points [p];
                Vector2 qval = m_points [q];
                A += pval.x * qval.y - qval.x * pval.y;
            }

            return A * 0.5f;
        }

        private bool Snip(int u, int v, int w, int n, int[] V)
        {
            Vector2 A = m_points[V[u]];
            Vector2 B = m_points[V[v]];
            Vector2 C = m_points[V[w]];

            if(Mathf.Epsilon > (((B.x - A.x) * (C.y - A.y)) - ((B.y - A.y) * (C.x - A.x))))
                return false;

            for(int p = 0; p < n; p++)
            {
                if((p == u) || (p == v) || (p == w))
                    continue;

                Vector2 P = m_points[V[p]];

                if(InsideTriangle(A, B, C, P))
                    return false;
            }

            return true;
        }

        private static bool InsideTriangle(Vector2 A, Vector2 B, Vector2 C, Vector2 P)
        {
            float ax = C.x - B.x;
            float ay = C.y - B.y;
            float bx = A.x - C.x;
            float by = A.y - C.y;
            float cx = B.x - A.x;
            float cy = B.y - A.y;
            float apx = P.x - A.x;
            float apy = P.y - A.y;
            float bpx = P.x - B.x;
            float bpy = P.y - B.y;
            float cpx = P.x - C.x;
            float cpy = P.y - C.y;

            float aCROSSbp = ax * bpy - ay * bpx;
            float cCROSSap = cx * apy - cy * apx;
            float bCROSScp = bx * cpy - by * cpx;

            return ((aCROSSbp >= 0.0f) && (bCROSScp >= 0.0f) && (cCROSSap >= 0.0f));
        }
    }
}