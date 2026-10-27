using UnityEditor;
using UnityEngine;

namespace GeoWorld.Editor
{
    // Settings for Nature Renderer are shown only where the project has it.
    [CustomPropertyDrawer(typeof(GeoNatureRendererOnlyAttribute))]
    public class GeoNatureRendererOnlyDrawer : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) =>
            GeoNatureRendererTrees.Installed ? EditorGUI.GetPropertyHeight(property, label, true) : -EditorGUIUtility.standardVerticalSpacing;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (GeoNatureRendererTrees.Installed) EditorGUI.PropertyField(position, property, label, true);
        }
    }
}
