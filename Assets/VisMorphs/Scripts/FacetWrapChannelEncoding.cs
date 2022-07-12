using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DxR
{
    public class FacetWrapChannelEncoding : ChannelEncoding
    {
        public List<int> directions;             // Ordered list of directions that this facet wrap goes in (valid values are "x", "y", and "z")
        public List<float> spacing;                // The spacing between small multiples (not padding)
        public int size = 1;                          // The number of small multiples to fit before wrapping
        public List<string> xTranslation;         // List of positional offsets along the three spatial dimension. Each element corresponds with a Mark
        public List<string> yTranslation;         // List of positional offsets along the three spatial dimension. Each element corresponds with a Mark
        public List<string> zTranslation;         // List of positional offsets along the three spatial dimension. Each element corresponds with a Mark
        public int numFacets = 0;                   // The number of small multiples that will be created

        public List<GameObject> axes;

        public FacetWrapChannelEncoding() : base()
        {
            directions = new List<int>();
            spacing = new List<float>();
            xTranslation = new List<string>();
            yTranslation = new List<string>();
            zTranslation = new List<string>();
            axes = new List<GameObject>();
        }
    }
}