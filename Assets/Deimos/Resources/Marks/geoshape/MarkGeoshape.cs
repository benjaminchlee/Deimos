using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GeoJSON.Net.Geometry;
using UniRx;
using UnityEngine;

namespace DxR
{
    public class MarkGeoshape : Mark
    {
        /// <summary>
        /// The mesh for our geographic shape
        /// </summary>
        private Mesh geoshapeMesh;
        /// <summary>
        /// The list of vertices in the mesh itself. Vertices are repeated in order to attain proper lighting via vertex normals. The vertex list is stored as follows:
        /// [ {front vertices}{back vertices}{front vertices repeated}{back vertices repeated} , {f}{fr}{b}{br} ,...]
        /// Each large chunk of geoPositions.Length * 4 corresponds to a single polygon as part of the overall multipolygon (if applicable)
        /// </summary>
        private List<Vector3> vertices;
        /// A list of triangle indices. Used in conjunction with areTrianglesClockwiseWinding to track whether the trianges are ordered clockwise or anticlockwise. This
        /// is important as the faces are reversed when the depth channel is changed from positive to negative.
        /// </summary>
        private List<int> triangles;
        private bool areTrianglesClockwiseWinding = true;
        /// <summary>
        /// A list of lists of 2D coordinates that defines the border of the geographic regions
        /// </summary>
        private List<List<Vector2>> geoPositions;

        private ChannelEncoding longitudeChannelEncoding;
        private ChannelEncoding latitudeChannelEncoding;

        public MarkGeoshape() : base()
        {
        }

        public override void ResetToDefault()
        {
            base.ResetToDefault();

            if (vertices != null)
            {
                vertices = Enumerable.Repeat(Vector3.zero, vertices.Count).ToList();
                geoshapeMesh.SetVertices(vertices);
            }
        }

        #region Morphing functions

        protected override string GetValueForChannelEncoding(ChannelEncoding channelEncoding, int markIndex)
        {
            if (channelEncoding.fieldDataType != null && channelEncoding.fieldDataType == "spatial")
            {
                // If the channel is either x, y, or z, we can just pass it the centroid of this mark's polygons
                if (channelEncoding.channel == "x" || channelEncoding.channel == "y" || channelEncoding.channel == "z")
                {
                    if (channelEncoding.field.ToLower() == "latitude")
                    {
                        return channelEncoding.scale.ApplyScale(centre.Latitude.ToString());
                    }
                    else if (channelEncoding.field.ToLower() == "longitude")
                    {
                        return channelEncoding.scale.ApplyScale(centre.Longitude.ToString());
                    }
                }
                // Otherwise, if it is a size channel, pass in a value of either just "latitude" or "longituide"
                // A custom function will set the size based on the stored GeoShapeValues array
                else if (channelEncoding.channel == "width" || channelEncoding.channel == "height" || channelEncoding.channel == "depth")
                {
                    SetSpatialChannelEncoding(channelEncoding.field.ToLower(), channelEncoding);
                    return channelEncoding.field.ToLower();
                }
            }

            return base.GetValueForChannelEncoding(channelEncoding, markIndex);
        }

        protected override void InitialiseTweeners(ActiveMarkTransition activeMarkTransition, string channel, string initialValue, string finalValue)
        {
            // We initialise the tweener differently if it is affecting a size channel (width, height, depth), as these channels
            // operate by manipulating an array, rather than just a singular value
            if (channel == "width" || channel == "height" || channel == "depth")
            {
                // We need to rescale our tweening value from the observable based on any staging that is defined, if any
                // We access these values now and then use them in the observable later
                bool tweenRescaled = false;
                float minTween = 0;
                float maxTween = 1;

                if (activeMarkTransition.Stages.TryGetValue(channel, out Tuple<float, float> range))
                {
                    tweenRescaled = true;
                    minTween = range.Item1;
                    maxTween = range.Item2;
                }

                // Initialise our arrays that will be used for tweening between
                List<float> initialVertexPositions = null;
                List<float> finalVertexPositions = null;
                List<Vector3> tmpVertices = Enumerable.Repeat(Vector3.zero, vertices.Count).ToList();

                int dim = (channel == "width") ? 0 : (channel == "height") ? 1 : 2;

                CalculateSizeVertices(initialValue, dim, ref tmpVertices);
                initialVertexPositions = tmpVertices.Select(v => v[dim]).ToList();
                CalculateSizeVertices(finalValue, dim, ref tmpVertices);
                finalVertexPositions = tmpVertices.Select(v => v[dim]).ToList();

                activeMarkTransition.TweeningObservable.Subscribe(t =>
                {
                    // Rescale the tween value if necessary
                    if (tweenRescaled)
                        t = Utils.NormaliseValue(t, minTween, maxTween, 0, 1);


                    // We also need to apply an easing function if one has been given
                    if (activeMarkTransition.EasingFunction != null)
                        t = activeMarkTransition.EasingFunction(0, 1, t);

                    for (int i = 0; i < vertices.Count; i++)
                    {
                        Vector3 vertex = vertices[i];
                        vertex[dim] = Mathf.Lerp(initialVertexPositions[i], finalVertexPositions[i], t);
                        vertices[i] = vertex;
                    }

                    geoshapeMesh.SetVertices(vertices);
                    geoshapeMesh.RecalculateNormals();
                    geoshapeMesh.RecalculateBounds();
                }).AddTo(activeMarkTransition.Disposable);
            }
            else
            {
                base.InitialiseTweeners(activeMarkTransition, channel, initialValue, finalValue);
            }
        }

        #endregion Morphing functions

        #region Channel value functions

        public override void SetChannelValue(string channel, string value)
        {
            switch (channel)
            {
                case "length":
                    throw new Exception("Length for GeoShapes is not yet implemented.");
                case "width":
                    SetSize(value, 0);
                    break;
                case "height":
                    SetSize(value, 1);
                    break;
                case "depth":
                    SetSize(value, 2);
                    break;
                default:
                    base.SetChannelValue(channel, value);
                    break;
            }
        }

        public void SetSpatialChannelEncoding(string field, ChannelEncoding channelEncoding)
        {
            if (field == "longitude")
            {
                longitudeChannelEncoding = channelEncoding;
            }
            else if (field == "latitude")
            {
                latitudeChannelEncoding = channelEncoding;
            }
        }

        private void InitialiseGeoshapeMesh()
        {
            geoshapeMesh = GetComponent<MeshFilter>().mesh;

            vertices = new List<Vector3>();
            triangles = new List<int>();
            geoPositions = new List<List<Vector2>>();
            int vertexIdx = 0;
            areTrianglesClockwiseWinding = true;

            foreach (List<IPosition> polygon in polygons)
            {
                // Create our set of positions along a 2D plane
                List<Vector2> polygonGeoPositions = new List<Vector2>();
                foreach (var position in polygon)
                    polygonGeoPositions.Add(new Vector2((float)position.Longitude, (float)position.Latitude));

                // Store this to our total list of lists of vector2s
                geoPositions.Add(polygonGeoPositions);

                // Use these positions to triangulate triangles for our forward and back faces
                Triangulator triangulator = new Triangulator(polygonGeoPositions.ToArray());
                // Triangulate the triangles on this 2D plane
                int[] tris = triangulator.Triangulate();

                // Draw our triangles for a 3D mesh
                int polygonPositionCount = polygonGeoPositions.Count;

                // Front vertices
                for (int i = 0; i < tris.Length; i += 3)
                {
                    triangles.Add(vertexIdx + tris[i]);
                    triangles.Add(vertexIdx + tris[i + 1]);
                    triangles.Add(vertexIdx + tris[i + 2]);
                }

                // Back vertices
                for (int i = 0; i < tris.Length; i += 3)
                {
                    triangles.Add(vertexIdx + polygonPositionCount + tris[i + 2]);
                    triangles.Add(vertexIdx + polygonPositionCount + tris[i + 1]);
                    triangles.Add(vertexIdx + polygonPositionCount + tris[i]);
                }

                // Side vertices
                for (int i = 0; i < polygonPositionCount - 1; i++)
                {
                    int v1 = (polygonPositionCount * 2) + vertexIdx + i;
                    int v2 = v1 + 1;
                    int v3 = (polygonPositionCount * 2) + vertexIdx + polygonPositionCount + i;
                    int v4 = v3 + 1;

                    triangles.Add(v1);
                    triangles.Add(v4);
                    triangles.Add(v3);
                    triangles.Add(v1);
                    triangles.Add(v2);
                    triangles.Add(v4);
                }
                // Complete the side vertices where they loop back with the start
                {
                    int v1 = (polygonPositionCount * 2) + vertexIdx + polygonPositionCount - 1;
                    int v2 = (polygonPositionCount * 2) + vertexIdx;
                    int v3 = (polygonPositionCount * 2) + vertexIdx + polygonPositionCount + polygonPositionCount - 1;
                    int v4 = (polygonPositionCount * 2) + vertexIdx + polygonPositionCount;

                    triangles.Add(v1);
                    triangles.Add(v4);
                    triangles.Add(v3);
                    triangles.Add(v1);
                    triangles.Add(v2);
                    triangles.Add(v4);
                }

                vertexIdx += (polygonPositionCount * 4);
            }

            // Create our dummy list of vertices which will then be populated based on the geoPosition lists
            vertices = Enumerable.Repeat(Vector3.zero, vertexIdx).ToList();
            geoshapeMesh.Clear();
            geoshapeMesh.SetVertices(vertices);
            geoshapeMesh.SetIndices(triangles, MeshTopology.Triangles, 0);
        }

        /// <summary>
        /// Sets the size of this mark using either the geometric values provided as part of the polygon, or a specified value in a rectangular fashion.
        ///
        /// If value is either "longitude" or "latitude", will do the former
        /// </summary>
        private void SetSize(string value, int dim)
        {
            if (geoPositions == null)
                InitialiseGeoshapeMesh();

            CalculateSizeVertices(value, dim, ref vertices);

            geoshapeMesh.SetVertices(vertices);
            geoshapeMesh.RecalculateNormals();
            geoshapeMesh.RecalculateBounds();
        }

        private void CalculateSizeVertices(string value, int dim, ref List<Vector3> newVertices)
        {
            // If the value is either longitude or latitude, calculate the vertices based on the longitude/latitude channels
            if (value == "longitude" || value == "latitude")
            {
                if (value == "longitude")
                {
                    float longitudeOffset = float.Parse(longitudeChannelEncoding.scale.ApplyScale(centre.Longitude.ToString())) * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;

                    int vertexIdx = 0;
                    foreach (List<Vector2> polygonGeoPositions in geoPositions)
                    {
                        int polygonPositionCount = polygonGeoPositions.Count;

                        for (int i = 0; i < polygonPositionCount; i++)
                        {
                            int v1 = vertexIdx + i;
                            int v2 = v1 + polygonPositionCount;
                            int v3 = v2 + polygonPositionCount;
                            int v4 = v3 + polygonPositionCount;

                            Vector3 vertexFront1 = newVertices[v1];
                            Vector3 vertexBack1 = newVertices[v2];
                            Vector3 vertexFront2 = newVertices[v3];
                            Vector3 vertexBack2 = newVertices[v4];

                            float longitudeValue = float.Parse(longitudeChannelEncoding.scale.ApplyScale(polygonGeoPositions[i].x.ToString())) * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;
                            longitudeValue -= longitudeOffset;

                            vertexFront1[dim] = longitudeValue;
                            vertexBack1[dim] = longitudeValue;
                            vertexFront2[dim] = longitudeValue;
                            vertexBack2[dim] = longitudeValue;

                            newVertices[v1] = vertexFront1;
                            newVertices[v2] = vertexBack1;
                            newVertices[v3] = vertexFront2;
                            newVertices[v4] = vertexBack2;
                        }

                        vertexIdx += (polygonPositionCount * 4);
                    }

                    // Vector3 localPos = gameObject.transform.localPosition;
                    // localPos[dim] = longitudeOffset;
                    // gameObject.transform.localPosition = localPos;
                }
                else if (value == "latitude")
                {
                    float latitudeOffset = float.Parse(latitudeChannelEncoding.scale.ApplyScale(centre.Latitude.ToString())) * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;

                    int vertexIdx = 0;
                    foreach (List<Vector2> polygonGeoPositions in geoPositions)
                    {
                        int polygonPositionCount = polygonGeoPositions.Count;

                        for (int i = 0; i < polygonPositionCount; i++)
                        {
                            int v1 = vertexIdx + i;
                            int v2 = v1 + polygonPositionCount;
                            int v3 = v2 + polygonPositionCount;
                            int v4 = v3 + polygonPositionCount;

                            Vector3 vertexFront1 = newVertices[v1];
                            Vector3 vertexBack1 = newVertices[v2];
                            Vector3 vertexFront2 = newVertices[v3];
                            Vector3 vertexBack2 = newVertices[v4];

                            float latitudeValue = float.Parse(latitudeChannelEncoding.scale.ApplyScale(polygonGeoPositions[i].y.ToString())) * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;
                            latitudeValue -= latitudeOffset;

                            vertexFront1[dim] = latitudeValue;
                            vertexBack1[dim] = latitudeValue;
                            vertexFront2[dim] = latitudeValue;
                            vertexBack2[dim] = latitudeValue;

                            newVertices[v1] = vertexFront1;
                            newVertices[v2] = vertexBack1;
                            newVertices[v3] = vertexFront2;
                            newVertices[v4] = vertexBack2;
                        }

                        vertexIdx += (polygonPositionCount * 4);
                    }
                }
            }
            else
            {
                // Otherwise, calculate the size based on the float given in the value parameter
                float size = float.Parse(value) * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;
                float halfSize = size / 2f;

                if (dim == 0)
                {
                    // Split the different polygons into two
                    int vertexIdx = 0;
                    foreach (List<Vector2> polygonGeoPositions in geoPositions)
                    {
                        int polygonPositionCount = polygonGeoPositions.Count;
                        int startRange = 0;
                        int endRange = Mathf.FloorToInt(polygonPositionCount * 0.5f);

                        for (int i = 0; i < polygonPositionCount; i++)
                        {
                            int v1 = vertexIdx + i;
                            int v2 = v1 + polygonPositionCount;
                            int v3 = v2 + polygonPositionCount;
                            int v4 = v3 + polygonPositionCount;

                            Vector3 vertexFront1 = newVertices[v1];
                            Vector3 vertexBack1 = newVertices[v2];
                            Vector3 vertexFront2 = newVertices[v3];
                            Vector3 vertexBack2 = newVertices[v4];

                            if (startRange < i && i < endRange)
                            {
                                vertexFront1[dim] = halfSize;
                                vertexBack1[dim] = halfSize;
                                vertexFront2[dim] = halfSize;
                                vertexBack2[dim] = halfSize;
                            }
                            else
                            {
                                vertexFront1[dim] = -halfSize;
                                vertexBack1[dim] = -halfSize;
                                vertexFront2[dim] = -halfSize;
                                vertexBack2[dim] = -halfSize;
                            }

                            newVertices[v1] = vertexFront1;
                            newVertices[v2] = vertexBack1;
                            newVertices[v3] = vertexFront2;
                            newVertices[v4] = vertexBack2;
                        }

                        vertexIdx += (polygonPositionCount * 4);
                    }
                }
                else if (dim == 1)
                {
                    // Split the different polygons into two
                    int vertexIdx = 0;
                    foreach (List<Vector2> polygonGeoPositions in geoPositions)
                    {
                        int polygonPositionCount = polygonGeoPositions.Count;
                        int startRange = Mathf.FloorToInt(polygonPositionCount * 0.25f);
                        int endRange = Mathf.FloorToInt(polygonPositionCount * 0.75f);

                        for (int i = 0; i < polygonPositionCount; i++)
                        {
                            int v1 = vertexIdx + i;
                            int v2 = v1 + polygonPositionCount;
                            int v3 = v2 + polygonPositionCount;
                            int v4 = v3 + polygonPositionCount;

                            Vector3 vertexFront1 = newVertices[v1];
                            Vector3 vertexBack1 = newVertices[v2];
                            Vector3 vertexFront2 = newVertices[v3];
                            Vector3 vertexBack2 = newVertices[v4];

                            if (startRange < i && i < endRange)
                            {
                                vertexFront1[dim] = -halfSize;
                                vertexBack1[dim] = -halfSize;
                                vertexFront2[dim] = -halfSize;
                                vertexBack2[dim] = -halfSize;
                            }
                            else
                            {
                                vertexFront1[dim] = halfSize;
                                vertexBack1[dim] = halfSize;
                                vertexFront2[dim] = halfSize;
                                vertexBack2[dim] = halfSize;
                            }

                            newVertices[v1] = vertexFront1;
                            newVertices[v2] = vertexBack1;
                            newVertices[v3] = vertexFront2;
                            newVertices[v4] = vertexBack2;
                        }

                        vertexIdx += (polygonPositionCount * 4);
                    }
                }
                else if (dim == 2)
                {
                    int vertexIdx = 0;
                    foreach (List<Vector2> polygonGeoPositions in geoPositions)
                    {
                        int polygonPositionCount = polygonGeoPositions.Count;

                        for (int i = 0; i < polygonPositionCount; i++)
                        {
                            int v1 = vertexIdx + i;
                            int v2 = v1 + polygonPositionCount;
                            int v3 = v2 + polygonPositionCount;
                            int v4 = v3 + polygonPositionCount;

                            Vector3 vertexFront1 = newVertices[v1];
                            Vector3 vertexBack1 = newVertices[v2];
                            Vector3 vertexFront2 = newVertices[v3];
                            Vector3 vertexBack2 = newVertices[v4];

                            vertexFront1[dim] = halfSize;
                            vertexBack1[dim] = -halfSize;
                            vertexFront2[dim] = halfSize;
                            vertexBack2[dim] = -halfSize;

                            newVertices[v1] = vertexFront1;
                            newVertices[v2] = vertexBack1;
                            newVertices[v3] = vertexFront2;
                            newVertices[v4] = vertexBack2;
                        }

                        vertexIdx += (polygonPositionCount * 4);
                    }

                    // We also need to flip the winding order of the triangles if the depth (dim = 2) is negative or positive
                    if (float.TryParse(value, out float result))
                    {
                        if ((result >= 0 && !areTrianglesClockwiseWinding) ||
                            (result < 0 && areTrianglesClockwiseWinding))
                        {
                            for(int i = 0; i < triangles.Count; i = i + 3)
                            {
                                int tmp = triangles[i + 1];
                                triangles[i + 1] = triangles[i + 2];
                                triangles[i + 2] = tmp;
                            }

                            geoshapeMesh.SetIndices(triangles, MeshTopology.Triangles, 0);
                            geoshapeMesh.RecalculateNormals();
                            geoshapeMesh.RecalculateBounds();

                            areTrianglesClockwiseWinding = !areTrianglesClockwiseWinding;
                        }
                    }
                }
            }
        }

        #endregion ChannelValueFunctions
    }
}