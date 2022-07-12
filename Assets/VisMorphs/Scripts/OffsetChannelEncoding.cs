using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DxR
{
    public class OffsetChannelEncoding : ChannelEncoding
    {
        public string linkedChannel;    // Name of the Channel which this one is linked to
        public List<string> values;     // Special values that can get passed onto marks
                                        // Hacky solution for encodings like offsets

        public OffsetChannelEncoding() : base()
        {
            values = new List<string>();
        }
    }
}