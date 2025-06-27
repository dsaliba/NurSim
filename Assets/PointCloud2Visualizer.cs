using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using System;
using RosMessageTypes.Sensor;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class PointCloud2Visualizer : MonoBehaviour
{
    [Header("ROS Settings")]
    public string pointCloudTopic = "/camera/colored_near_points";

    [Header("Rendering Settings")]
    public int maxPoints = 100000;
    public Material pointMaterial;
    [Range(1f, 20f)]
    public float pointSize = 5f;

    Mesh mesh;
    Vector3[] vertices;
    Color32[] colors;
    int[] indices;

    void Start()
    {
        mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        vertices = new Vector3[maxPoints];
        colors = new Color32[maxPoints];
        indices = new int[maxPoints];
        for (int i = 0; i < maxPoints; i++) indices[i] = i;

        GetComponent<MeshFilter>().mesh = mesh;

        var renderer = GetComponent<MeshRenderer>();
        if (pointMaterial == null)
        {
            Shader shader = Shader.Find("Custom/URPPointCloud");
            if (shader == null)
            {
                Debug.LogError("Shader not found. Please import your URPPointCloud.shader!");
                return;
            }
            pointMaterial = new Material(shader);
        }

        renderer.material = pointMaterial;

        ROSConnection.GetOrCreateInstance().Subscribe<PointCloud2Msg>(pointCloudTopic, PointCloudCallback);
    }

    void Update()
    {
        if (pointMaterial != null)
        {
            pointMaterial.SetFloat("_PointSize", pointSize);
        }
    }

    void PointCloudCallback(PointCloud2Msg msg)
    {
        int pointCount = (int)(msg.width * msg.height);
        if (pointCount == 0 || msg.data.Length == 0)
        {
            mesh.Clear();
            return;
        }

        int xOffset = GetFieldOffset(msg, "x");
        int yOffset = GetFieldOffset(msg, "y");
        int zOffset = GetFieldOffset(msg, "z");
        int rgbOffset = GetFieldOffset(msg, "rgb");
        if (xOffset < 0 || yOffset < 0 || zOffset < 0 || rgbOffset < 0) return;

        int pointStep = (int)msg.point_step;
        int usedPoints = Mathf.Min(pointCount, maxPoints);

        for (int i = 0; i < usedPoints; i++)
        {
            int baseIndex = i * pointStep;
            float x = BitConverter.ToSingle(msg.data, baseIndex + xOffset);
            float y = BitConverter.ToSingle(msg.data, baseIndex + yOffset);
            float z = BitConverter.ToSingle(msg.data, baseIndex + zOffset);

            uint rgb = BitConverter.ToUInt32(msg.data, baseIndex + rgbOffset);
            byte r = (byte)((rgb >> 16) & 0xFF);
            byte g = (byte)((rgb >> 8) & 0xFF);
            byte b = (byte)(rgb & 0xFF);

            vertices[i] = new Vector3(x, y, z);
            colors[i] = new Color32(r, g, b, 255);
        }

        mesh.Clear();
        mesh.vertices = vertices;
        mesh.colors32 = colors;
        mesh.SetIndices(indices, 0, usedPoints, MeshTopology.Points, 0);
        mesh.RecalculateBounds();
    }

    int GetFieldOffset(PointCloud2Msg msg, string name)
    {
        foreach (var field in msg.fields)
            if (field.name == name)
                return (int)field.offset;
        return -1;
    }
}
