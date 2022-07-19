using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DxR
{
    public class ChannelEncoding
    {
        public string channel;          // Name of channel.

        public string value;            // If not "undefined", use this value as constant value for channel.
        public string valueDataType;    // Type of value can be "float", "string", or "int".

        public string field;            // Name of field in data mapped to this channel.
        public string fieldDataType;    // Type of data field can be "quantitative", "temporal", "oridinal", or "nominal".

        public Scale scale;
        public GameObject axis;
        public GameObject legend;

        public ChannelEncoding()
        {
            value = DxR.Vis.UNDEFINED;
            scale = null;
            axis = null;
            legend = null;
        }

        public bool IsOffset()
        {
            return channel.EndsWith("offset");
        }

        public bool IsFacetWrap()
        {
            return channel == "facetwrap";
        }

        public static void FilterUnchangedChannelEncodings(ref List<ChannelEncoding> channelEncodings1, ref List<ChannelEncoding> channelEncodings2, bool keepSpatialChanges)
        {
            var _channelEncodings1 = channelEncodings1;
            var _channelEncodings2 = channelEncodings2;

            // Remove channel encodings from the first list that have duplicates in the second list
            var filteredChannelEncodings1 = _channelEncodings1.Where(ce1 => !_channelEncodings2.Any(ce2 => ce1.channel == ce2.channel && AreChannelEncodingsEqual(ce1, ce2))).ToList();

            // Now do the same thing but in reverse
            var filteredChannelEncodings2 = _channelEncodings2.Where(ce2 => !_channelEncodings1.Any(ce1 => ce1.channel == ce2.channel && AreChannelEncodingsEqual(ce1, ce2))).ToList();

            if (keepSpatialChanges)
            {
                // If one of the changed encodings is either its spatial position, offset, or offsetpct, we keep ALL of them even they didn't change
                // This is so that marks later on can calculate a single spatial value by merging these values together
                string[] dimensions = new string[] { "x", "y", "z" };
                var filteredChannelNames1 = filteredChannelEncodings1.Select(ce => ce.channel);
                var filteredChannelNames2 = filteredChannelEncodings2.Select(ce => ce.channel);
                var allChangedChannelNames = filteredChannelNames1.Union(filteredChannelNames2);
                bool hasFacetWrap = allChangedChannelNames.Contains("facetwrap");

                foreach (string dim in dimensions)
                {
                    string offset = dim + "offset";
                    string offsetpct = offset + "pct";

                    // If the filtered list contains any of the dim's channels, then add all of the dim's associated channels
                    // Also add everything if there's a facetwrap channel, as facetwrap can affect all spatial dimensions
                    if ((allChangedChannelNames.Contains(dim) || allChangedChannelNames.Contains(offset) || allChangedChannelNames.Contains(offsetpct)) || hasFacetWrap)
                    {
                        // Find all of the channels in both original lists and add them back in
                        ChannelEncoding dimCE = channelEncodings1.SingleOrDefault(ce => ce.channel == dim);
                        if (dimCE != null && !filteredChannelNames1.Contains(dim))
                        {
                            filteredChannelEncodings1.Add(dimCE);
                        }

                        ChannelEncoding offsetCE = channelEncodings1.SingleOrDefault(ce => ce.channel == offset);
                        if (offsetCE != null && !filteredChannelNames1.Contains(offset))
                        {
                            filteredChannelEncodings1.Add(offsetCE);
                        }

                        ChannelEncoding offsetpctCE = channelEncodings1.SingleOrDefault(ce => ce.channel == offsetpct);
                        if (offsetpctCE != null && !filteredChannelNames1.Contains(offsetpct))
                        {
                            filteredChannelEncodings1.Add(offsetpctCE);
                        }

                        dimCE = channelEncodings2.SingleOrDefault(ce => ce.channel == dim);
                        if (dimCE != null && !filteredChannelNames2.Contains(dim))
                        {
                            filteredChannelEncodings2.Add(dimCE);
                        }

                        offsetCE = channelEncodings2.SingleOrDefault(ce => ce.channel == offset);
                        if (offsetCE != null && !filteredChannelNames2.Contains(offset))
                        {
                            filteredChannelEncodings2.Add(offsetCE);
                        }

                        offsetpctCE = channelEncodings2.SingleOrDefault(ce => ce.channel == offsetpct);
                        if (offsetpctCE != null && !filteredChannelNames2.Contains(offsetpct))
                        {
                            filteredChannelEncodings2.Add(offsetpctCE);
                        }
                    }
                }
            }

            channelEncodings1 = filteredChannelEncodings1;
            channelEncodings2 = filteredChannelEncodings2;
        }

        public static bool AreChannelEncodingsEqual(ChannelEncoding ce1, ChannelEncoding ce2)
        {
            // Check base attributes of the channel encodings
            if (!(ce1.channel == ce2.channel &&
                  ce1.value == ce2.value &&
                  ce1.valueDataType == ce2.valueDataType &&
                  ce1.field == ce2.field &&
                  ce1.fieldDataType == ce2.fieldDataType &&
                  ce1.channel == ce2.channel))
            {
                return false;
            }

            // Check scale object
            if (ce1.scale == null && ce2.scale != null ||
                ce1.scale != null && ce2.scale == null)
            {
                return false;
            }
            if (!(ce1.scale == null && ce2.scale == null) &&
                !(ce1.scale != null && ce2.scale != null &&
                  ce1.scale.GetType() == ce2.scale.GetType() &&
                  ce1.scale.domain != null && ce2.scale.domain != null &&
                  ce1.scale.domain.SequenceEqual(ce2.scale.domain) &&
                  ce1.scale.range != null && ce2.scale.range != null &&
                  ce1.scale.range.SequenceEqual(ce2.scale.range)))
            {
                return false;
            }

            // Check special channel encoding types
            if (ce1.IsOffset() && ce2.IsOffset())
            {
                var oce1 = (OffsetChannelEncoding)ce1;
                var oce2 = (OffsetChannelEncoding)ce2;

                if (!(oce1.linkedChannel == oce2.linkedChannel &&
                      oce1.values[0] == oce2.values[0]))    // TODO: By right we should check all values, but this is faster and should be fine for now
                    return false;
            }
            else if (ce1.IsFacetWrap() && ce2.IsFacetWrap())
            {
                var fwce1 = (FacetWrapChannelEncoding)ce1;
                var fwce2 = (FacetWrapChannelEncoding)ce2;

                if (!(fwce1.directions.SequenceEqual(fwce2.directions) &&
                      fwce1.spacing.SequenceEqual(fwce2.spacing) &&
                      fwce1.size == fwce2.size &&
                      fwce1.numFacets == fwce2.numFacets))
                    return false;
            }

            return true;
        }
    }
}
