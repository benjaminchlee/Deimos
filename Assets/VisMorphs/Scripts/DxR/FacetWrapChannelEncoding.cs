using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DxR
{
    public class FacetWrapChannelEncoding : ChannelEncoding
    {
        public List<int> directions;              // Ordered list of directions that this facet wrap goes in (valid values are "x", "y", and "z"). Only two elements are allowed
        public List<float> spacing;               // The spacing between small multiples. This is the sum of the specified spacing + padding values. This corresponds to the directions list
        public int size;                          // The number of small multiples to fit before wrapping
        public int numFacets;                     // The number of small multiples that will be created
        public List<float> angles;                 // The angle between small multiples. The layout will be curved when an angle is specified. Use 0 to use a flat layout. This corresponds to the directions list
        public float radius;                      // The radius of the curved layout. This radius is applied to both directions
        public bool faceCentre;                   // Whether the small multiples will face towards the centre. If no curvature is applied, then this will do nothing

        public List<Vector3> translation;         // A list of translation vectors whereby each one corresponds to a mark
        public List<Quaternion> rotate;           // A list of rotate quaternions whereby each one corresponds to a mark
        public Vector3[,] translationMatrix;      // The matrix of translation vectors
        public Quaternion[,] rotateMatrix;        // The matrix of rotate vectors

        public List<GameObject> axes;

        public FacetWrapChannelEncoding() : base()
        {
            directions = new List<int>();
            spacing = new List<float>();
            translation = new List<Vector3>();
            rotate = new List<Quaternion>();
            axes = new List<GameObject>();
            angles = new List<float>();
        }
    }
}