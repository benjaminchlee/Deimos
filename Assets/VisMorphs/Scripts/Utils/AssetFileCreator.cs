using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DxR.VisMorphs
{
    public static class AssetFileCreator
    {
        [MenuItem("Assets/Create/Morphs/Morph Specification", false, 1)]
        private static void CreateNewAsset()
        {
            ProjectWindowUtil.CreateAssetWithContent(
                "NewMorphSpecification.json",
@"{
    ""$schema"": ""https://raw.githubusercontent.com/benjaminchlee/DxR/master/Assets/VisMorphs/Schema/morph-schema.json"",
    ""name"": ""NewMorphSpecification"",

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
    }
}