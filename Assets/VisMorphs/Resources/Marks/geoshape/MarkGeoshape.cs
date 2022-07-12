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
        /// <summary>
        /// A list of lists of 2D coordinates that defines the border of the geographic regions
        /// </summary>
        private List<List<Vector2>> geoPositions;

        private ChannelEncoding longitudeChannelEncoding;
        private ChannelEncoding latitudeChannelEncoding;

        private IObservable<float> tweeningObservable;
        private GeoshapeGeometricValues initialGeoshapeValues;
        private GeoshapeGeometricValues finalGeoshapeValues;
        private CompositeDisposable morphingSubscriptions;


        public MarkGeoshape() : base()
        {
        }

        #region Morphing functions


        private class GeoshapeGeometricValues
        {
            public List<Vector3> vertices;
            public Vector3 localPosition;
            public Vector3 localEulerAngles;
            public Vector3 localScale;
            public Color colour;
        }

        public override void StoreInitialMarkValues()
        {
            initialGeoshapeValues = new GeoshapeGeometricValues
            {
                vertices = this.vertices.ConvertAll(v => new Vector3(v.x, v.y, v.z)),
                localPosition = transform.localPosition,
                localEulerAngles = transform.localEulerAngles,
                localScale = transform.localScale,
                colour = myRenderer.material.color
            };
        }

        public override void StoreFinalMarkValues()
        {
            finalGeoshapeValues = new GeoshapeGeometricValues
            {
                vertices = this.vertices.ConvertAll(v => new Vector3(v.x, v.y, v.z)),
                localPosition = transform.localPosition,
                localEulerAngles = transform.localEulerAngles,
                localScale = transform.localScale,
                colour = myRenderer.material.color
            };
        }

        public override void LoadInitialMarkValues()
        {
            LoadMarkValues(initialGeoshapeValues);
        }

        public override void LoadFinalMarkValues()
        {
            LoadMarkValues(finalGeoshapeValues);
        }

        private void LoadMarkValues(GeoshapeGeometricValues geoshapeValues)
        {
            vertices = geoshapeValues.vertices;
            geoshapeMesh.SetVertices(vertices);
            geoshapeMesh.RecalculateNormals();
            geoshapeMesh.RecalculateBounds();

            transform.localPosition = geoshapeValues.localPosition;
            transform.localEulerAngles = geoshapeValues.localEulerAngles;
            transform.localScale = geoshapeValues.localScale;
            myRenderer.material.color = geoshapeValues.colour;
        }

        public override void InitialiseMorphing(IObservable<float> tweeningObservable)
        {
            morphingSubscriptions = new CompositeDisposable();

            // TODO: For now, we make a subscription for each geometric value that has actually changed
            // There probably is a more elegant way of doing this though
            if (initialGeoshapeValues.vertices[0] != finalGeoshapeValues.vertices[0])
            {
                tweeningObservable.Subscribe(t =>
                {
                    if (isMorphing)
                    {
                        List<Vector3> interpolatedVertices = new List<Vector3>();

                        for (int i = 0; i < initialGeoshapeValues.vertices.Count; i++)
                        {
                            interpolatedVertices.Add(Vector3.Lerp(initialGeoshapeValues.vertices[i], finalGeoshapeValues.vertices[i], t));
                        }

                        geoshapeMesh.SetVertices(interpolatedVertices);
                        geoshapeMesh.RecalculateNormals();
                        geoshapeMesh.RecalculateBounds();
                    }
                }).AddTo(morphingSubscriptions);
            }

            if (initialGeoshapeValues.localPosition != finalGeoshapeValues.localPosition)
            {
                tweeningObservable.Subscribe(t =>
                {
                    if (isMorphing)
                        transform.localPosition = Vector3.Lerp(initialGeoshapeValues.localPosition, finalGeoshapeValues.localPosition, t);
                }).AddTo(morphingSubscriptions);
            }
            if (initialGeoshapeValues.localEulerAngles != finalGeoshapeValues.localEulerAngles)
            {
                tweeningObservable.Subscribe(t =>
                {
                    if (isMorphing)
                        transform.localEulerAngles = Vector3.Lerp(initialGeoshapeValues.localEulerAngles, finalGeoshapeValues.localEulerAngles, t);
                }).AddTo(morphingSubscriptions);
            }
            if (initialGeoshapeValues.localScale != finalGeoshapeValues.localScale)
            {
                tweeningObservable.Subscribe(t =>
                {
                    if (isMorphing)
                        transform.localScale = Vector3.Lerp(initialGeoshapeValues.localScale, finalGeoshapeValues.localScale, t);
                }).AddTo(morphingSubscriptions);
            }
            if (initialGeoshapeValues.colour != finalGeoshapeValues.colour)
            {
                tweeningObservable.Subscribe(t =>
                {
                    if (isMorphing)
                        myRenderer.material.color = Color.Lerp(initialGeoshapeValues.colour, finalGeoshapeValues.colour, t);
                }).AddTo(morphingSubscriptions);
            }

            isMorphing = true;
        }

        public override void DisableMorphing()
        {
            // Cleanup subscriptions
            morphingSubscriptions.Dispose();

            isMorphing = false;
            // The data values on this mark will also need to be reset. We will just have this be set externally
        }

        #endregion // Morphing functions

        public override void ResetToDefault()
        {
            base.ResetToDefault();

            if (vertices != null)
            {
                vertices = Enumerable.Repeat(Vector3.zero, vertices.Count).ToList();
                geoshapeMesh.SetVertices(vertices);
            }
        }

        public override void SetChannelValue(string channel, string value)
        {
            switch (channel)
            {
                case "x":
                    if (value == "longitude" || value == "latitude")
                        SetGeoPosition(value, 0);
                    else
                        base.SetChannelValue(channel, value);
                    break;
                case "y":
                    if (value == "longitude" || value == "latitude")
                        SetGeoPosition(value, 1);
                    else
                        base.SetChannelValue(channel, value);
                    break;
                case "z":
                    if (value == "longitude" || value == "latitude")
                        SetGeoPosition(value, 2);
                    else
                        base.SetChannelValue(channel, value);
                    break;
                case "length":
                    throw new Exception("Length for GeoShapes is not yet implemented.");
                case "width":
                    if (value == "longitude" || value == "latitude")
                        SetGeoSize(value, 0);
                    else
                        SetSize(value, 0);
                    break;
                case "height":
                    if (value == "longitude" || value == "latitude")
                        SetGeoSize(value, 1);
                    else
                        SetSize(value, 1);
                    break;
                case "depth":
                    if (value == "longitude" || value == "latitude")
                        SetGeoSize(value, 2);
                    else
                        SetSize(value, 2);
                    break;
                default:
                    base.SetChannelValue(channel, value);
                    break;
            }
        }

        public void SetChannelEncoding(ChannelEncoding channelEncoding)
        {
            if (channelEncoding.field.ToLower() == "longitude")
                longitudeChannelEncoding = channelEncoding;
            else if (channelEncoding.field.ToLower() == "latitude")
                latitudeChannelEncoding = channelEncoding;
        }

        private void InitialiseGeoshapeMesh()
        {
            geoshapeMesh = GetComponent<MeshFilter>().mesh;

            vertices = new List<Vector3>();
            List<int> triangles = new List<int>();
            geoPositions = new List<List<Vector2>>();
            int vertexIdx = 0;

            foreach (List<IPosition> polygon in polygons)
            {
                // Create our set of positions along a 2D plane
                List<Vector2> polygonGeoPositions = new List<Vector2>();
                foreach (var position in polygon)
                    polygonGeoPositions.Add(new Vector2((float)position.Longitude, (float)position.Latitude));

                // Store this to our total list of lists of vector2s
                geoPositions.Add(polygonGeoPositions);

                // Use these positions to triangulate triangles for our forward and back faces
                Triangulator triangulator = new Triangulator(polygonGeoPositions);
                // Triangulate the triangles on this 2D plane
                int[] tris = triangulator.Triangulate();

                // Draw our triangles for a 3D mesh
                int polygonPositionCount = polygonGeoPositions.Count;

                // Front vertices
                for (int i = 0; i < tris.Length; i += 3)
                {
                    triangles.Add(vertexIdx + tris[i]);
                    triangles.Add(vertexIdx + tris[i + 2]);
                    triangles.Add(vertexIdx + tris[i + 1]);
                }

                // Back vertices
                for (int i = 0; i < tris.Length; i += 3)
                {
                    triangles.Add(vertexIdx + polygonPositionCount + tris[i + 2]);
                    triangles.Add(vertexIdx + polygonPositionCount + tris[i]);
                    triangles.Add(vertexIdx + polygonPositionCount + tris[i + 1]);
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
        /// Sets the position of this mark using the centrepoint of the polygon
        /// </summary>
        private void SetGeoPosition(string value, int dim)
        {
            if (geoPositions == null)
                InitialiseGeoshapeMesh();

            float position = 0;

            if (value == "longitude")
            {
                position = float.Parse(longitudeChannelEncoding.scale.ApplyScale(centre.Longitude.ToString())) * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;
            }
            else if (value == "latitude")
            {
                position = float.Parse(latitudeChannelEncoding.scale.ApplyScale(centre.Latitude.ToString())) * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;
            }

            Vector3 localPos = gameObject.transform.localPosition;
            localPos[dim] = position;
            gameObject.transform.localPosition = localPos;
        }

        /// <summary>
        /// Sets the size of this mark using the geometric values provided as part of the polygon
        /// </summary>
        private void SetGeoSize(string value, int dim)
        {
            if (geoPositions == null)
                InitialiseGeoshapeMesh();

            int geoPositionsCount = geoPositions.Count;

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

                        Vector3 vertexFront1 = vertices[v1];
                        Vector3 vertexBack1 = vertices[v2];
                        Vector3 vertexFront2 = vertices[v3];
                        Vector3 vertexBack2 = vertices[v4];

                        float longitudeValue = float.Parse(longitudeChannelEncoding.scale.ApplyScale(polygonGeoPositions[i].x.ToString())) * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;
                        longitudeValue -= longitudeOffset;

                        vertexFront1[dim] = longitudeValue;
                        vertexBack1[dim] = longitudeValue;
                        vertexFront2[dim] = longitudeValue;
                        vertexBack2[dim] = longitudeValue;

                        vertices[v1] = vertexFront1;
                        vertices[v2] = vertexBack1;
                        vertices[v3] = vertexFront2;
                        vertices[v4] = vertexBack2;
                    }

                    vertexIdx += (polygonPositionCount * 4);
                }

                Vector3 localPos = gameObject.transform.localPosition;
                localPos[dim] = longitudeOffset;
                gameObject.transform.localPosition = localPos;
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

                        Vector3 vertexFront1 = vertices[v1];
                        Vector3 vertexBack1 = vertices[v2];
                        Vector3 vertexFront2 = vertices[v3];
                        Vector3 vertexBack2 = vertices[v4];

                        float latitudeValue = float.Parse(latitudeChannelEncoding.scale.ApplyScale(polygonGeoPositions[i].y.ToString())) * DxR.Vis.SIZE_UNIT_SCALE_FACTOR;
                        latitudeValue -= latitudeOffset;

                        vertexFront1[dim] = latitudeValue;
                        vertexBack1[dim] = latitudeValue;
                        vertexFront2[dim] = latitudeValue;
                        vertexBack2[dim] = latitudeValue;

                        vertices[v1] = vertexFront1;
                        vertices[v2] = vertexBack1;
                        vertices[v3] = vertexFront2;
                        vertices[v4] = vertexBack2;
                    }

                    vertexIdx += (polygonPositionCount * 4);
                }
            }

            geoshapeMesh.SetVertices(vertices);
            geoshapeMesh.RecalculateNormals();
            geoshapeMesh.RecalculateBounds();
        }

        /// <summary>
        /// Sets the size of this mark using a specified value in a rectangular fashion.
        /// </summary>
        private void SetSize(string value, int dim)
        {
            if (geoPositions == null)
                InitialiseGeoshapeMesh();

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

                        Vector3 vertexFront1 = vertices[v1];
                        Vector3 vertexBack1 = vertices[v2];
                        Vector3 vertexFront2 = vertices[v3];
                        Vector3 vertexBack2 = vertices[v4];

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

                        vertices[v1] = vertexFront1;
                        vertices[v2] = vertexBack1;
                        vertices[v3] = vertexFront2;
                        vertices[v4] = vertexBack2;
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

                        Vector3 vertexFront1 = vertices[v1];
                        Vector3 vertexBack1 = vertices[v2];
                        Vector3 vertexFront2 = vertices[v3];
                        Vector3 vertexBack2 = vertices[v4];

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

                        vertices[v1] = vertexFront1;
                        vertices[v2] = vertexBack1;
                        vertices[v3] = vertexFront2;
                        vertices[v4] = vertexBack2;
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

                        Vector3 vertexFront1 = vertices[v1];
                        Vector3 vertexBack1 = vertices[v2];
                        Vector3 vertexFront2 = vertices[v3];
                        Vector3 vertexBack2 = vertices[v4];

                        vertexFront1[dim] = halfSize;
                        vertexBack1[dim] = -halfSize;
                        vertexFront2[dim] = halfSize;
                        vertexBack2[dim] = -halfSize;

                        vertices[v1] = vertexFront1;
                        vertices[v2] = vertexBack1;
                        vertices[v3] = vertexFront2;
                        vertices[v4] = vertexBack2;
                    }

                    vertexIdx += (polygonPositionCount * 4);
                }
            }

            geoshapeMesh.SetVertices(vertices);
            geoshapeMesh.RecalculateNormals();
            geoshapeMesh.RecalculateBounds();
        }
    }
}