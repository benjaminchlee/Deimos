// using UnityEngine;
// using UnityEditor;

// namespace DxR.VisMorphs
// {
//     [CustomEditor(typeof(MorphManager))]
//     [CanEditMultipleObjects]
//     public class MorphManagerEditor : Editor
//     {
//         MorphManager morphManagerScript;
//         SerializedProperty json;

//         void OnEnable()
//         {
//             morphManagerScript = (MorphManager)target;
//             json = serializedObject.FindProperty("Json");
//         }

//         public override void OnInspectorGUI()
//         {
//             serializedObject.Update();

//             if (GUILayout.Button("Edit JSON"))
//             {
//                 var updatedJson = EditorInputDialog.Show("Edit JSON", "", json.stringValue);
//                 json.stringValue = updatedJson;
//                 morphManagerScript.TransformationJsonUpdated();
//             }

//             EditorGUILayout.TextArea( json.stringValue );

//             serializedObject.ApplyModifiedProperties();
//         }
//     }
// }