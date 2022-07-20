using System;
using System.Collections;
using System.Collections.Generic;
using SimpleJSON;
using UnityEngine;

namespace DxR.VisMorphs
{
    public class ActiveTransition
    {
        public string Name;
        public List<Tuple<ChannelEncoding, ChannelEncoding, string>> ChangedChannelEncodings;
        public JSONNode InitialVisSpecs;
        public JSONNode FinalVisSpecs;
        public JSONNode InitialInferredVisSpecs;
        public JSONNode FinalInferredVisSpecs;
        public IObservable<float> TweeningObservable;
        public Dictionary<string, Tuple<float, float>> Stages;
    }
}