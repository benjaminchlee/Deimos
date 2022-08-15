using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DxR.Deimos
{
    public static class AssetFileCreator
    {
        [MenuItem("Assets/Create/Morphs/DxR Vis Specification", false, 1)]
        private static void CreateDxRVisSpecification()
        {
            ProjectWindowUtil.CreateAssetWithContent(
                "NewVisSpecification.json",
@"{
    ""data"": {
        ""url"": """"
    },
    ""mark"": """",
    ""encoding"": {

    }
}");
        }


        [MenuItem("Assets/Create/Morphs/Morph Specification", false, 1)]
        private static void CreateMorphSpecification()
        {
            ProjectWindowUtil.CreateAssetWithContent(
                "NewMorphSpecification.json",
@"{
    ""$schema"": ""https://raw.githubusercontent.com/benjaminchlee/Deimos/master/Assets/Deimos/Schema/morph-schema.json"",
    ""name"": ""New Morph Specification"",

    ""states"": [
        {
            ""name"": ""first state""
        },
        {
            ""name"": ""second state""
        }
    ],

    ""signals"": [

    ],

    ""transitions"": [
        {
            ""name"": ""transition"",
            ""states"": [""first state"", ""second state""]
        }
    ]
}");
        }

        [MenuItem("GameObject/Morphs/DxR Runtime Vis", false, 1)]
        private static void CreateDxRRuntimeVis(MenuCommand menuCommand)
        {
            GameObject go = Object.Instantiate(Resources.Load("DxRRuntimeVis")) as GameObject;
            go.name = "DxR Runtime Vis";

            // Ensure it gets reparented if this was a context click (otherwise does nothing)
            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);

            // Register the creation in the undo system
            Undo.RegisterCreatedObjectUndo(go, "Create " + go.name);
            Selection.activeObject = go;
        }

        [MenuItem("GameObject/Morphs/DxR Morphable Vis", false, 1)]
        private static void CreateDxRMorphableVis(MenuCommand menuCommand)
        {
            GameObject go = Object.Instantiate(Resources.Load("DxRMorphableVis")) as GameObject;
            go.name = "DxR Morphable Vis";

            // Ensure it gets reparented if this was a context click (otherwise does nothing)
            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);

            // Register the creation in the undo system
            Undo.RegisterCreatedObjectUndo(go, "Create " + go.name);
            Selection.activeObject = go;
        }

        [MenuItem("GameObject/Morphs/Morph Manager", false, 1)]
        private static void CreateMorphManager(MenuCommand menuCommand)
        {
            GameObject go = Object.Instantiate(Resources.Load("MorphManager")) as GameObject;
            go.name = "Morph Manager";

            // Ensure it gets reparented if this was a context click (otherwise does nothing)
            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);

            // Register the creation in the undo system
            Undo.RegisterCreatedObjectUndo(go, "Create " + go.name);
            Selection.activeObject = go;
        }
    }

}