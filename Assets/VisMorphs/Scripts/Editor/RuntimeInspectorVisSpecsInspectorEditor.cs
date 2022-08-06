using UnityEngine;
using System.Collections;
using UnityEditor;

namespace DxR
{
    [CustomEditor(typeof(RuntimeInspectorVisSpecs))]
    public class RuntimeInspectorVisSpecsInspectorEditor : Editor
    {
        public SerializedProperty jsonSpecificationProperty;
        public SerializedProperty inputSpecificationProperty;
        RuntimeInspectorVisSpecs runtimeVisScript;

        private void OnEnable()
        {
            runtimeVisScript = (RuntimeInspectorVisSpecs)target;

            jsonSpecificationProperty = serializedObject.FindProperty("JSONSpecification");
            inputSpecificationProperty = serializedObject.FindProperty("InputSpecification");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Input Specification");
            if (Application.isPlaying && GUILayout.Button("Update Vis"))
            {
                runtimeVisScript.UpdateVis();
            }
            GUILayout.EndHorizontal();

            jsonSpecificationProperty.objectReferenceValue = (TextAsset)EditorGUILayout.ObjectField("", jsonSpecificationProperty.objectReferenceValue, typeof(TextAsset), true);

            if (runtimeVisScript.JSONSpecification == null)
            {
                inputSpecificationProperty.stringValue = EditorGUILayout.TextArea(inputSpecificationProperty.stringValue, GUILayout.MinHeight(40), GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
            }

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Internal Specification");
            if (Application.isPlaying && runtimeVisScript.JSONSpecification == null && GUILayout.Button("Copy Internal Specification to Input"))
            {
                inputSpecificationProperty.stringValue = runtimeVisScript.InternalSpecification;
            }
            GUILayout.EndHorizontal();

            EditorGUI.BeginDisabledGroup(true);
            if (runtimeVisScript.InternalSpecification != "")
            {
                EditorGUILayout.TextArea(runtimeVisScript.InternalSpecification, GUILayout.MinHeight(40), GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
            }
            else
            {
                EditorGUILayout.TextArea("The specification of this Vis that is currently being used internally will be shown here during runtime.", GUILayout.MinHeight(40), GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
            }
            EditorGUI.EndDisabledGroup();

            serializedObject.ApplyModifiedProperties();
        }
    }
}