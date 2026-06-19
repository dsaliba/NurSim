#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
///     Adds a "Save Current Position As Preset" control to the
///     VirtualKortexDriver inspector for use during play mode testing.
/// </summary>
[CustomEditor(typeof(VirtualKortexDriver))]
public class VirtualKortexDriverEditor : Editor
{
    private string newPresetName = "";

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        VirtualKortexDriver driver = (VirtualKortexDriver)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Save Current Position", EditorStyles.boldLabel);

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox(
                "Enter Play Mode to save the arm's current joint positions as a preset.",
                MessageType.Info
            );
            return;
        }

        EditorGUILayout.BeginHorizontal();
        newPresetName = EditorGUILayout.TextField("Preset Name", newPresetName);
        GUI.enabled = !string.IsNullOrWhiteSpace(newPresetName);
        if (GUILayout.Button("Save", GUILayout.Width(60)))
        {
            driver.SaveCurrentPositionAsPreset(newPresetName);
            newPresetName = "";
        }
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();
    }
}
#endif
