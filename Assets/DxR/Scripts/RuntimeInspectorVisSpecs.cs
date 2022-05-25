using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using SimpleJSON;
using System.IO;
using Newtonsoft.Json;

namespace DxR
{
    /// <summary>
    /// This component can be attached to any GameObject (_parentObject) that already
    /// has a Vis component on it. This component acts as an alternative method to modify
    /// a Vis' JSON specification by doing so within the Unity Inspector at runtime,
    /// rather than from a .json asset file. All similar rules for DxR Vis specifications
    /// still apply.
    /// </summary>
    [RequireComponent(typeof(Vis))]
    public class RuntimeInspectorVisSpecs : MonoBehaviour
    {
        [TextArea(4, 20)]
        public string JsonSpecification;

        private Vis vis;

        private void Awake()
        {
            vis = GetComponent<Vis>();
            vis.VisUpdated.AddListener(UpdateRuntimeSpecs);
        }

        private void OnValidate()
        {
            if (EditorApplication.isPlaying && vis != null)
            {
                if (vis.IsReady)
                {
                    vis.UpdateVisSpecsFromStringSpecs(JsonSpecification);
                }
            }
        }

        private void UpdateRuntimeSpecs(Vis vis, JSONNode visSpecs)
        {
            using (var stringReader = new StringReader(visSpecs.ToString()))
            using (var stringWriter = new StringWriter())
            {
                var jsonReader = new JsonTextReader(stringReader);
                var jsonWriter = new JsonTextWriter(stringWriter) { Formatting = Formatting.Indented };
                jsonWriter.WriteToken(jsonReader);
                JsonSpecification = stringWriter.ToString();
            }

        }
    }
}