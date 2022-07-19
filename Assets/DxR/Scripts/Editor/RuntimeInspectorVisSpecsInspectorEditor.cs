using UnityEngine;
using System.Collections;
using UnityEditor;

namespace DxR
{
    [CustomEditor(typeof(RuntimeInspectorVisSpecs))]
    public class RuntimeInspectorVisSpecsInspectorEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            RuntimeInspectorVisSpecs runtimeVis = (RuntimeInspectorVisSpecs)target;
            if (GUILayout.Button("Update Vis"))
            {
                runtimeVis.UpdateVis();
            }

            DrawDefaultInspector();
        }
    }
}