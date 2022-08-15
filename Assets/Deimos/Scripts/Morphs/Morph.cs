using System.Collections;
using System.Collections.Generic;
using SimpleJSON;
using UnityEngine;

namespace DxR.Deimos
{
    public class Morph
    {
        public string Name;
        public List<JSONNode> States;
        public List<JSONNode> GlobalSignals;
        public List<JSONNode> LocalSignals;
        public List<JSONNode> Transitions;
        public List<string> SignalNames;

        public Morph()
        {
            States = new List<JSONNode>();
            GlobalSignals = new List<JSONNode>();
            LocalSignals = new List<JSONNode>();
            Transitions = new List<JSONNode>();
            SignalNames = new List<string>();
        }

        public JSONNode GetStateFromName(string name)
        {
            foreach (JSONNode stateSpec in States)
            {
                if (stateSpec["name"] == name)
                    return stateSpec;
            }
            return null;
        }
    }
}