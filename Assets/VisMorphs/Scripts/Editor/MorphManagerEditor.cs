using UnityEngine;
using UnityEditor;

namespace DxR.VisMorphs
{
    [CustomEditor(typeof(MorphManager))]
    [CanEditMultipleObjects]
    public class MorphManagerEditor : Editor
    {
        MorphManager morphManagerScript;

        void OnEnable()
        {
            morphManagerScript = (MorphManager)target;
        }

        public override void OnInspectorGUI()
        {
            if (Application.isPlaying)
            {
                if (GUILayout.Button("Refresh Morphs"))
                {
                    morphManagerScript.ReadMorphJsonSpecifications();
                }
            }

            DrawDefaultInspector();
        }
    }
}