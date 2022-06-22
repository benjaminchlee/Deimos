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

        // public static void LoadGeoJSON(string json)
        // {
        //     var featureCollection = JsonConvert.DeserializeObject<FeatureCollection>(json);

        //     foreach (var feature in featureCollection.Features)
        //     {
        //         switch (feature.Geometry.Type)
        //         {
        //             case GeoJSONObjectType.Polygon:
        //                 {
        //                     GameObject go = new GameObject();
        //                     LineRenderer lr = go.AddComponent<LineRenderer>();
        //                     List<Vector3> positions = new List<Vector3>();

        //                     var polygon = feature.Geometry as Polygon;
        //                     foreach (LineString lineString in polygon.Coordinates)
        //                     {
        //                         foreach (IPosition position in lineString.Coordinates)
        //                         {
        //                             positions.Add(new Vector3(((float)position.Longitude), ((float)position.Latitude), 0));
        //                         }
        //                     }

        //                     lr.material = new Material(Shader.Find("Standard"));
        //                     lr.startWidth = 0.2f;
        //                     lr.endWidth = 0.2f;
        //                     lr.positionCount = positions.Count;
        //                     lr.SetPositions(positions.ToArray());
        //                     break;
        //                 }

        //             case GeoJSONObjectType.MultiPolygon:
        //                 {
        //                     MultiPolygon multiPolygon = feature.Geometry as MultiPolygon;
        //                     foreach (Polygon polygon in multiPolygon.Coordinates)
        //                     {
        //                         GameObject go = new GameObject();
        //                         LineRenderer lr = go.AddComponent<LineRenderer>();
        //                         List<Vector3> positions = new List<Vector3>();

        //                         foreach (LineString lineString in polygon.Coordinates)
        //                         {
        //                             foreach (IPosition position in lineString.Coordinates)
        //                             {
        //                                 positions.Add(new Vector3(((float)position.Longitude), ((float)position.Latitude), 0));
        //                             }
        //                         }

        //                         lr.material = new Material(Shader.Find("Standard"));
        //                         lr.material.color = Color.red;
        //                         lr.startColor = Color.red;
        //                         lr.endColor = Color.red;
        //                         lr.startWidth = 0.2f;
        //                         lr.endWidth = 0.2f;
        //                         lr.positionCount = positions.Count;
        //                         lr.SetPositions(positions.ToArray());
        //                     }
        //                     break;
        //                 }
        //         }

        //         foreach (var kvp in feature.Properties)
        //         {
        //             Debug.Log(kvp.Key + " " + kvp.Value);
        //         }
        //     }
        // }
    }

    public class Triangulator
    {
        private List<Vector2> m_points = new List<Vector2>();

        public Triangulator (Vector2[] points) {
            m_points = new List<Vector2>(points);
        }

        public Triangulator (List<Vector2> points) {
            m_points = points;
        }

        public int[] Triangulate() {
            List<int> indices = new List<int>();

            int n = m_points.Count;
            if (n < 3)
                return indices.ToArray();

            int[] V = new int[n];
            if (Area() > 0) {
                for (int v = 0; v < n; v++)
                    V[v] = v;
            }
            else {
                for (int v = 0; v < n; v++)
                    V[v] = (n - 1) - v;
            }

            int nv = n;
            int count = 2 * nv;
            for (int v = nv - 1; nv > 2; ) {
                if ((count--) <= 0)
                    return indices.ToArray();

                int u = v;
                if (nv <= u)
                    u = 0;
                v = u + 1;
                if (nv <= v)
                    v = 0;
                int w = v + 1;
                if (nv <= w)
                    w = 0;

                if (Snip(u, v, w, nv, V)) {
                    int a, b, c, s, t;
                    a = V[u];
                    b = V[v];
                    c = V[w];
                    indices.Add(a);
                    indices.Add(b);
                    indices.Add(c);
                    for (s = v, t = v + 1; t < nv; s++, t++)
                        V[s] = V[t];
                    nv--;
                    count = 2 * nv;
                }
            }

            indices.Reverse();
            return indices.ToArray();
        }

        private float Area () {
            int n = m_points.Count;
            float A = 0.0f;
            for (int p = n - 1, q = 0; q < n; p = q++) {
                Vector2 pval = m_points[p];
                Vector2 qval = m_points[q];
                A += pval.x * qval.y - qval.x * pval.y;
            }
            return (A * 0.5f);
        }

        private bool Snip (int u, int v, int w, int n, int[] V) {
            int p;
            Vector2 A = m_points[V[u]];
            Vector2 B = m_points[V[v]];
            Vector2 C = m_points[V[w]];
            if (Mathf.Epsilon > (((B.x - A.x) * (C.y - A.y)) - ((B.y - A.y) * (C.x - A.x))))
                return false;
            for (p = 0; p < n; p++) {
                if ((p == u) || (p == v) || (p == w))
                    continue;
                Vector2 P = m_points[V[p]];
                if (InsideTriangle(A, B, C, P))
                    return false;
            }
            return true;
        }

        private bool InsideTriangle (Vector2 A, Vector2 B, Vector2 C, Vector2 P) {
            float ax, ay, bx, by, cx, cy, apx, apy, bpx, bpy, cpx, cpy;
            float cCROSSap, bCROSScp, aCROSSbp;

            ax = C.x - B.x; ay = C.y - B.y;
            bx = A.x - C.x; by = A.y - C.y;
            cx = B.x - A.x; cy = B.y - A.y;
            apx = P.x - A.x; apy = P.y - A.y;
            bpx = P.x - B.x; bpy = P.y - B.y;
            cpx = P.x - C.x; cpy = P.y - C.y;

            aCROSSbp = ax * bpy - ay * bpx;
            cCROSSap = cx * apy - cy * apx;
            bCROSScp = bx * cpy - by * cpx;

            return ((aCROSSbp >= 0.0f) && (bCROSScp >= 0.0f) && (cCROSSap >= 0.0f));
        }
    }
}