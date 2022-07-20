using System;
using System.Collections;
using System.Collections.Generic;
using SimpleJSON;
using UnityEngine;
using static DxR.VisMorphs.EasingFunction;

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
        public Function EasingFunction;
        public Dictionary<string, Tuple<float, float>> Stages;
    }
}